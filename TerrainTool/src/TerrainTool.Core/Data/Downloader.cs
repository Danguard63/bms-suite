using System.Net;
using System.Net.Http.Headers;

namespace TerrainTool.Core.Data;

public enum DownloadState { Pending, Skipped, Done, NotAvailable, Failed, Cancelled }

public sealed class DownloadResult
{
    public required DownloadItem Item { get; init; }
    public DownloadState State { get; set; }
    public long Bytes { get; set; }
    public string Message { get; set; } = "";
}

public sealed class DownloadProgress
{
    public int FilesDone { get; init; }
    public int FilesTotal { get; init; }
    public long BytesReceived { get; init; }
    public string Current { get; init; } = "";
    public string? Line { get; init; }
}

/// <summary>
/// Scarica una lista di file nella cartella del progetto.
/// - salta i file già completi;
/// - riprende i download interrotti (file .part + richiesta Range);
/// - un 404 su un file che può non esistere (tile in mare) non è un errore;
/// - scrive su .part e rinomina solo a download finito, così un file presente è sempre completo.
/// </summary>
public sealed class Downloader : IDisposable
{
    private readonly HttpClient _http;
    public int Parallel { get; set; } = 4;
    public int Retries { get; set; } = 3;

    public Downloader(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // file grandi: il timeout lo gestiamo per lettura
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BmsTerrainTool/0.2");
    }

    public void Dispose() => _http.Dispose();

    public async Task<List<DownloadResult>> RunAsync(string projectFolder, IReadOnlyList<DownloadItem> items,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var results = items.Select(i => new DownloadResult { Item = i }).ToList();
        int done = 0;
        long bytes = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, Parallel));
        var tasks = results.Select(async r =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await DownloadOneAsync(projectFolder, r, b =>
                {
                    long tot = Interlocked.Add(ref bytes, b);
                    progress?.Report(new DownloadProgress { FilesDone = Volatile.Read(ref done), FilesTotal = results.Count, BytesReceived = tot, Current = Path.GetFileName(r.Item.RelPath) });
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { r.State = DownloadState.Cancelled; }
            catch (Exception ex) { r.State = DownloadState.Failed; r.Message = ex.Message; }
            finally
            {
                gate.Release();
                int d = Interlocked.Increment(ref done);
                progress?.Report(new DownloadProgress
                {
                    FilesDone = d, FilesTotal = results.Count, BytesReceived = Interlocked.Read(ref bytes),
                    Current = Path.GetFileName(r.Item.RelPath), Line = Describe(r),
                });
            }
        }).ToList();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* gli stati sono già impostati */ }
        foreach (var r in results.Where(r => r.State == DownloadState.Pending)) r.State = DownloadState.Cancelled;
        return results;
    }

    public static string Describe(DownloadResult r)
    {
        string name = Path.GetFileName(r.Item.RelPath);
        return r.State switch
        {
            DownloadState.Done => $"Downloaded  {name}  ({FormatBytes(r.Bytes)})",
            DownloadState.Skipped => $"Already present  {name}",
            DownloadState.NotAvailable => $"Not available (sea / no data)  {name}",
            DownloadState.Failed => $"FAILED  {name}: {r.Message}",
            DownloadState.Cancelled => $"Cancelled  {name}",
            _ => name,
        };
    }

    private async Task DownloadOneAsync(string root, DownloadResult r, Action<long> onBytes, CancellationToken ct)
    {
        string dest = Path.Combine(root, r.Item.RelPath);
        string part = dest + ".part";
        string missingMarker = dest + ".notavailable";
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        if (File.Exists(dest) && new FileInfo(dest).Length > 0) { r.State = DownloadState.Skipped; r.Bytes = new FileInfo(dest).Length; return; }
        if (File.Exists(missingMarker)) { r.State = DownloadState.NotAvailable; return; }

        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                using var req = new HttpRequestMessage(HttpMethod.Get, r.Item.Url);
                if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.NotFound || (resp.StatusCode == HttpStatusCode.Forbidden && r.Item.MayNotExist))
                {
                    // S3 risponde 403/404 per gli oggetti che non esistono
                    if (r.Item.MayNotExist)
                    {
                        r.State = DownloadState.NotAvailable;
                        await File.WriteAllTextAsync(missingMarker, r.Item.Url, ct).ConfigureAwait(false);
                        return;
                    }
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {r.Item.Url}");
                }
                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Il .part è già completo (o corrotto): ricomincia da zero
                    File.Delete(part);
                    continue;
                }
                resp.EnsureSuccessStatusCode();

                bool resumed = resp.StatusCode == HttpStatusCode.PartialContent;
                if (!resumed) have = 0; // il server ha ignorato Range: riparte da capo

                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var fs = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
                {
                    var buf = new byte[1 << 20];
                    while (true)
                    {
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        readCts.CancelAfter(TimeSpan.FromSeconds(120)); // nessun dato per 2 minuti = connessione morta
                        int n = await src.ReadAsync(buf, readCts.Token).ConfigureAwait(false);
                        if (n == 0) break;
                        await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        onBytes(n);
                    }
                }

                long expected = (resp.Content.Headers.ContentLength ?? -1) + (resumed ? have : 0);
                long got = new FileInfo(part).Length;
                if (resp.Content.Headers.ContentLength.HasValue && got != expected)
                    throw new IOException($"incomplete file ({got} of {expected} bytes)");

                if (File.Exists(dest)) File.Delete(dest);
                File.Move(part, dest);
                r.State = DownloadState.Done;
                r.Bytes = got;
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (attempt < Retries)
            {
                r.Message = ex.Message;
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    public static string FormatBytes(long b)
    {
        double v = b;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.0} {u[i]}";
    }
}
