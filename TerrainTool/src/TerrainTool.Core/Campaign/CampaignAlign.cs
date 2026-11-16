using System.Text;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// File della cartella Campaign del vecchio add-on (fatto per la 4.37) da mettere in pari con la Korea di BMS 4.38,
/// tenendo solo cio' che e' davvero del teatro. Usato da Assemble (scrive in Output\Install, mai in BMS).
/// </summary>
public static class CampaignAlign
{
    /// <summary>Uguali per ogni teatro: si prendono dalla Korea 4.38.
    /// I .db sono gli script dell'impaginazione del debriefing (colonne #TAB spostate in 4.38); ECM_Def.ini manca nel vecchio add-on.</summary>
    public static readonly string[] FromKorea = { "Element.db", "PElement.db", "PHeader.db", "Pilot.db", "ECM_Def.ini" };

    /// <summary>
    /// Ppt.ini (nomi e raggi delle minacce e dei punti di navigazione pre-pianificati): la parte comune dalla Korea
    /// (minacce SA-x, tipi di steerpoint della 4.38, N/E/S/W, A/B/C/D, ALT e DIV), poi i nomi locali del teatro presi dal file del
    /// vecchio add-on: le righe dopo il separatore "SP4", fino alla prima radioassistenza coreana (VORTAC) rimasta dalla Korea vecchia.
    /// Restituisce null se i due file non hanno la forma attesa.
    /// </summary>
    public static string? MergePpt(string koreaPpt, string oldPpt, out int local)
    {
        local = 0;
        var k = File.ReadAllLines(koreaPpt, Encoding.Latin1).ToList();
        var m = File.ReadAllLines(oldPpt, Encoding.Latin1).ToList();
        static string Code(string l) { var p = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); return p.Length > 0 ? p[0] : ""; }
        int kd = k.FindIndex(l => Code(l) == "D");
        int ms = m.FindIndex(l => Code(l) == "SP4");
        if (kd < 0 || ms < 0) return null;
        var outL = k.Take(kd + 1).ToList();
        outL.AddRange(k.Skip(kd + 1).Where(l => Code(l) is "ALT" or "DIV"));
        var have = outL.Select(Code).ToHashSet();
        foreach (var l in m.Skip(ms + 1))
        {
            if (l.Contains("VORTAC", StringComparison.OrdinalIgnoreCase)) break;
            string c = Code(l);
            if (c.Length == 0 || c.StartsWith("SP") || have.Contains(c)) continue;
            outL.Add(l.TrimEnd());
            have.Add(c);
            local++;
        }
        return string.Join("\r\n", outL) + "\r\n";
    }

    /// <summary>
    /// Raggi delle minacce del Ppt.ini cambiati dal progetto (PptEdits.txt: righe "CODICE km  # commento", es. "PAT 160"):
    /// i cerchi che si mettono sulla mappa nella pianificazione della missione, allineati alle gittate corrette dei SAM.
    /// Nel Ppt.ini il raggio e' in piedi. Errore se un codice non c'e'.
    /// </summary>
    public static string ApplyPptEdits(string ppt, string editsFile, List<string> notes)
    {
        if (!File.Exists(editsFile)) return ppt;
        var lines = ppt.Replace("\r\n", "\n").Split('\n').ToList();
        int ln = 0;
        foreach (var raw in File.ReadAllLines(editsFile))
        {
            ln++;
            string l = raw; int h = l.IndexOf('#'); if (h >= 0) l = l[..h];
            var t = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;
            if (t.Length != 2 || !double.TryParse(t[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double km) || km <= 0)
                throw new FormatException($"PptEdits.txt line {ln}: expected 'CODE km', got '{raw.Trim()}'");
            int i = lines.FindIndex(x => x.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } p && p[0] == t[0]);
            if (i < 0) throw new InvalidOperationException($"PptEdits.txt line {ln}: threat code '{t[0]}' not in Ppt.ini");
            var p2 = lines[i].Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            double oldFt = double.Parse(p2[1], System.Globalization.CultureInfo.InvariantCulture);
            string ft = (km * 3280.839895).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            lines[i] = $"{p2[0]} {ft}" + (p2.Length > 2 ? " " + p2[2] : "");
            notes.Add($"   Ppt.ini {p2[0]} ({(p2.Length > 2 ? p2[2] : "")}): {oldFt / 3280.839895:0.#} km → {km:0.#} km");
        }
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// Strings.txt salvato in UTF-8 (virgolette “ ” ’ di Word): BMS legge il testo come Windows-1252, come quello della Korea.
    /// Restituisce i byte convertiti, o null se il file e' gia' a posto.
    /// </summary>
    public static byte[]? FixAnsi(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.All(x => x < 0x80)) return null;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException) { return null; }   // non e' UTF-8: e' gia' ANSI
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp = Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
        return cp.GetBytes(text);
    }

    /// <summary>Bandiere valide in BMS 4.38: BMS ha solo le bandiere 0-7 (SWISS, USA, SKOREA,
    /// JAPAN, CIS, CHINA, NKOREA, NATO). Con un valore piu' alto BMS legge oltre la tabella e mostra immagini a caso (es. la
    /// coccarda ROUNDEL_ITAF per la bandiera 10).</summary>
    public const int MaxFlag = 7;

    /// <summary>
    /// Cambia la bandiera delle squadre in un .cam/.tac, sia nel .tea sia nell'intestazione (.cmp). 'remap' (nome, bandiera) dice la nuova
    /// bandiera per una vecchia (null = invariata). Riscrive il file solo se cambia qualcosa; restituisce le squadre cambiate.
    /// </summary>
    public static List<string> RemapTeamFlags(string path, Func<string, byte, byte?> remap, string? dst = null)
    {
        var res = new List<string>();
        var files = Dds.CampaignArchive.Read(path);
        int Idx(string ext) => files.FindIndex(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        int iv = Idx(".ver"), it = Idx(".tea"), ic = Idx(".cmp");
        int v = iv >= 0 ? UniFile.ReadVersion(files[iv].Data) : 0;
        bool changed = false;
        if (it >= 0)
        {
            var tf = TeamFile.Parse(files[it].Data, v);
            if (tf.Error == null)
            {
                foreach (var t in tf.Teams)
                    if (remap(t.Name, t.Flag) is byte nf && nf != t.Flag) { res.Add($"{t.Name} (.tea) {t.Flag}→{nf}"); tf.SetFlag(t, nf); changed = true; }
                files[it] = (files[it].Name, tf.Data);
            }
        }
        if (ic >= 0 && files[ic].Data.Length > 8)
        {
            var cmp = files[ic].Data;
            int cs = BitConverter.ToInt32(cmp, 0), us = BitConverter.ToInt32(cmp, 4);
            var raw = Dds.CampaignArchive.Expand(cmp.AsSpan(8, Math.Min(cs, cmp.Length - 8)), us);
            bool cc = false;
            if (raw.Length == us && raw.Length > 128 + 8 * 222)
                for (int i = 0; i < 8; i++)
                {
                    int o = 128 + i * 222;
                    string name = Encoding.Latin1.GetString(raw, o + 2, 20).Split('\0')[0];
                    if (remap(name, raw[o]) is byte nf && nf != raw[o])
                    {
                        res.Add($"{name} (.cmp) {raw[o]}→{nf}"); raw[o] = nf; cc = true;
                    }
                }
            if (cc)
            {
                var c = Dds.CampaignArchive.CompressLiteral(raw);
                var nd = new byte[8 + c.Length];
                BitConverter.TryWriteBytes(nd.AsSpan(0, 4), c.Length);
                BitConverter.TryWriteBytes(nd.AsSpan(4, 4), raw.Length);
                c.CopyTo(nd, 8);
                files[ic] = (files[ic].Name, nd);
                changed = true;
            }
        }
        // .te dei TE: testo con ":TeamName" e ":TeamFlag" per ogni squadra
        int ie = Idx(".te");
        if (ie >= 0)
        {
            var lines = Encoding.Latin1.GetString(files[ie].Data).Split('\n');
            bool tc = false;
            for (int k = 0; k + 3 < lines.Length; k++)
                if (lines[k].Trim() == ":TeamName" && lines[k + 2].Trim() == ":TeamFlag" && byte.TryParse(lines[k + 3].Trim(), out byte fl)
                    && remap(lines[k + 1].Trim(), fl) is byte nf && nf != fl)
                {
                    res.Add($"{lines[k + 1].Trim()} (.te) {fl}→{nf}");
                    lines[k + 3] = nf.ToString() + (lines[k + 3].EndsWith("\r") ? "\r" : "");
                    tc = true;
                }
            if (tc) { files[ie] = (files[ie].Name, Encoding.Latin1.GetBytes(string.Join("\n", lines))); changed = true; }
        }
        if (changed || (dst != null && dst != path)) Dds.CampaignArchive.Write(dst ?? path, files);
        return res;
    }

    /// <summary>
    /// Porta un .cam/.tac dal formato 108 (vecchi add-on) al 110 della 4.38: BMS risalva un file nel formato con cui l'ha
    /// letto (gCampDataVersion), quindi un TE fatto da un modello 108 resta 108. Differenze (dal codice di BMS 4.38):
    /// .cmp +2 u16 in fondo (v110: in KTO 1945 e 2100), ogni volo +4 byte alla fine del record (v109, BMS mette 1 se mancano).
    /// Squadroni, obiettivi, squadre e waypoint hanno solo controlli di versione piu' vecchi di 108. Null se non e' un 108.
    /// </summary>
    public static List<string>? UpgradeTo110(string path, string dst, Func<int, (int Domain, int Class, int Type)?> classOf)
    {
        var files = Dds.CampaignArchive.Read(path);
        int Idx(string ext) => files.FindIndex(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        int iv = Idx(".ver"), ic = Idx(".cmp"), iu = Idx(".uni");
        if (iv < 0 || UniFile.ReadVersion(files[iv].Data) != 108) return null;
        var notes = new List<string>();
        if (ic >= 0 && files[ic].Data.Length > 8)
        {
            var cmp = files[ic].Data;
            int cs = BitConverter.ToInt32(cmp, 0), us = BitConverter.ToInt32(cmp, 4);
            var raw = Dds.CampaignArchive.Expand(cmp.AsSpan(8, Math.Min(cs, cmp.Length - 8)), us);
            if (raw.Length != us) throw new InvalidDataException(".cmp does not expand");
            var nr = new byte[raw.Length + 4];
            raw.CopyTo(nr, 0);
            BitConverter.TryWriteBytes(nr.AsSpan(raw.Length, 2), (ushort)1945);
            BitConverter.TryWriteBytes(nr.AsSpan(raw.Length + 2, 2), (ushort)2100);
            var c = Dds.CampaignArchive.CompressLiteral(nr);
            var nd = new byte[8 + c.Length];
            BitConverter.TryWriteBytes(nd.AsSpan(0, 4), c.Length);
            BitConverter.TryWriteBytes(nd.AsSpan(4, 4), nr.Length);
            c.CopyTo(nd, 8);
            files[ic] = (files[ic].Name, nd);
            notes.Add(".cmp: +4 bytes (v110)");
        }
        if (iu >= 0 && files[iu].Data.Length > 10)
        {
            Func<int, (int, int)?> co = t => classOf(t - 100) is { } k ? (k.Domain, k.Type) : null;
            var u = UniFile.Parse(files[iu].Data, 108, co);
            if (u.Error != null) throw new InvalidDataException(".uni: " + u.Error);
            var flights = u.Units.Where(x => x.Kind == UnitKind.Flight).OrderByDescending(x => x.End).ToList();
            // i TE salvati da BMS 4.38 partendo da un modello 108 hanno gia' i 4 byte dei voli (il .ver resta 108): BMS
            // pero' li legge come 108, senza quei byte, e il pacchetto dopo il volo esce sfasato
            if (flights.Count > 0 && u.FlightCft) notes.Add($".uni: {flights.Count} flights already in the v109 layout (saved by BMS 4.38 with a 108 .ver)");
            else if (flights.Count > 0)
            {
                var data = u.Data.ToList();
                foreach (var f in flights) data.InsertRange(f.End, new byte[] { 1, 1, 1, 1 });
                u.Data = data.ToArray();
                var nu = u.Build(files[iu].Data);
                var chk = UniFile.Parse(nu, 110, co);
                if (chk.Error != null || chk.Units.Count != u.Units.Count) throw new InvalidDataException(".uni check failed: " + chk.Error);
                files[iu] = (files[iu].Name, nu);
                notes.Add($".uni: {flights.Count} flights +4 bytes (v109)");
            }
        }
        files[iv] = (files[iv].Name, Encoding.ASCII.GetBytes("110"));
        Dds.CampaignArchive.Write(dst, files);
        return notes;
    }
}
