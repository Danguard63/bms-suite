using System.Globalization;
using TerrainTool.Core.Geo;
using TerrainTool.Core.Theater;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
string dataDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "Data");
int fail = 0;
void Check(string what, double got, double exp, double tol) {
  bool ok = Math.Abs(got - exp) <= tol; if (!ok) fail++;
  Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {what}: {got:R} expected {exp:R}");
}
// valori di riferimento calcolati con pyproj (righe stdin: lon0 lat lon x y)
foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "ref.txt"))) {
  var f = line.Split(' ').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
  var tm = new TransverseMercator(f[0]);
  var (x, y) = tm.Forward(f[1], f[2]);
  Check($"fwd lon0={f[0]} {f[1]},{f[2]} x", x, f[3], 0.001); Check("   y", y, f[4], 0.001);
  var (la, lo) = tm.Inverse(f[3], f[4]);
  Check("   inv lat", la, f[1], 1e-9); Check("   inv lon", lo, f[2], 1e-9);
}
var korea = new TheaterConfig { Name="KTO", SizeKm=1024, CenterLat=38.5, CenterLon=127.5 };
Check("Korea y_0", korea.FalseNorthing, -3749293.4269, 0.01);
var bases = AirbaseCsv.Load(Path.Combine(dataDir, "airbases.csv"));
var real = bases.Where(b => b.HasReal).Select(b => (b.Lat, b.Lon)).ToList();
Console.WriteLine($"Airbases: {bases.Count}, with real coordinates: {real.Count}");
var c = CenterFinder.Fit(real);
Console.WriteLine($"Center: {c.Lat} {c.Lon}");
var cfg = new TheaterConfig { Name = "Mideast", SizeKm = 2048, CenterLat = c.Lat, CenterLon = c.Lon };
var g = cfg.CreateGrid();
var m = CenterFinder.Margins(g, real);
Console.WriteLine($"Margins km W {m.W:0} E {m.E:0} S {m.S:0} N {m.N:0}");
foreach (var k in g.Corners()) Console.WriteLine($"Corner {k.Lat:0.0000} {k.Lon:0.0000}");
var (cx, cy) = g.ToTheater(cfg.CenterLat, cfg.CenterLon);
Check("center X", cx, 1024000, 0.001); Check("center Y", cy, 1024000, 0.001);
Console.WriteLine(cfg.ToTheaterTxt());
var bm = BaseMap.Load(Path.Combine(dataDir, "mappa_base.txt"));
Console.WriteLine($"Map lines: {bm.Count}");

// database compilato: BMS compila le Falcon4_*.xml in FALCON4.*; letti nei due modi devono dare gli stessi valori
string bmsObjects = Environment.GetEnvironmentVariable("BMS_OBJECTS") ?? @"C:\Falcon BMS 4.38\Data\TerrData\Objects";
if (File.Exists(Path.Combine(bmsObjects, "Falcon4_CT.xml")) && File.Exists(Path.Combine(bmsObjects, "FALCON4.CT"))) {
  var xml = TerrainTool.Core.Campaign.CampaignDb.Load(new[] { bmsObjects });
  string tmpDb = Directory.CreateTempSubdirectory("compiledDb").FullName;
  try {
    foreach (var t in new[] { "CT", "UCD", "VCD", "WCD", "WLD", "SSD" }) File.Copy(Path.Combine(bmsObjects, "FALCON4." + t), Path.Combine(tmpDb, "FALCON4." + t));
    var bin = TerrainTool.Core.Campaign.CampaignDb.Load(new[] { tmpDb }, compiledInFirstDir: true);
    void Same(string what, int diff, int total) { if (diff != 0) fail++; Console.WriteLine($"{(diff == 0 ? "OK  " : "FAIL")} compiled {what}: {total} records, {diff} different"); }
    Same("CT", xml.Ct.Count(kv => !bin.Ct.TryGetValue(kv.Key, out var b) || b != kv.Value) + Math.Abs(xml.Ct.Count - bin.Ct.Count), xml.Ct.Count);
    Same("UCD", xml.Ucd.Count(kv => !bin.Ucd.TryGetValue(kv.Key, out var b) || b.Name != kv.Value.Name || !b.Vehicles.SequenceEqual(kv.Value.Vehicles)
        || !b.Elements!.SequenceEqual(kv.Value.Elements!) || !b.RoleScores!.SequenceEqual(kv.Value.RoleScores!)
        || (b with { Vehicles = kv.Value.Vehicles, Elements = kv.Value.Elements, RoleScores = kv.Value.RoleScores }) != kv.Value), xml.Ucd.Count);
    // nomi dei mezzi: nel compilato sono char[15], i piu' lunghi restano tagliati
    Same("VCD", xml.Vcd.Count(kv => bin.Vcd.GetValueOrDefault(kv.Key) != (kv.Value.Length > 15 ? kv.Value[..15].Trim() : kv.Value) || bin.VcdInfo[kv.Key] != xml.VcdInfo[kv.Key] || bin.VcdGun[kv.Key] != xml.VcdGun[kv.Key]
        || bin.VcdWeights[kv.Key] != xml.VcdWeights[kv.Key] || !bin.VcdHardpoints[kv.Key].SequenceEqual(xml.VcdHardpoints[kv.Key])), xml.Vcd.Count);
    Same("WCD", xml.Wcd.Count(kv => bin.Wcd.GetValueOrDefault(kv.Key) != kv.Value || bin.WcdWeight[kv.Key] != xml.WcdWeight[kv.Key]), xml.Wcd.Count);
    Same("WLD", xml.Wld.Count(kv => !bin.Wld.TryGetValue(kv.Key, out var b) || b.Name != kv.Value.Name || !b.Weapons.SequenceEqual(kv.Value.Weapons)), xml.Wld.Count);
    Same("SSD", xml.Ssd.Count(kv => !bin.Ssd.TryGetValue(kv.Key, out var b) || !b.OrderBy(p => p.Key).SequenceEqual(kv.Value.Where(p => p.Value != 0).OrderBy(p => p.Key))), xml.Ssd.Count);
  } finally { Directory.Delete(tmpDb, true); }
} else Console.WriteLine($"compiled database not checked: {bmsObjects} has no Falcon4_CT.xml + FALCON4.CT");

// numerazione delle tabelle XML come le carica BMS (dimensione = Num dell'ultimo record + 1, record al posto del suo Num)
{
  void Expect(string what, bool ok) { if (!ok) fail++; Console.WriteLine($"{(ok ? "OK  " : "FAIL")} numbering: {what}"); }
  string T(params int[] n) => "<CTRecords>" + string.Concat(n.Select(x => $"<CT Num=\"{x}\"><Domain>1</Domain></CT>")) + "</CTRecords>";
  var X = TerrainTool.Core.Database.XmlNumbering.CheckText;
  Expect("0..3 in order: no issues", X("a.xml", "CT", T(0, 1, 2, 3)).Count == 0);
  var gx = X("a.xml", "CT", T(0, 1, 4, 5, 9));
  Expect("gaps found and serious in CT", gx.Count == 1 && gx[0].Serious && gx[0].Text.StartsWith("5 missing numbers: 2-3, 6-8"));
  Expect("gaps only a warning in VCD", X("a.xml", "VCD", T(0, 2).Replace("CT", "VCD")).Single().Serious == false);
  var lx = X("a.xml", "CT", T(0, 1, 3, 2));
  Expect("last record lower than the highest", lx.Any(i => i.Serious && i.Text.StartsWith("the last record is 2 but 3 is higher")));
  Expect("duplicate number", X("a.xml", "CT", T(0, 1, 1, 2)).Any(i => i.Text.StartsWith("number used twice: 1")));
  var stock = TerrainTool.Core.Database.XmlNumbering.Check(bmsObjects);
  Expect($"BMS stock tables in {bmsObjects}: {stock.Count} issues", stock.Count == 0);
}

// Korea TvT (solo database compilato): la campagna si legge con il suo database, non con quello di BMS
string tvt = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(bmsObjects)!)!, "Add-On Korea TvT");
string tvtCam = Path.Combine(tvt, "Campaign", "TvT_Start.cam");
if (File.Exists(tvtCam)) {
  var tdb = TerrainTool.Core.Campaign.CampaignDb.Load(new[] { Path.Combine(tvt, "TerrData", "Objects"), bmsObjects }, compiledInFirstDir: true);
  var doc = TerrainTool.Core.Campaign.CampaignDoc.Load(tvtCam, tdb.ClassOf);
  int units = doc.Units?.Units.Count ?? 0;
  bool ok = tdb.Compiled && tdb.Ct.Count == 5261 && doc.Units?.Error == null && units == 675 && doc.Teams?.Error == null;
  if (!ok) fail++;
  Console.WriteLine($"{(ok ? "OK  " : "FAIL")} Korea TvT with its compiled database: {tdb.Ct.Count} CT, {units} units, unit error {doc.Units?.Error ?? "none"}, team error {doc.Teams?.Error ?? "none"}");
} else Console.WriteLine("Korea TvT not installed: not checked");

Console.WriteLine(fail == 0 ? "ALL TESTS OK" : $"{fail} TESTS FAILED");
return fail;
