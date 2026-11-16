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
Console.WriteLine(fail == 0 ? "ALL TESTS OK" : $"{fail} TESTS FAILED");
return fail;
