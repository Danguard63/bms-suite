using System.Globalization;
using TerrainTool.Core.Campaign;

namespace TerrainTool.Core.Database;

/// <summary>
/// A copy of an aircraft (or any vehicle) of a theater under another name, e.g. "F-16CM-50 Red" next to the F-16CM-50 of the
/// other side of a PvP theater: a new Class Table entry and vehicle record (same 3D model, same flight model, same aircraft
/// data, same hardpoints), its own class number (Specific: BMS tells aircraft apart by it), a copy of its squadron and flight
/// types, a line in the theater's TE list and the name in its interface files. Nothing of the original changes; the flight
/// model is only referred to (never copied). Installed with a backup (Restore removes what was added).
/// </summary>
public static class AircraftDuplicate
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>Why the theater's database cannot get copies (null = it can).</summary>
    public static string? Problem(BmsTheater t) =>
        DbInstaller.IsShared(t.DataDir, t.ObjectDir) ? "it uses BMS's own database (Terrdata\\objects), which is not changed: use an add-on theater" : null;

    /// <summary>The plan of the copy (nothing written; workDir holds the files it prepares). Report and Problems tell what it does.</summary>
    public static Interchange.Plan Make(BmsTheater t, int ct, string newName, string workDir, BmsDatabase? db = null)
    {
        var p = new Interchange.Plan { Src = t, Dst = t, BmsObjects = Path.Combine(t.DataDir, "Terrdata", "objects") };
        if (Problem(t) is { } why) { p.Problems.Add(why); return p; }
        Directory.CreateDirectory(workDir);
        // the database as the program shows it (its changes not installed yet count for the free numbers), else the theater's
        var D = db ?? BmsDatabase.Load(t.ObjectDir);
        if (D.Get("CT", ct) is not { } c || D.EntityOf(ct) is not { Table: "VCD" } v) { p.Problems.Add($"CT {ct} is not a vehicle"); return p; }
        newName = newName.Trim();
        if (newName.Length == 0) { p.Problems.Add("Give the copy a name"); return p; }
        if (newName.Length > BmsDatabase.MaxName("VCD")) { p.Problems.Add($"The name can have at most {BmsDatabase.MaxName("VCD")} characters (BMS's limit)"); return p; }
        if (D.All("VCD").Any(x => string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase))) { p.Problems.Add($"'{newName}' is already a vehicle of this theater"); return p; }

        int dom = c.Int("Domain"), cls = c.Int("Class"), type = c.Int("Type"), sub = c.Int("SubType");
        // a class number (Specific) no other entry of the same Domain / Class / Type / SubType has
        int Free(int d, int k, int ty, int su, IEnumerable<int> also)
        {
            var used = D.All("CT").Where(x => x.Int("Domain") == d && x.Int("Class") == k && x.Int("Type") == ty && x.Int("SubType") == su)
                        .Select(x => x.Int("Specific")).Concat(also).ToHashSet();
            for (int s = 1; s < 256; s++) if (!used.Contains(s)) return s;
            return -1;
        }
        int spec = Free(dom, cls, type, sub, Array.Empty<int>());
        if (spec < 0) { p.Problems.Add($"No free class number left for Domain {dom} Class {cls} Type {type} SubType {sub}"); return p; }

        int nct = D.NextFree("CT"), nv = D.NextFree("VCD"), nu = D.NextFree("UCD");
        p.Records.Add(("CT", nct, DbOps.With(c.Fields, ("EntityIdx", nv.ToString(Ci)), ("Specific", spec.ToString(Ci)))));
        p.Records.Add(("VCD", nv, DbOps.With(v.Fields, ("Name", newName), ("CtIdx", nct.ToString(Ci)))));
        p.CtMap[ct] = nct;
        p.Report.Add($"{v.Name} (CT {ct}, VCD {v.Num}) → '{newName}': CT {nct}, VCD {nv}, class {dom} {cls} {type} {sub} {spec} (the original has Specific {c.Int("Specific")})");
        p.Report.Add($"Same 3D model ({c.Int("GraphicsNormal")}), same aircraft data (ACD {c.Int("MoverDefinitionData")}: flight model, sensors), same hardpoints and weapons");

        // its squadron and flight types: units made only of this aircraft (TE and campaigns choose aircraft through them)
        int next = nct + 1;
        var squadrons = new List<(int OldCt, int NewCt, int SubType)>();
        foreach (var u in D.All("CT").Where(u => u.Int("Domain") == dom && BmsDatabase.EntityTable(u.Int("EntityType")) == "UCD"))
        {
            if (D.EntityOf(u.Num) is not { } ue) continue;
            var el = D.Elements(ue).Where(x => x.Ct > 0 && x.Count > 0).ToList();
            if (el.Count == 0 || el.Any(x => x.Ct != ct)) continue;
            var uf = ue.Fields.ToList();
            for (int i = 0; i < 32; i++)
                if (ue.Get($"VehicleCtIdx_{i}") != null && ue.Int($"VehicleCtIdx_{i}", 0) == ct) uf = DbOps.With(uf, ($"VehicleCtIdx_{i}", nct.ToString(Ci)));
            // the unit types have the aircraft's class number (that is how BMS pairs them), unless it is taken
            int ucs = UsedBy(D, u, spec) ? Free(u.Int("Domain"), u.Int("Class"), u.Int("Type"), u.Int("SubType"), Array.Empty<int>()) : spec;
            p.Records.Add(("CT", next, DbOps.With(u.Fields, ("EntityIdx", nu.ToString(Ci)), ("Specific", ucs.ToString(Ci)))));
            p.Records.Add(("UCD", nu, DbOps.With(uf, ("CtIdx", next.ToString(Ci)))));   // same name: it is the role (Fighter, Attack…)
            p.CtMap[u.Num] = next;
            string kind = u.Int("Type") == 3 ? "squadron" : u.Int("Type") == 1 ? "flight" : "unit";
            p.Report.Add($"Its {kind} type '{ue.Name}' (CT {u.Num}) → a copy for '{newName}': CT {next}, UCD {nu}");
            if (u.Int("Type") == 3) squadrons.Add((u.Num, next, u.Int("SubType")));
            next++; nu++;
        }

        // the TE list: a line like the original's, with the new class number and name
        if (dom == 2 && squadrons.Count > 0)
        {
            var d = new UnitsConversion.Destination(t, t.DataDir, null);
            var te = UnitsConversion.TePlanes(d);
            var orig = te.Entries.Where(e => e.Type == type && e.SubType == sub && e.Specific == c.Int("Specific")).ToList();
            if (orig.Count == 0) p.Report.Add($"TE list: the original is not in it, so the copy is not added either (Campaign Editor, TE lists)");
            else
            {
                string? art = UnitsConversion.OwnArtRoot(d);
                var names = art != null ? new UnitsConversion.UiNames(d, art) : null;
                int text = orig[0].Text;
                if (names != null) text = names.Add(newName, te.Entries.Select(e => e.Text).DefaultIfEmpty(0).Max());
                foreach (var o in orig)
                {
                    int id = te.Entries.Select(e => e.Id).DefaultIfEmpty(21100).Max() + 1;
                    var line = new TePlanesFile.Entry { Name = newName, Type = type, SubType = sub, Specific = spec, UnitSubType = o.UnitSubType, Id = id, Text = text, Icon = o.Icon };
                    te.Entries.Add(line);
                    p.Report.Add($"TE list: {line.Type} {line.SubType} {line.Specific} {line.UnitSubType}, ID {id}, icon {o.Icon} (as {o.Name})");
                }
                string f = Path.Combine(workDir, "TePlanes.lst");
                te.Save(f);
                p.Files[Path.Combine(t.CampaignDir, "TePlanes.lst")] = (f, null);
                if (names != null) { names.Write(p, workDir); p.Report.Add($"Name '{newName}' (text {text}) in the theater's interface files ({art})"); }
                else p.Report.Add($"The TE shows the original's name ('{orig[0].Name}'): the theater uses BMS's own interface files, which are not changed");
            }
        }
        else if (dom == 2) p.Report.Add("No squadron type made only of this aircraft: the copy is not offered in the TE");
        p.Report.Insert(0, $"Copy of {v.Name} as '{newName}' in {t.Name}");
        // remembered in the theater: a database rebuilt by TerrainTool / Objective Studio gets the copy again
        var list = Load(t.ObjectDir).Where(x => !string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)).Append((Original: v.Name, Name: newName)).ToList();
        string sf = Path.Combine(workDir, StateFile);
        File.WriteAllLines(sf, new[] { "Original;Copy" }.Concat(list.Select(x => $"{x.Original};{x.Name}")));
        p.Files[Path.Combine(t.ObjectDir, StateFile)] = (sf, null);
        return p;
    }

    /// <summary>In the theater's objects folder: the copies made (original;copy), made again when the database is rebuilt.</summary>
    public const string StateFile = "BmsSuite_duplicates.csv";

    public static List<(string Original, string Name)> Load(string objectDir)
    {
        string f = Path.Combine(objectDir, StateFile);
        if (!File.Exists(f)) return new();
        return File.ReadAllLines(f).Skip(1).Select(l => l.Split(';')).Where(c => c.Length >= 2 && c[0].Trim().Length > 0 && c[1].Trim().Length > 0)
            .Select(c => (c[0].Trim(), c[1].Trim())).ToList();
    }

    /// <summary>
    /// After the theater's database was rebuilt (TerrainTool's install): the copies listed in it that are missing are made
    /// again, each with its backup. Returns the log (empty when there is nothing to do).
    /// </summary>
    public static List<string> Reapply(string dataDir, string objectDir, string workDir, string backupRoot)
    {
        var log = new List<string>();
        var t = BmsTheater.List(dataDir).FirstOrDefault(x => string.Equals(Path.GetFullPath(x.ObjectDir).TrimEnd('\\', '/'), Path.GetFullPath(objectDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (t == null) return log;
        foreach (var (orig, name) in Load(objectDir))
        {
            var D = BmsDatabase.Load(objectDir);
            if (D.All("VCD").Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            var src = D.All("VCD").FirstOrDefault(v => string.Equals(v.Name, orig, StringComparison.OrdinalIgnoreCase));
            if (src == null) { log.Add($"Copy '{name}': its original '{orig}' is not in the database any more: not made again"); continue; }
            var p = Make(t, D.CtOf(src), name, Path.Combine(workDir, "duplicate"));
            if (p.Problems.Count > 0) { log.Add($"Copy '{name}': not made again ({string.Join("; ", p.Problems)})"); continue; }
            log.Add($"Copy '{name}' of '{orig}' made again");
            log.AddRange(Install(p, Path.Combine(workDir, "duplicate"), backupRoot).Select(x => "  " + x));
        }
        return log;
    }

    private static bool UsedBy(BmsDatabase D, DbRecord u, int spec) =>
        D.All("CT").Any(x => x.Int("Domain") == u.Int("Domain") && x.Int("Class") == u.Int("Class") && x.Int("Type") == u.Int("Type") && x.Int("SubType") == u.Int("SubType") && x.Int("Specific") == spec);

    /// <summary>Writes the copy into the theater (BMS closed), backup first. Returns the log.</summary>
    public static List<string> Install(Interchange.Plan p, string workDir, string backupRoot)
    {
        if (DbInstaller.BmsRunning()) throw new InvalidOperationException("BMS is running: close it first.");
        if (p.Problems.Count > 0) throw new InvalidOperationException(string.Join("\n", p.Problems));
        Interchange.Write(p, workDir, Path.Combine(workDir, "out"));
        return Interchange.Install(p, Path.Combine(workDir, "out"), backupRoot);
    }
}
