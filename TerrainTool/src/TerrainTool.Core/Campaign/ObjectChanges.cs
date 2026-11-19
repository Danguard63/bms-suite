using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerrainTool.Core.Dds;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.Campaign;

/// <summary>Un obiettivo cambiato dal lavoro Objects: disposizione (OCD), nome se cambiato, direzione e posizione (piedi, x nord / y est).</summary>
/// <summary>Edited = la posizione viene da objective_edits.csv (una modifica a mano o di Optimize bridges), non da un calcolo automatico.</summary>
public sealed record ObjectiveChange(int Id, int Ocd, string? NewName, double Heading, double X, double Y, bool Edited = false);

/// <summary>
/// Le modifiche agli obiettivi fatte dall'Object Editor (lavoro Objects) che riguardano i file della campagna:
/// basi spostate, girate, sostituite o tolte (diventate incroci). L'Object Editor le scrive in Output\ObjectChanges.json
/// del progetto; il Campaign Editor le applica (ObjectChangeApplier) a CampObjData.XML, ai .cam, al modello del TE e a stations+ils.dat.
/// </summary>
public sealed class ObjectChangeSet
{
    public DateTime Created { get; set; } = DateTime.Now;
    public List<ObjectiveChange> Objectives { get; set; } = new();
    public List<CampaignArchive.Move> Moves { get; set; } = new();
    public List<int> RemovedBases { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string PathFor(TheaterProject p) => Path.Combine(p.ProjectFolder, "Output", "ObjectChanges.json");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts), new UTF8Encoding(false));
    }

    public static ObjectChangeSet? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<ObjectChangeSet>(File.ReadAllText(path), Opts) : null;
}

/// <summary>
/// Applica un ObjectChangeSet ai file della campagna di un progetto. Legge dal vecchio add-on (sola lettura) e dalle copie salvate
/// dal Campaign Editor (CampaignEdits: hanno la precedenza, cosi' collegamenti, proprietari e unita' modificati restano), scrive in
/// Output\Install\&lt;add-on&gt;\Campaign; le copie di CampaignEdits vengono aggiornate anche li', cosi' l'editor le apre gia' giuste.
/// </summary>
public static class ObjectChangeApplier
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static List<string> Apply(ObjectChangeSet set, TheaterProject p, string campOut, NetworkEdits? net = null)
    {
        var notes = new List<string>();
        // installed theater (Objective Studio): its own campaign folder; conversion project: the old add-on's
        string oldCamp = p.IsInstalledTheater ? p.SourceCampaignDir : Path.Combine(p.OldTheaterFolder, "Campaign");
        string campEdits = ObjectsJob.CampaignEditsFolder(p);
        if (p.IsProtectedPath(campOut)) throw new InvalidOperationException("Protected output folder: " + campOut);
        Directory.CreateDirectory(campOut);

        // CampObjData.XML: ogni obiettivo cambiato (disposizione, nome, direzione, posizione)
        string srcXml = Path.Combine(oldCamp, "CampObjData.XML");
        if (File.Exists(srcXml))
        {
            var byId = set.Objectives.ToDictionary(o => o.Id);
            string xml = Regex.Replace(File.ReadAllText(srcXml), @"<CampObj CampId=""(\d+)"">.*?</CampObj>", m =>
            {
                if (!byId.TryGetValue(int.Parse(m.Groups[1].Value, Ci), out var o)) return m.Value;
                string v = m.Value;
                v = Regex.Replace(v, "<OcdIndex>.*?</OcdIndex>", $"<OcdIndex>{o.Ocd}</OcdIndex>");
                if (o.NewName != null) v = Regex.Replace(v, "<CampName>.*?</CampName>", $"<CampName>{System.Security.SecurityElement.Escape(o.NewName)}</CampName>");
                v = Regex.Replace(v, "<Heading>.*?</Heading>", $"<Heading>{o.Heading.ToString("0.000", Ci)}</Heading>");
                v = Regex.Replace(v, "<PositionX>.*?</PositionX>", $"<PositionX>{o.X.ToString("0.000", Ci)}</PositionX>");
                v = Regex.Replace(v, "<PositionY>.*?</PositionY>", $"<PositionY>{o.Y.ToString("0.000", Ci)}</PositionY>");
                return v;
            }, RegexOptions.Singleline);
            File.WriteAllText(Path.Combine(campOut, "CampObjData.XML"), xml, new UTF8Encoding(true));
            notes.Add($"CampObjData.XML: {set.Objectives.Count} objectives changed");
        }

        // BMS prende posizione e direzione degli obiettivi dai file della campagna (.tac/.cam), non da CampObjData.XML:
        // si riscrivono il modello del TE (te_new / Te_New_Nt) e i salvataggi della campagna che contengono gli obiettivi
        string? FindIn(string dir, string n) => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*").FirstOrDefault(f => string.Equals(Path.GetFileName(f), n, StringComparison.OrdinalIgnoreCase)) : null;
        string? FindCamp(string n) => FindIn(campEdits, n) ?? FindIn(oldCamp, n);
        var jobs = new List<(string Src, string Dst)>();
        string? teSrc = FindCamp("Te_New_Nt.tac") ?? FindCamp("te_new.tac");
        if (teSrc != null) { jobs.Add((teSrc, "Te_New_Nt.tac")); if (FindCamp("te_new.tac") is string t0) jobs.Add((t0, Path.GetFileName(t0))); }
        var cams = (Directory.Exists(oldCamp) ? Directory.EnumerateFiles(oldCamp, "*.cam").Select(Path.GetFileName) : Enumerable.Empty<string?>())
            .Concat(Directory.Exists(campEdits) ? Directory.EnumerateFiles(campEdits, "*.cam").Select(Path.GetFileName) : Enumerable.Empty<string?>())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var n in cams)
            if (FindCamp(n!) is string f) jobs.Add((f, Path.GetFileName(f)));
        // every TE of the theater has the objectives too (installed theater), and in a project moved to the standard projection
        // its units and waypoints have to follow (the editor's saved copy, if any, was converted with the project)
        if ((p.IsInstalledTheater || p.Theater.SourceGeoref != null) && Directory.Exists(oldCamp))
            foreach (var f in Directory.EnumerateFiles(oldCamp, "*.tac"))
                if (!jobs.Any(j => j.Dst.Equals(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase))) jobs.Add((FindIn(campEdits, Path.GetFileName(f)) ?? f, Path.GetFileName(f)));
        int fromEdits = jobs.Count(j => j.Src.StartsWith(campEdits, StringComparison.OrdinalIgnoreCase));
        if (fromEdits > 0) notes.Add($"{fromEdits} file(s) taken from the campaign editor's saves ({campEdits}), updated there too");
        int patched = 0; var problems = new HashSet<string>();
        foreach (var (src, name) in jobs)
        {
            string dst = Path.Combine(campOut, name);
            var log = CampaignArchive.PatchArchive(src, dst, set.Moves);
            if (log == null) continue;
            patched++;
            foreach (var l in log) problems.Add($"{name}: {l}");
            if (src.StartsWith(campEdits, StringComparison.OrdinalIgnoreCase)) File.Copy(dst, src, true);
        }
        notes.Add($"Campaign files rewritten with the new objective positions: {patched} (Te_New_Nt.tac, te_new.tac, *.cam)");
        foreach (var pr in problems.Take(20)) notes.Add("   " + pr);

        // old add-on in another grid (project moved to the standard projection): units, waypoints and bullseye follow.
        // Only the files read from the old add-on: the Campaign Editor's saves (CampaignEdits) were converted once, when the project was.
        var grid = new TerrainTool.Core.Theater.TheaterGrid(p.Theater);
        if (grid.Reprojects)
        {
            var udb = CampaignDb.Load(DbDirs(p).Where(Directory.Exists));
            foreach (var (src, name) in jobs)
            {
                if (src.StartsWith(campEdits, StringComparison.OrdinalIgnoreCase)) continue;
                string dst = Path.Combine(campOut, name);
                if (!File.Exists(dst)) continue;
                var d = CampaignDoc.Load(dst, udb.ClassOf);
                string log = TerrainTool.Core.Theater.GeorefMigration.Reproject(d, grid);
                d.Save(dst);
                notes.Add($"{name}: {log}");
            }
            // campaign regions (<theater>.pak) of the old add-on, resampled; a copy in CampaignEdits was converted with the project
            if (Directory.Exists(oldCamp) && Directory.EnumerateFiles(oldCamp, "*.pak").FirstOrDefault() is { } pak
                && !(Directory.Exists(campEdits) && Directory.EnumerateFiles(campEdits, "*.pak").Any()))
            {
                if (TerrainTool.Core.Theater.GeorefMigration.ReprojectPak(File.ReadAllBytes(pak), grid) is { } pk)
                {
                    File.WriteAllBytes(Path.Combine(campOut, Path.GetFileName(pak)), pk);
                    notes.Add($"{Path.GetFileName(pak)}: campaign regions resampled to the standard projection");
                }
            }
            notes.Add("Rebuild the theater cells (<theater>.thr) on the new terrain: Campaign Editor, Theater cells");
        }

        // tipi degli obiettivi coerenti con le disposizioni attuali (un giro precedente di Objects puo' aver lasciato CT di copie che non esistono piu')
        // (theater conversion only: an installed theater keeps the types it has, except the objectives changed here)
        if (!p.IsInstalledTheater)
        try { SyncTypes(p, campOut, oldCamp, campEdits, jobs, notes); }
        catch (Exception ex) { notes.Add("Objective types not checked: " + ex.Message); }

        // rete degli obiettivi (Object Editor): obiettivi nuovi e link, dopo spostamenti e tipi
        if (net != null && !net.IsEmpty)
        {
            string xmlOut = Path.Combine(campOut, "CampObjData.XML");
            string xmlIn = File.Exists(xmlOut) ? xmlOut : srcXml;
            if (File.Exists(xmlIn)) File.WriteAllText(xmlOut, net.ApplyToCampObjData(File.ReadAllText(xmlIn)), new UTF8Encoding(true));
            int nf = 0;
            // database installato dal progetto (serve a leggere bene le unita' dei salvataggi), se no quello del vecchio add-on
            string addOnName = new DirectoryInfo(p.OldTheaterFolder).Name;
            var db = CampaignDb.Load(DbDirs(p).Where(Directory.Exists));
            foreach (var (src, name) in jobs)
            {
                string dst = Path.Combine(campOut, name);
                string from = File.Exists(dst) ? dst : src;
                var d = CampaignDoc.Load(from, db.ClassOf);
                if (d.Units?.Error != null) { notes.Add($"{name}: units not readable ({d.Units.Error}), file left as it was"); continue; }
                if (!d.ObjectivesOwn) continue;
                var log = net.ApplyTo(d);
                d.Save(dst);
                nf++;
                notes.Add($"{name}: network – {string.Join("; ", log.Take(4))}");
                if (src.StartsWith(campEdits, StringComparison.OrdinalIgnoreCase)) File.Copy(dst, src, true);
            }
            notes.Add($"Objective network: {net.Added.Count} new objectives, {net.Links.Count} link changes written to CampObjData.XML and {nf} campaign files");
        }

        // CampObjData.XML completo: gli obiettivi del modello del TE che non vi hanno una voce (es. Mideast 5200 "Doha Seaport North",
        // aggiunto dall'autore dell'add-on solo nei file della campagna) la ricevono, con nome, posizione, direzione e disposizione
        if (!p.IsInstalledTheater)
        try { CompleteCampObjData(p, campOut, srcXml, jobs, notes); }
        catch (Exception ex) { notes.Add("CampObjData.XML not completed: " + ex.Message); }

        // stations+ils.dat: le righe delle basi tolte si commentano
        string st = FindCamp("stations+ils.dat") ?? "";
        if (set.RemovedBases.Count > 0 && File.Exists(st))
        {
            var removed = set.RemovedBases.ToHashSet();
            int n = 0;
            var outl = File.ReadAllLines(st, Encoding.Latin1).Select(l =>
            {
                var m = Regex.Match(l, @"^\s*(\d+)\s");
                if (!m.Success || !removed.Contains(int.Parse(m.Groups[1].Value, Ci))) return l;
                n++; return "# removed airbase (now a junction): " + l;
            });
            File.WriteAllLines(Path.Combine(campOut, "stations+ils.dat"), outl, Encoding.Latin1);
            notes.Add($"stations+ils.dat: {n} navaid lines of removed airbases commented out");
        }
        if (!p.IsInstalledTheater)
            File.WriteAllText(Path.Combine(campOut, "relocated.txt"), $"Objectives moved by the Objects job ({set.Moves.Count}), applied by the Campaign Editor. Assemble does not overwrite Te_New_Nt.tac while this file exists.\r\n");
        return notes;
    }

    /// <summary>Objects database to read the campaign units with: the build, then the theater's own.</summary>
    public static IEnumerable<string> DbDirs(TheaterProject p)
    {
        if (p.IsInstalledTheater) return new[] { ObjectsJob.InstalledObjectsOut(p), p.SourceObjectsDir };
        string addOnName = new DirectoryInfo(p.OldTheaterFolder).Name;
        return new[] { Path.Combine(p.ProjectFolder, "Output", "Install", addOnName, "Terrdata", ObjectsJob.FolderName), Path.Combine(p.OldTheaterFolder, "Terrdata", "objects") };
    }

    private static void CompleteCampObjData(TheaterProject p, string campOut, string srcXml, List<(string Src, string Dst)> jobs, List<string> notes)
    {
        string xmlOut = Path.Combine(campOut, "CampObjData.XML");
        if (!File.Exists(xmlOut)) { if (!File.Exists(srcXml)) return; File.Copy(srcXml, xmlOut); }
        var te = jobs.FirstOrDefault(j => j.Dst.Equals("Te_New_Nt.tac", StringComparison.OrdinalIgnoreCase));
        if (te.Dst == null) return;
        string teFile = File.Exists(Path.Combine(campOut, te.Dst)) ? Path.Combine(campOut, te.Dst) : te.Src;
        string addOnName = new DirectoryInfo(p.OldTheaterFolder).Name;
        var db = CampaignDb.Load(DbDirs(p).Where(Directory.Exists));
        var d = CampaignDoc.Load(teFile, db.ClassOf);
        string xml = File.ReadAllText(xmlOut);
        var have = Regex.Matches(xml, @"<CampObj CampId=""(\d+)"">").Select(m => int.Parse(m.Groups[1].Value, Ci)).ToHashSet();
        var sb = new StringBuilder();
        var added = new List<string>();
        foreach (var o in d.Objectives.Where(o => !have.Contains(o.CampId)).OrderBy(o => o.CampId))
        {
            int ocd = db.Ct.TryGetValue(o.Type - 100, out var ct) && ct.EntityType == 3 ? ct.EntityIdx : -1;
            if (ocd < 0) { notes.Add($"CampObjData.XML: {o.CampId} {o.Name} has no layout in the database, not added"); continue; }
            double hdg = (o.HeadingRad * 180 / Math.PI % 360 + 360) % 360;
            sb.Append($"    <CampObj CampId=\"{o.CampId}\">\r\n        <CampName>{System.Security.SecurityElement.Escape(o.Name)}</CampName>\r\n" +
                      $"        <OcdIndex>{ocd}</OcdIndex>\r\n        <Heading>{hdg.ToString("0.000", Ci)}</Heading>\r\n" +
                      $"        <PositionX>{o.SimNorth.ToString("0.000", Ci)}</PositionX>\r\n        <PositionY>{o.SimEast.ToString("0.000", Ci)}</PositionY>\r\n" +
                      $"        <PositionZ>0.000</PositionZ>\r\n    </CampObj>\r\n");
            added.Add($"{o.CampId} {o.Name}");
        }
        if (added.Count == 0) return;
        int end = xml.LastIndexOf("</CampObjData>", StringComparison.Ordinal);
        if (end < 0) return;
        File.WriteAllText(xmlOut, xml.Insert(end, sb.ToString()), new UTF8Encoding(true));
        notes.Add($"CampObjData.XML: {added.Count} objectives of the campaign had no entry, added: {string.Join(", ", added.Take(10))}");
    }

    private static Dictionary<int, int> OcdOf(string campObjData) =>
        Regex.Matches(File.ReadAllText(campObjData), @"<CampObj CampId=""(\d+)"">.*?<OcdIndex>(\d+)</OcdIndex>", RegexOptions.Singleline)
            .GroupBy(m => int.Parse(m.Groups[1].Value, Ci)).ToDictionary(g => g.Key, g => int.Parse(g.First().Groups[2].Value, Ci));

    /// <summary>
    /// Ogni obiettivo dei file della campagna riceve il CT della sua disposizione attuale (CampObjData finale) se ha lo stesso
    /// numero di oggetti, altrimenti quello della disposizione originale dell'add-on (basi e ponti tolti che negli archivi con
    /// unita' restano com'erano). I CT inesistenti nel database installato vengono segnalati.
    /// </summary>
    private static void SyncTypes(TheaterProject p, string campOut, string oldCamp, string campEdits, List<(string Src, string Dst)> jobs, List<string> notes)
    {
        string addOn = new DirectoryInfo(p.OldTheaterFolder).Name;
        string ctXml = p.IsInstalledTheater ? Path.Combine(ObjectsJob.InstalledObjectsOut(p), "Falcon4_CT.xml")
                                            : Path.Combine(p.ProjectFolder, "Output", "Install", addOn, "Terrdata", ObjectsJob.FolderName, "Falcon4_CT.xml");
        string finalXml = Path.Combine(campOut, "CampObjData.XML"), origXml = Path.Combine(oldCamp, "CampObjData.XML");
        if (!File.Exists(ctXml) || !File.Exists(finalXml)) { notes.Add("Objective types not checked (Falcon4_CT.xml or CampObjData.XML missing in Output\\Install)"); return; }
        var ocdToCt = new Dictionary<int, int>();
        int ctCount = 0;
        foreach (Match m in Regex.Matches(File.ReadAllText(ctXml), @"<CT Num=""(\d+)"">(.*?)</CT>", RegexOptions.Singleline))
        {
            int num = int.Parse(m.Groups[1].Value, Ci);
            ctCount = Math.Max(ctCount, num + 1);
            string b = m.Groups[2].Value;
            if (!b.Contains("<EntityType>3</EntityType>")) continue;
            var e = Regex.Match(b, @"<EntityIdx>(\d+)</EntityIdx>");
            if (e.Success) ocdToCt.TryAdd(int.Parse(e.Groups[1].Value, Ci), num);
        }
        var finalOcd = OcdOf(finalXml);
        var origOcd = File.Exists(origXml) ? OcdOf(origXml) : new Dictionary<int, int>();
        IReadOnlyList<int> Cands(int campId)
        {
            var l = new List<int>(2);
            if (finalOcd.TryGetValue(campId, out int o1) && ocdToCt.TryGetValue(o1, out int c1)) l.Add(c1);
            if (origOcd.TryGetValue(campId, out int o2) && ocdToCt.TryGetValue(o2, out int c2) && !l.Contains(c2)) l.Add(c2);
            return l;
        }
        // quanti oggetti ha ogni CT: dai record degli archivi gia' giusti (tipo = quello atteso per il loro CampId)
        var fcOf = new Dictionary<int, int>();
        foreach (var (_, name) in jobs)
        {
            string dst = Path.Combine(campOut, name);
            if (!File.Exists(dst)) continue;
            var types = CampaignArchive.ObjectiveTypes(dst);
            if (types == null) continue;
            foreach (var (campId, (ct, fc)) in types)
            {
                var c = Cands(campId);
                if (ct >= 0 && ct < ctCount && c.Contains(ct)) fcOf.TryAdd(ct, fc);
            }
        }
        int files = 0;
        foreach (var (src, name) in jobs)
        {
            string dst = Path.Combine(campOut, name);
            if (!File.Exists(dst)) continue;
            var log = CampaignArchive.SyncObjectiveTypes(dst, Cands, fcOf, ctCount);
            if (log == null || log.Count == 0) continue;
            if (log[0].Contains("from the current layouts", StringComparison.Ordinal))
            {
                files++;
                if (src.StartsWith(campEdits, StringComparison.OrdinalIgnoreCase)) File.Copy(dst, src, true);
            }
            notes.Add($"{name}: " + log[0]);
            foreach (var l in log.Skip(1).Take(10)) notes.Add("   " + l);
        }
        notes.Add(files == 0 ? "Objective types: all match the installed database" : $"Objective types fixed in {files} file(s)");
    }
}
