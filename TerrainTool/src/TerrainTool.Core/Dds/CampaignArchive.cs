using System.Text;

namespace TerrainTool.Core.Dds;

/// <summary>
/// Archivi della campagna di Falcon (.tac/.cam): u32 posizione della directory, poi i file, poi la directory
/// (u32 numero; per ogni file: u8 lunghezza nome, nome, u32 posizione, u32 dimensione).
/// Il file .obj (obiettivi) e' compresso con l'LZSS di Falcon: intestazione i16 numero obiettivi, i32 dimensione
/// espansa, i32 dimensione compressa. In scrittura si usa un LZSS di soli letterali (piu' grande ma sempre valido).
/// </summary>
public static class CampaignArchive
{
    public static List<(string Name, byte[] Data)> Read(string path)
    {
        var b = File.ReadAllBytes(path);
        int dir = BitConverter.ToInt32(b, 0), n = BitConverter.ToInt32(b, dir), p = dir + 4;
        var list = new List<(string, byte[])>();
        for (int i = 0; i < n; i++)
        {
            int len = b[p];
            string name = Encoding.Latin1.GetString(b, p + 1, len);
            int off = BitConverter.ToInt32(b, p + 1 + len), size = BitConverter.ToInt32(b, p + 5 + len);
            p += 9 + len;
            list.Add((name, b.AsSpan(off, size).ToArray()));
        }
        return list;
    }

    public static void Write(string path, List<(string Name, byte[] Data)> files)
    {
        files = MatchNames(files, Path.GetFileNameWithoutExtension(path));
        using var ms = new MemoryStream();
        ms.Write(new byte[4]);
        var dir = new List<(string, int, int)>();
        foreach (var (name, data) in files) { dir.Add((name, (int)ms.Position, data.Length)); ms.Write(data); }
        int dirPos = (int)ms.Position;
        ms.Write(BitConverter.GetBytes(dir.Count));
        foreach (var (name, off, size) in dir)
        {
            var nb = Encoding.Latin1.GetBytes(name);
            ms.WriteByte((byte)nb.Length); ms.Write(nb); ms.Write(BitConverter.GetBytes(off)); ms.Write(BitConverter.GetBytes(size));
        }
        var all = ms.ToArray();
        BitConverter.TryWriteBytes(all.AsSpan(0, 4), dirPos);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, all);
    }

    /// <summary>
    /// Nei file della Korea i file interni hanno il nome dell'archivio (Te_New_Nt.tac → Te_New_Nt.cmp, Te_New_Nt.obj ...): se tutti
    /// hanno un altro nome (te_new.* copiati in Te_New_Nt.tac) si rinominano. OpenCampFile di BMS 4.38 confronta solo la parte dal
    /// primo punto in poi (strchr '.', _stricmp): con nomi senza punti basta l'estensione, ma se il nome dell'archivio ha un punto
    /// (es. "Op.Telic.tac" cerca ".Telic.tea") i file interni devono avere lo stesso nome, ed e' cio' che questa funzione garantisce.
    /// </summary>
    public static List<(string Name, byte[] Data)> MatchNames(List<(string Name, byte[] Data)> files, string baseName)
    {
        if (baseName.Length == 0 || files.Count == 0) return files;
        var bases = files.Select(f => Path.GetFileNameWithoutExtension(f.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (bases.Count != 1 || bases[0].Equals(baseName, StringComparison.OrdinalIgnoreCase)) return files;
        return files.Select(f => (baseName + Path.GetExtension(f.Name), f.Data)).ToList();
    }

    /// <summary>Copia un archivio della campagna con un altro nome, rinominando anche i file interni.</summary>
    public static void CopyAs(string src, string dst) => Write(dst, Read(src));

    /// <summary>LZSS di Falcon: byte di flag letto dal bit basso (1 = letterale); coppia a,b: lunghezza (a&gt;&gt;4)+2,
    /// posizione assoluta ((a&amp;15)&lt;&lt;8)|b in una finestra di 4096 byte a zero che si riempie da 1.</summary>
    public static byte[] Expand(ReadOnlySpan<byte> src, int size)
    {
        const int N = 4096;
        var win = new byte[N]; int r = 1, i = 0;
        var outp = new byte[size]; int o = 0;
        while (o < size && i < src.Length)
        {
            int flags = src[i++];
            for (int k = 0; k < 8 && o < size && i < src.Length; k++)
            {
                if (((flags >> k) & 1) != 0) { byte c = src[i++]; outp[o++] = c; win[r] = c; r = (r + 1) & (N - 1); }
                else
                {
                    if (i + 1 >= src.Length) break;
                    int a = src[i], bb = src[i + 1]; i += 2;
                    int len = (a >> 4) + 2, pos = ((a & 15) << 8) | bb;
                    for (int q = 0; q < len && o < size; q++) { byte c = win[(pos + q) & (N - 1)]; outp[o++] = c; win[r] = c; r = (r + 1) & (N - 1); }
                }
            }
        }
        return outp;
    }

    public static byte[] CompressLiteral(ReadOnlySpan<byte> d)
    {
        var o = new byte[d.Length + (d.Length + 7) / 8];
        int p = 0;
        for (int i = 0; i < d.Length; i += 8)
        {
            int n = Math.Min(8, d.Length - i);
            o[p++] = (byte)((1 << n) - 1);
            d.Slice(i, n).CopyTo(o.AsSpan(p)); p += n;
        }
        return o;
    }

    /// <param name="Convert">true = l'obiettivo cambia natura (base tolta → incrocio): NewType e' il nuovo indice CT, il numero
    /// di oggetti si copia da un record che ha gia' quel tipo, radar spento, nome NewName. Solo negli archivi senza unita'.</param>
    /// <param name="NewFeatures">disposizione nuova con un altro numero di pezzi (base coreana al posto della base): il record
    /// si rifa' con NewFeatures byte di stato a 0 (tutto intatto), tipo NewType; collegamenti, radar e nome restano. In tutti gli archivi.</param>
    public sealed record Move(string Name, double OldX, double OldY, double NewX, double NewY, double HeadingDeg, int NewType = -1, int CampId = -1,
                              bool Convert = false, string? NewName = null, int NewFeatures = -1);

    /// <summary>Posizioni (byte) dei campi di un record obiettivo nel file .obj espanso.</summary>
    public sealed record ObjRecord(int Start, int End, int CampId, int FeatureCountAt, int PosAt, string Name);

    /// <summary>
    /// Formato del record obiettivo (BMS 4.38, verificato su tutto te_new.tac del Mideast: 4046 record letti fino all'ultimo
    /// byte): i16 tipo (indice CT + 100), VU_ID (u32 numero, u32 creatore), i16 tipo, i16 x griglia (est km), i16 y griglia
    /// (nord km), 14 byte, u8 squadra, i16 CampId (quello di CampObjData.XML), u32 ultima riparazione, u32 flag, u8 rifornimenti,
    /// u8 carburante, u8 perdite, u8 numero oggetti N, N byte di stato degli oggetti, u8 priorita', i16 nome, VU_ID padre,
    /// u8 primo proprietario, u8 numero collegamenti L, L × (8 byte costi per tipo di movimento + VU_ID del vicino),
    /// u8 radar (1 = seguono 8 float), double X (nord ft), double Y (est ft), float, float, float direzione (rad), nome 80 byte.
    /// </summary>
    public static List<ObjRecord> ParseObjectives(byte[] d, int count)
    {
        var res = new List<ObjRecord>(count);
        int p = 0;
        for (int k = 0; k < count; k++)
        {
            int s = p;
            int campId = BitConverter.ToInt16(d, p + 29);
            int fcAt = p + 42, fc = d[fcAt];
            int q = fcAt + 1 + fc;
            q += 1 + 2 + 8 + 1;                // priorita', nome, padre, primo proprietario
            int nl = d[q]; q += 1 + nl * 16;   // collegamenti
            int radar = d[q]; q += 1 + (radar != 0 ? 32 : 0);
            int posAt = q;
            q += 16 + 12;
            string name = Encoding.Latin1.GetString(d, q, 80).Split('\0')[0];
            q += 80;
            res.Add(new ObjRecord(s, q, campId, fcAt, posAt, name));
            p = q;
        }
        if (p != d.Length) throw new InvalidDataException($"objective file: {p} of {d.Length} bytes read");
        return res;
    }

    /// <summary>
    /// Sposta gli obiettivi nel file .obj espanso: il record si trova dal CampId (se c'e' nella mossa) e si controlla il
    /// nome; altrimenti dal nome e dalla vecchia posizione. Scrive x/y di griglia, X/Y in piedi, direzione e, se richiesto,
    /// il tipo. Lunghezza del file invariata.
    /// </summary>
    public static List<string> PatchObjectives(ref byte[] data, int count, IEnumerable<Move> moves, bool allowConvert = true)
    {
        var d = data;
        var conv = new List<(ObjRecord R, Move M)>();
        var log = new List<string>();
        List<ObjRecord> recs;
        try { recs = ParseObjectives(d, count); }
        catch (Exception ex) { log.Add("objective file not readable: " + ex.Message); return log; }
        var byId = recs.GroupBy(r => r.CampId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var m in moves)
        {
            ObjRecord? r = null;
            if (m.CampId >= 0 && byId.TryGetValue(m.CampId, out var l)) r = l.FirstOrDefault(x => x.Name == m.Name) ?? (l.Count == 1 ? l[0] : null);
            r ??= recs.FirstOrDefault(x => x.Name == m.Name && Math.Abs(BitConverter.ToDouble(d, x.PosAt) - m.OldX) < 5 && Math.Abs(BitConverter.ToDouble(d, x.PosAt + 8) - m.OldY) < 5);
            if (r == null) { log.Add($"{m.Name}: not found"); continue; }
            if (m.Convert && !allowConvert) continue;   // archivio con unita': la base resta com'e'
            if (m.Convert || (m.NewFeatures >= 0 && m.NewType >= 0)) conv.Add((r, m));
            BitConverter.TryWriteBytes(d.AsSpan(r.Start + 12, 2), (short)TerrainTool.Core.Campaign.CampaignDoc.SimToGrid(m.NewY));
            BitConverter.TryWriteBytes(d.AsSpan(r.Start + 14, 2), (short)TerrainTool.Core.Campaign.CampaignDoc.SimToGrid(m.NewX));
            BitConverter.TryWriteBytes(d.AsSpan(r.PosAt, 8), m.NewX);
            BitConverter.TryWriteBytes(d.AsSpan(r.PosAt + 8, 8), m.NewY);
            BitConverter.TryWriteBytes(d.AsSpan(r.PosAt + 24, 4), (float)(m.HeadingDeg * Math.PI / 180));
            // tipo dell'obiettivo = indice nella Falcon4_CT + 100 (VU_LAST_ENTITY_TYPE), all'inizio e 10 byte dopo
            if (m.NewType >= 0 && !m.Convert && m.NewFeatures < 0)
            {
                short t = (short)(m.NewType + 100);
                BitConverter.TryWriteBytes(d.AsSpan(r.Start, 2), t);
                BitConverter.TryWriteBytes(d.AsSpan(r.Start + 10, 2), t);
            }
        }
        if (conv.Count > 0) data = ConvertRecords(d, recs, conv, log);
        return log;
    }

    /// <summary>
    /// Cambia natura agli obiettivi (base → incrocio): N oggetti e byte di stato presi da un record che ha gia' il tipo nuovo,
    /// radar a 0 (senza gli 8 float), nome nuovo. Collegamenti, VU_ID, proprietario e posizione restano. Il file cambia lunghezza:
    /// i record si rifanno dall'ultimo al primo, cosi' le posizioni lette prima restano valide.
    /// </summary>
    private static byte[] ConvertRecords(byte[] d, List<ObjRecord> recs, List<(ObjRecord R, Move M)> conv, List<string> log)
    {
        var buf = d.ToList();
        foreach (var (r, m) in conv.OrderByDescending(x => x.R.Start))
        {
            short t = (short)(m.NewType + 100);
            byte[] status;
            if (m.Convert)
            {
                var donor = recs.FirstOrDefault(x => BitConverter.ToInt16(d, x.Start) == t && !conv.Any(c => c.R == x));
                if (donor == null) { log.Add($"{m.Name}: no objective of type {m.NewType} to copy the features from, not converted"); continue; }
                status = d.AsSpan(donor.FeatureCountAt + 1, d[donor.FeatureCountAt]).ToArray();
            }
            else
            {
                if (m.NewFeatures > 255) { log.Add($"{m.Name}: {m.NewFeatures} pieces (more than 255), not changed"); continue; }
                status = new byte[m.NewFeatures];   // tutti intatti
            }
            int nf = status.Length;
            int fc = d[r.FeatureCountAt];
            int q = r.FeatureCountAt + 1 + fc + 12;
            int nl = d[q];
            int radarAt = q + 1 + nl * 16;
            int afterRadar = radarAt + 1 + (d[radarAt] != 0 ? 32 : 0);
            var rec = new List<byte>();
            rec.AddRange(d.AsSpan(r.Start, r.FeatureCountAt - r.Start).ToArray());
            rec.Add((byte)nf); rec.AddRange(status);
            rec.AddRange(d.AsSpan(r.FeatureCountAt + 1 + fc, radarAt - (r.FeatureCountAt + 1 + fc)).ToArray());
            if (m.Convert) { rec.Add(0); rec.AddRange(d.AsSpan(afterRadar, r.End - afterRadar).ToArray()); }   // incrocio: niente radar
            else rec.AddRange(d.AsSpan(radarAt, r.End - radarAt).ToArray());
            var a = rec.ToArray();
            BitConverter.TryWriteBytes(a.AsSpan(0, 2), t);
            BitConverter.TryWriteBytes(a.AsSpan(10, 2), t);
            if (m.NewName != null)
            {
                var nb = new byte[80];
                var src = Encoding.Latin1.GetBytes(m.NewName);
                Array.Copy(src, nb, Math.Min(79, src.Length));
                nb.CopyTo(a, a.Length - 80);
            }
            buf.RemoveRange(r.Start, r.End - r.Start);
            buf.InsertRange(r.Start, a);
        }
        var res = buf.ToArray();
        ParseObjectives(res, recs.Count);   // controllo: il file deve rileggersi fino all'ultimo byte
        return res;
    }

    private static int Next(byte[] d, byte[] nb, int from)
    {
        if (from >= d.Length) return -1;
        int j = d.AsSpan(from).IndexOf(nb);
        return j < 0 ? -1 : from + j;
    }

    /// <summary>Tipo (indice CT) e numero di oggetti di ogni record obiettivo dell'archivio, per CampId; null se non ha obiettivi.</summary>
    public static Dictionary<int, (int Ct, int Features)>? ObjectiveTypes(string path)
    {
        var files = Read(path);
        int idx = files.FindIndex(f => f.Name.EndsWith(".obj", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return null;
        var data = files[idx].Data;
        short count = BitConverter.ToInt16(data, 0);
        var raw = Expand(data.AsSpan(10), BitConverter.ToInt32(data, 2));
        var res = new Dictionary<int, (int, int)>();
        foreach (var r in ParseObjectives(raw, count)) res.TryAdd(r.CampId, (BitConverter.ToInt16(raw, r.Start) - 100, raw[r.FeatureCountAt]));
        return res;
    }

    /// <summary>
    /// Rimette a posto il tipo (indice CT) degli obiettivi di un archivio: per ogni record si provano i candidati del suo CampId
    /// (prima il tipo atteso dalla CampObjData finale, poi quello della disposizione originale) e si prende il primo che ha lo
    /// stesso numero di oggetti del record (featuresOfCt: quanti oggetti ha quel tipo negli archivi giusti). Serve quando un giro
    /// precedente di Objects aveva dato a un obiettivo una copia della disposizione (nuovo CT) che nel giro attuale non c'e' piu':
    /// il vecchio CT resta nei salvataggi e BMS si ferma caricando la campagna (CT inesistente). Restituisce le note, null se
    /// l'archivio non ha obiettivi; riscrive il file solo se cambia qualcosa.
    /// </summary>
    public static List<string>? SyncObjectiveTypes(string path, Func<int, IReadOnlyList<int>> candidates, IReadOnlyDictionary<int, int> featuresOfCt, int ctCount)
    {
        var files = Read(path);
        int idx = files.FindIndex(f => f.Name.EndsWith(".obj", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return null;
        var data = files[idx].Data;
        short count = BitConverter.ToInt16(data, 0);
        var raw = Expand(data.AsSpan(10), BitConverter.ToInt32(data, 2));
        var log = new List<string>();
        int fixedN = 0, gridN = 0;
        foreach (var r in ParseObjectives(raw, count))
        {
            // cella della griglia come la calcola BMS (SimToGrid, 3279.98 ft) dalla posizione in piedi
            short gx = (short)TerrainTool.Core.Campaign.CampaignDoc.SimToGrid(BitConverter.ToDouble(raw, r.PosAt + 8));
            short gy = (short)TerrainTool.Core.Campaign.CampaignDoc.SimToGrid(BitConverter.ToDouble(raw, r.PosAt));
            if (BitConverter.ToInt16(raw, r.Start + 12) != gx || BitConverter.ToInt16(raw, r.Start + 14) != gy)
            {
                BitConverter.TryWriteBytes(raw.AsSpan(r.Start + 12, 2), gx);
                BitConverter.TryWriteBytes(raw.AsSpan(r.Start + 14, 2), gy);
                gridN++;
            }
            int cur = BitConverter.ToInt16(raw, r.Start) - 100, fc = raw[r.FeatureCountAt];
            var cands = candidates(r.CampId);
            if (cands.Count > 0 && cands[0] == cur) continue;
            int pick = -1;
            foreach (int c in cands)
                if (c >= 0 && c < ctCount && featuresOfCt.TryGetValue(c, out int n) && n == fc) { pick = c; break; }
            if (pick >= 0 && pick != cur)
            {
                short t = (short)(pick + 100);
                BitConverter.TryWriteBytes(raw.AsSpan(r.Start, 2), t);
                BitConverter.TryWriteBytes(raw.AsSpan(r.Start + 10, 2), t);
                fixedN++;
                if (cur >= ctCount || cur < 0) log.Add($"{r.Name} (CampId {r.CampId}): CT {cur} does not exist, now {pick}");
            }
            else if (pick < 0 && (cur >= ctCount || cur < 0))
                log.Add($"{r.Name} (CampId {r.CampId}): CT {cur} does not exist and no layout with {fc} pieces fits: BMS will stop loading this file");
        }
        if (fixedN == 0 && gridN == 0) return log;
        var c2 = CompressLiteral(raw);
        var nd = new byte[10 + c2.Length];
        BitConverter.TryWriteBytes(nd.AsSpan(0, 2), count);
        BitConverter.TryWriteBytes(nd.AsSpan(2, 4), raw.Length);
        BitConverter.TryWriteBytes(nd.AsSpan(6, 4), c2.Length);
        c2.CopyTo(nd, 10);
        files[idx] = (files[idx].Name, nd);
        Write(path, files);
        log.Insert(0, $"{fixedN} objective types set from the current layouts, {gridN} grid cells recomputed as BMS does (3279.98 ft per cell)");
        return log;
    }

    /// <summary>Copia un archivio spostando gli obiettivi del suo .obj. Restituisce null se l'archivio non ha obiettivi.</summary>
    public static List<string>? PatchArchive(string src, string dst, IEnumerable<Move> moves)
    {
        var files = Read(src);
        int idx = files.FindIndex(f => f.Name.EndsWith(".obj", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return null;
        var data = files[idx].Data;
        short count = BitConverter.ToInt16(data, 0);
        int exp = BitConverter.ToInt32(data, 2);
        var raw = Expand(data.AsSpan(10), exp);
        // archivi con unita' (campagne dinamiche, missioni TE): le basi tolte restano basi, le unita' le usano
        int ui = files.FindIndex(f => f.Name.EndsWith(".uni", StringComparison.OrdinalIgnoreCase));
        int units = ui >= 0 && files[ui].Data.Length >= 10 ? BitConverter.ToUInt16(files[ui].Data, 4) : 0;
        var log = PatchObjectives(ref raw, count, moves, units == 0);
        int nconv = moves.Count(m => m.Convert);
        if (nconv > 0) log.Add(units == 0 ? $"{nconv} objectives (airbases, bridges) converted to junctions" : $"has {units} units: the {nconv} removed objectives stay as they are here");
        var c = CompressLiteral(raw);
        var nd = new byte[10 + c.Length];
        BitConverter.TryWriteBytes(nd.AsSpan(0, 2), count);
        BitConverter.TryWriteBytes(nd.AsSpan(2, 4), raw.Length);
        BitConverter.TryWriteBytes(nd.AsSpan(6, 4), c.Length);
        c.CopyTo(nd, 10);
        files[idx] = (files[idx].Name, nd);
        Write(dst, files);
        return log;
    }
}
