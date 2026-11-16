using System.Globalization;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// Il file &lt;teatro&gt;.thr della cartella Campaign: una cella per km² con copertura, rilievo e ferrovia.
/// Formato del file .thr di BMS 4.38:
/// uint16 MaxX (celle verso est), uint16 MaxY (celle verso nord), poi MaxX·MaxY byte, indice = x·MaxY + y,
/// x = km da ovest, y = km da sud. Byte: bit 0-3 copertura, bit 4-5 rilievo, bit 6 strada (BMS 4.38 non lo legge più), bit 7 ferrovia.
/// In 4.38 lo usano ancora: stima dei tempi di marcia, posizione coperta dei battaglioni, "vicino all'acqua" di battaglioni e
/// task force, mappa delle priorità, parte dell'IA dei veicoli nel 3D, danni alle unità.
/// I costi di movimento per il percorso invece vengono dalla LandCoverMap del New Terrain (cache &lt;teatro&gt;.terrain.binpb).
/// </summary>
public sealed class TheaterCells
{
    public enum Cover : byte { Water = 0, Bog = 1, Barren = 2, Plain = 3, Brush = 4, LightForest = 5, HeavyForest = 6, Urban = 7 }
    public enum Relief : byte { Flat = 0, Rough = 1, Hills = 2, Mountains = 3 }

    public int MaxX { get; }
    public int MaxY { get; }
    public byte[] Cells { get; }

    public TheaterCells(int maxX, int maxY, byte[]? cells = null)
    {
        MaxX = maxX; MaxY = maxY;
        Cells = cells ?? new byte[maxX * maxY];
        if (Cells.Length != maxX * maxY) throw new ArgumentException("cells");
    }

    public byte this[int x, int y] { get => Cells[x * MaxY + y]; set => Cells[x * MaxY + y] = value; }
    public static Cover CoverOf(byte c) => (Cover)(c & 0x0F);
    public static Relief ReliefOf(byte c) => (Relief)((c >> 4) & 3);
    public static bool RoadOf(byte c) => (c & 0x40) != 0;
    public static bool RailOf(byte c) => (c & 0x80) != 0;
    public static byte Make(Cover c, Relief r, bool road, bool rail) => (byte)((byte)c | ((byte)r << 4) | (road ? 0x40 : 0) | (rail ? 0x80 : 0));

    public static TheaterCells Read(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 4) throw new InvalidDataException(path + ": too short for a .thr file");
        int mx = BitConverter.ToUInt16(b, 0), my = BitConverter.ToUInt16(b, 2);
        if (mx <= 0 || my <= 0 || b.Length != 4 + (long)mx * my)
            throw new InvalidDataException($"{path}: header {mx}×{my} does not match the size ({b.Length} bytes)");
        return new TheaterCells(mx, my, b[4..]);
    }

    public void Write(string path)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            fs.Write(BitConverter.GetBytes((ushort)MaxX)); fs.Write(BitConverter.GetBytes((ushort)MaxY));
            fs.Write(Cells);
        }
        File.Move(tmp, path, true);
    }

    public static readonly (byte R, byte G, byte B)[] CoverColors =
    {
        (40, 80, 170), (90, 120, 90), (222, 205, 160), (200, 190, 120), (170, 160, 100), (110, 160, 80), (40, 100, 40), (150, 150, 150),
    };
    public static readonly string[] CoverNames = { "Water", "Bog", "Barren", "Plain", "Brush", "Light forest", "Heavy forest", "Urban" };
    public static readonly string[] ReliefNames = { "Flat", "Rough", "Hills", "Mountains" };

    /// <summary>Immagine BGRA px×px (riga 0 = nord): colore della copertura, scurito col rilievo, ferrovie in nero.</summary>
    public byte[] Image(int px, bool shadeRelief = true)
    {
        var o = new byte[px * px * 4];
        for (int r = 0; r < px; r++)
            for (int c = 0; c < px; c++)
            {
                int x = (int)((c + 0.5) * MaxX / px), y = MaxY - 1 - (int)((r + 0.5) * MaxY / px);
                byte v = this[Math.Clamp(x, 0, MaxX - 1), Math.Clamp(y, 0, MaxY - 1)];
                var col = CoverColors[Math.Min(v & 15, 7)];
                double k = shadeRelief && CoverOf(v) != Cover.Water ? 1.0 - 0.13 * (int)ReliefOf(v) : 1.0;
                int i = (r * px + c) * 4;
                if (RailOf(v)) { o[i] = o[i + 1] = o[i + 2] = 0; }
                else { o[i] = (byte)(col.B * k); o[i + 1] = (byte)(col.G * k); o[i + 2] = (byte)(col.R * k); }
                o[i + 3] = 255;
            }
        return o;
    }

    /// <summary>Quante celle per copertura e rilievo.</summary>
    public (long[] Cover, long[] Relief, long Road, long Rail) Stats()
    {
        var cv = new long[16]; var rl = new long[4]; long road = 0, rail = 0;
        foreach (var v in Cells) { cv[v & 15]++; rl[(v >> 4) & 3]++; if (RoadOf(v)) road++; if (RailOf(v)) rail++; }
        return (cv, rl, road, rail);
    }

    public string Describe()
    {
        var (cv, rl, road, rail) = Stats();
        double n = Cells.Length;
        string Pct(long v) => (100 * v / n).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        var cov = Enumerable.Range(0, 16).Where(i => cv[i] > 0).OrderByDescending(i => cv[i]).Select(i => (i < 8 ? CoverNames[i] : "type " + i) + " " + Pct(cv[i]));
        var rel = Enumerable.Range(0, 4).Where(i => rl[i] > 0).Select(i => ReliefNames[i] + " " + Pct(rl[i]));
        return $"{MaxX}×{MaxY} km. Cover: {string.Join(", ", cov)}. Relief: {string.Join(", ", rel)}. Road bit {Pct(road)}, rail {Pct(rail)}.";
    }

    /// <summary>Confronto con un altro .thr della stessa misura: celle con la stessa copertura, e acqua in comune (Jaccard).</summary>
    public string Compare(TheaterCells other)
    {
        if (other.MaxX != MaxX || other.MaxY != MaxY) return $"different size ({other.MaxX}×{other.MaxY} vs {MaxX}×{MaxY})";
        long same = 0, wa = 0, wb = 0, both = 0;
        for (int i = 0; i < Cells.Length; i++)
        {
            byte a = Cells[i], b = other.Cells[i];
            if ((a & 15) == (b & 15)) same++;
            bool x = (a & 15) == 0, y = (b & 15) == 0;
            if (x) wa++; if (y) wb++; if (x && y) both++;
        }
        long uni = wa + wb - both;
        return $"same cover in {100.0 * same / Cells.Length:0.#}% of the cells; water in common {(uni > 0 ? 100.0 * both / uni : 100):0.#}% (Jaccard)";
    }
}

/// <summary>
/// Costruisce il .thr dal New Terrain finito (cartella NewTerrain del teatro o Output\NewTerrain del progetto):
/// acqua = pelo dell'acqua (WaterHeightMap) sopra il terreno (HeightMap), come la disegna BMS; copertura dalla LandCoverMap
/// (tradotta con LandCoverTypes.txt); rilievo dal dislivello dentro ogni km²; strade e ferrovie dai pixel ROAD/RAIL della LandCoverMap.
/// Legge le mappe a fasce di 1 km, senza caricarle intere.
/// </summary>
public static class TheaterCellsBuilder
{
    // tipi di terreno del New Terrain (id di LandCoverTypes.txt, uguali in Korea e nel Terrain Tool)
    private const int NoData = 0, Water = 1, River = 2, Swamp = 3, Plains = 4, Brush = 5, ThinForest = 6, ThickForest = 7, Rocky = 8,
        Urban = 9, Road = 10, Rail = 11, Bridge = 12, Runway = 13, Airbase = 14, Obj = 15, Shrubs = 16, SnowIce = 17, Cropland = 18, Moss = 19;
    private const int NT = 20;

    public sealed class Options
    {
        /// <summary>Frazione d'acqua oltre la quale la cella è acqua.</summary>
        public double WaterFraction { get; set; } = 0.5;
        /// <summary>Frazione di città (sulla terra) oltre la quale la cella è urbana.</summary>
        public double UrbanFraction { get; set; } = 0.25;
        /// <summary>Dislivello (m) dentro il km² sotto il quale: piano / mosso / colline; sopra l'ultimo: montagna.</summary>
        public double FlatM { get; set; } = 40;
        public double RoughM { get; set; } = 120;
        public double HillsM { get; set; } = 350;
        /// <summary>Pixel di strada o ferrovia (sul km²) per accendere il bit.</summary>
        public int MinLinePixels { get; set; } = 3;
    }

    public sealed record Result(TheaterCells Cells, List<string> Notes);

    /// <summary>Tabella valore della LandCoverMap → tipo (0-19) da LandCoverTypes.txt; senza file, identità.</summary>
    public static byte[] LandCoverTable(string? typesTxt, List<string> notes)
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) t[i] = (byte)(i < NT ? i : NoData);
        if (typesTxt == null || !File.Exists(typesTxt)) { notes.Add("LandCoverTypes.txt not found: map values used as ground types"); return t; }
        string txt = File.ReadAllText(typesTxt);
        int def = Regex.Match(txt, @"defaultIndex:\s*(\d+)") is { Success: true } dm ? int.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture) : NoData;
        for (int i = 0; i < 256; i++) t[i] = (byte)Math.Min(def, NT - 1);
        int mapped = 0;
        foreach (Match m in Regex.Matches(txt, @"protoLandCoverType\s*\{(.*?)\}", RegexOptions.Singleline))
        {
            var idm = Regex.Match(m.Groups[1].Value, @"\bid:\s*(\d+)");
            if (!idm.Success) continue;
            int id = int.Parse(idm.Groups[1].Value, CultureInfo.InvariantCulture);
            if (id >= NT) continue;
            foreach (Match v in Regex.Matches(m.Groups[1].Value, @"landcoverMapIDs:\s*(\d+)"))
            {
                int val = int.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture);
                if (val is >= 0 and < 256) { t[val] = (byte)id; mapped++; }
            }
        }
        notes.Add($"LandCoverTypes.txt: {mapped} map values translated, the others → type {def}");
        return t;
    }

    private static int Side(string path, int bytesPerPixel)
    {
        long px = new FileInfo(path).Length / bytesPerPixel;
        int s = (int)Math.Round(Math.Sqrt(px));
        return (long)s * s == px ? s : -1;
    }

    /// <summary>
    /// newTerrainDir = cartella NewTerrain (con HeightMaps e GroundTypes); km = lato del teatro in km (= celle del .thr).
    /// </summary>
    public static Result Build(string newTerrainDir, int km, Options? opt = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        opt ??= new Options();
        var notes = new List<string>();
        string hPath = Path.Combine(newTerrainDir, "HeightMaps", "HeightMap.raw");
        string wPath = Path.Combine(newTerrainDir, "HeightMaps", "WaterHeightMap.raw");
        string lPath = Path.Combine(newTerrainDir, "GroundTypes", "LandCoverMap.raw");
        if (!File.Exists(hPath)) throw new FileNotFoundException("HeightMap.raw not found in " + Path.Combine(newTerrainDir, "HeightMaps"), hPath);
        int sh = Side(hPath, 2);
        if (sh < km) throw new InvalidDataException("HeightMap.raw is not a square 16-bit RAW of at least one pixel per km");
        int sw = File.Exists(wPath) ? Side(wPath, 2) : -1;
        int sl = File.Exists(lPath) ? Side(lPath, 1) : -1;
        if (sw <= 0) notes.Add("WaterHeightMap.raw missing or not square: water from the LandCoverMap only");
        if (sl <= 0) notes.Add("LandCoverMap.raw missing or not square: cover from water and relief only (land = plain)");
        if (sw <= 0 && sl <= 0) throw new InvalidDataException("Neither WaterHeightMap.raw nor LandCoverMap.raw: no water information");
        var table = sl > 0 ? LandCoverTable(Path.Combine(newTerrainDir, "GroundTypes", "LandCoverTypes.txt"), notes) : new byte[256];
        notes.Insert(0, $"Sources: HeightMap {sh}² ({km * 1000.0 / sh:0.##} m/px)" + (sw > 0 ? $", WaterHeightMap {sw}²" : "") + (sl > 0 ? $", LandCoverMap {sl}² ({km * 1000.0 / sl:0.##} m/px)" : ""));

        var res = new TheaterCells(km, km);
        using var fh = new FileStream(hPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var fw = sw > 0 ? new FileStream(wPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20) : null;
        using var fl = sl > 0 ? new FileStream(lPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20) : null;

        var hRow = new byte[sh * 2]; var wRow = sw > 0 ? new byte[sw * 2] : Array.Empty<byte>(); var lRow = sl > 0 ? new byte[sl] : Array.Empty<byte>();
        // La cella di BMS e' 3279.98 ft (SimToGrid), non 1000 m: il teatro (km × 1000 m) e' un po' piu' largo di km celle
        // e la striscia in piu' (a est e a nord, ~0.5 km su 2048) non appartiene a nessuna cella.
        double S = km * 1000.0, cellM = CampaignDoc.BmsFtPerGrid * 0.3048;
        int[] Cols(int side) { var a = new int[Math.Max(side, 0)]; for (int c = 0; c < side; c++) { int x = (int)Math.Floor((c + 0.5) * S / side / cellM); a[c] = x < km ? x : -1; } return a; }
        (int, int) Rows(int side, int y)
        {
            double px = S / side;
            int r0 = (int)Math.Floor((S - (y + 1) * cellM) / px - 0.5) + 1, r1 = (int)Math.Floor((S - y * cellM) / px - 0.5) + 1;
            return (Math.Clamp(r0, 0, side), Math.Clamp(r1, 0, side));
        }
        var hCol = Cols(sh); var lCol = Cols(sl); var wCol = Cols(sw);
        var wColH = new int[Math.Max(sw, 0)];
        for (int c = 0; c < sw; c++) wColH[c] = Math.Min(sh - 1, (int)((c + 0.5) * sh / sw));

        var hMin = new float[km]; var hMax = new float[km];
        var wet = new int[km]; var wTot = new int[km];
        var lc = new int[km * NT];
        long counts = 0;
        for (int b = 0; b < km; b++) // fasce da nord
        {
            ct.ThrowIfCancellationRequested();
            int y = km - 1 - b;
            Array.Fill(hMin, float.MaxValue); Array.Fill(hMax, float.MinValue);
            Array.Clear(wet); Array.Clear(wTot); Array.Clear(lc);
            var (h0, h1) = Rows(sh, y);
            if (h1 <= h0) continue;
            // quote della fascia (tenute per confrontarle con il pelo dell'acqua)
            var band = new short[(h1 - h0) * sh];
            fh.Position = (long)h0 * sh * 2;
            for (int r = h0; r < h1; r++)
            {
                fh.ReadExactly(hRow);
                var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(hRow);
                span.CopyTo(band.AsSpan((r - h0) * sh, sh));
                for (int c = 0; c < sh; c++)
                {
                    float v = span[c]; int x = hCol[c];
                    if (x < 0) continue;
                    if (v < hMin[x]) hMin[x] = v;
                    if (v > hMax[x]) hMax[x] = v;
                }
            }
            if (fw != null)
            {
                // righe dell'acqua il cui centro cade nella fascia
                var (w0, w1) = Rows(sw, y);
                if (w1 > w0) fw.Position = (long)w0 * sw * 2;
                for (int r = w0; r < w1; r++)
                {
                    fw.ReadExactly(wRow);
                    var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(wRow);
                    int hr = Math.Clamp((int)((r + 0.5) * sh / sw), h0, h1 - 1) - h0;
                    for (int c = 0; c < sw; c++)
                    {
                        int x = wCol[c];
                        if (x < 0) continue;
                        wTot[x]++;
                        if (span[c] > band[hr * sh + wColH[c]] + 0.5f) wet[x]++;
                    }
                }
            }
            if (fl != null)
            {
                var (l0, l1) = Rows(sl, y);
                fl.Position = (long)l0 * sl;
                for (int r = l0; r < l1; r++)
                {
                    fl.ReadExactly(lRow);
                    for (int c = 0; c < sl; c++) { int x = lCol[c]; if (x >= 0) lc[x * NT + table[lRow[c]]]++; }
                }
            }
            for (int x = 0; x < km; x++)
            {
                if (hMax[x] < hMin[x]) { hMin[x] = hMax[x] = 0; }
                res[x, y] = Classify(x, hMin[x], hMax[x], wet[x], wTot[x], lc.AsSpan(x * NT, NT), opt);
                counts++;
            }
            if ((b & 15) == 0) progress?.Report((double)b / km);
        }
        progress?.Report(1);
        notes.Add(res.Describe());
        return new Result(res, notes);
    }

    private static byte Classify(int x, float hMin, float hMax, int wet, int wTot, ReadOnlySpan<int> lc, Options o)
    {
        long lTot = 0; foreach (var v in lc) lTot += v;
        double waterFrac = wTot > 0 ? (double)wet / wTot : lTot > 0 ? (double)(lc[Water] + lc[River]) / lTot : 0;
        bool road = lc[Road] + lc[Bridge] >= o.MinLinePixels, rail = lc[Rail] >= o.MinLinePixels;
        if (waterFrac >= o.WaterFraction) return TheaterCells.Make(TheaterCells.Cover.Water, TheaterCells.Relief.Flat, road, rail);

        // rilievo: dislivello nel km² (HeightMap in piedi)
        double rangeM = hMax > hMin ? (hMax - hMin) * 0.3048 : 0;
        var relief = rangeM < o.FlatM ? TheaterCells.Relief.Flat : rangeM < o.RoughM ? TheaterCells.Relief.Rough
                   : rangeM < o.HillsM ? TheaterCells.Relief.Hills : TheaterCells.Relief.Mountains;

        // copertura: solo i pixel di terra "di superficie" (strade, ponti, piste, oggetti non contano)
        long bog = lc[Swamp], urban = lc[Urban],
             plain = lc[Plains] + lc[Cropland] + lc[Moss] + lc[Airbase] + lc[Runway] + lc[NoData],
             barren = lc[Brush] + lc[Rocky] + lc[SnowIce],
             brush = lc[Shrubs], thin = lc[ThinForest], thick = lc[ThickForest];
        long land = bog + urban + plain + barren + brush + thin + thick;
        TheaterCells.Cover cover;
        if (land == 0) cover = TheaterCells.Cover.Plain;
        else if (urban >= o.UrbanFraction * land) cover = TheaterCells.Cover.Urban;
        else
        {
            var cands = new (long N, TheaterCells.Cover C)[]
            {
                (bog, TheaterCells.Cover.Bog), (plain, TheaterCells.Cover.Plain), (barren, TheaterCells.Cover.Barren),
                (brush, TheaterCells.Cover.Brush), (thin, TheaterCells.Cover.LightForest), (thick, TheaterCells.Cover.HeavyForest), (urban, TheaterCells.Cover.Urban),
            };
            // bosco: insieme rado+fitto, poi il prevalente dei due
            if (thin + thick >= 0.4 * land) cover = thick >= thin ? TheaterCells.Cover.HeavyForest : TheaterCells.Cover.LightForest;
            else cover = cands.OrderByDescending(c => c.N).First().C;
        }
        return TheaterCells.Make(cover, relief, road, rail);
    }
}

/// <summary>
/// La cache &lt;teatro&gt;.terrain.binpb che BMS 4.38 scrive nella cartella Campaign al caricamento della campagna
/// (campaign_terrain.CampaignTerrainData): una cella per km² con i tipi del New Terrain, i ponti degli obiettivi e le piste.
/// Da qui vengono i costi di movimento delle unità di terra.
/// </summary>
public sealed class CampaignTerrainCache
{
    [Flags]
    public enum T : uint
    {
        Unspecified = 0x1, NoData = 0x2, Water = 0x4, River = 0x8, Flat = 0x10, FlatWoods = 0x20, Rough = 0x40, RoughWoods = 0x80,
        Highlands = 0x100, HighlandWoods = 0x200, Swamp = 0x400, Mountain = 0x800, Urban = 0x2000, Road = 0x8000, Bridge = 0x20000, Runway = 0x40000,
    }

    public int SizeX { get; }
    public int SizeY { get; }
    /// <summary>[y·SizeX + x], y = km da sud.</summary>
    public uint[] Flags { get; }

    private CampaignTerrainCache(int sx, int sy, uint[] f) { SizeX = sx; SizeY = sy; Flags = f; }
    public uint this[int x, int y] => Flags[y * SizeX + x];

    private static ulong Varint(byte[] b, ref int p)
    {
        ulong r = 0; int s = 0;
        while (true) { byte c = b[p++]; r |= (ulong)(c & 0x7F) << s; if (c < 0x80) return r; s += 7; }
    }

    private static void Skip(byte[] b, ref int p, int wire)
    {
        switch (wire)
        {
            case 0: Varint(b, ref p); break;
            case 1: p += 8; break;
            case 2: p += (int)Varint(b, ref p); break;
            case 5: p += 4; break;
            default: throw new InvalidDataException("protobuf wire type " + wire);
        }
    }

    public static CampaignTerrainCache Read(string path)
    {
        var b = File.ReadAllBytes(path);
        int p = 0, tStart = -1, tEnd = -1;
        while (p < b.Length)
        {
            ulong k = Varint(b, ref p); int f = (int)(k >> 3), w = (int)(k & 7);
            if (f == 1 && w == 2) { int len = (int)Varint(b, ref p); tStart = p; tEnd = p + len; p = tEnd; }
            else Skip(b, ref p, w);
        }
        if (tStart < 0) throw new InvalidDataException(path + ": no terrain message");
        // prima passata: dimensioni
        int sx = 0, sy = 0;
        p = tStart;
        var cells = new List<(int X, int Y, uint F)>();
        while (p < tEnd)
        {
            ulong k = Varint(b, ref p); int f = (int)(k >> 3), w = (int)(k & 7);
            if (f == 1 && w == 2)
            {
                int len = (int)Varint(b, ref p), e = p + len, x = 0, y = 0; uint fl = 0;
                while (p < e)
                {
                    ulong k2 = Varint(b, ref p); int f2 = (int)(k2 >> 3), w2 = (int)(k2 & 7);
                    if (f2 == 1 && w2 == 0) x = (int)Varint(b, ref p);
                    else if (f2 == 2 && w2 == 0) y = (int)Varint(b, ref p);
                    else if (f2 == 3 && w2 == 0) fl |= (uint)Varint(b, ref p);
                    else if (f2 == 3 && w2 == 2) { int l2 = (int)Varint(b, ref p), e2 = p + l2; while (p < e2) fl |= (uint)Varint(b, ref p); }
                    else Skip(b, ref p, w2);
                }
                cells.Add((x, y, fl));
            }
            else if (f == 2 && w == 0) sx = (int)Varint(b, ref p);
            else if (f == 3 && w == 0) sy = (int)Varint(b, ref p);
            else Skip(b, ref p, w);
        }
        if (sx <= 0) sx = cells.Count == 0 ? 0 : cells.Max(c => c.X) + 1;
        if (sy <= 0) sy = cells.Count == 0 ? 0 : cells.Max(c => c.Y) + 1;
        var arr = new uint[sx * sy];
        foreach (var c in cells) if (c.X < sx && c.Y < sy) arr[c.Y * sx + c.X] = c.F;
        return new CampaignTerrainCache(sx, sy, arr);
    }

    /// <summary>Colore di una cella come la vede la campagna: acqua, fiumi, rilievo/boschi, città, strade, ponti, piste.</summary>
    public (byte R, byte G, byte B) CellColor(int x, int y)
    {
        var f = (T)this[x, y];
        (int R, int G, int B) col =
            f.HasFlag(T.Runway) ? (0, 0, 0) :
            f.HasFlag(T.Bridge) ? (255, 215, 0) :
            f.HasFlag(T.Road) ? (215, 45, 35) :
            f.HasFlag(T.River) ? (60, 140, 255) :
            f.HasFlag(T.Water) ? (40, 80, 170) :
            f.HasFlag(T.Urban) ? (150, 150, 150) :
            f.HasFlag(T.Swamp) ? (90, 120, 90) :
            f.HasFlag(T.Mountain) ? (130, 110, 100) :
            f.HasFlag(T.HighlandWoods) ? (40, 100, 40) :
            f.HasFlag(T.Highlands) ? (170, 140, 110) :
            f.HasFlag(T.RoughWoods) ? (70, 130, 60) :
            f.HasFlag(T.Rough) ? (190, 160, 120) :
            f.HasFlag(T.FlatWoods) ? (120, 170, 90) :
            f.HasFlag(T.Flat) ? (222, 205, 160) : (230, 230, 230);
        return ((byte)col.R, (byte)col.G, (byte)col.B);
    }

    /// <summary>Immagine BGRA px×px (riga 0 = nord) come la vede la campagna.</summary>
    public byte[] Image(int px)
    {
        var o = new byte[px * px * 4];
        for (int r = 0; r < px; r++)
            for (int c = 0; c < px; c++)
            {
                int x = Math.Clamp((int)((c + 0.5) * SizeX / px), 0, SizeX - 1), y = Math.Clamp(SizeY - 1 - (int)((r + 0.5) * SizeY / px), 0, SizeY - 1);
                var col = CellColor(x, y);
                int i = (r * px + c) * 4;
                o[i] = col.B; o[i + 1] = col.G; o[i + 2] = col.R; o[i + 3] = 255;
            }
        return o;
    }

    /// <summary>Acqua in comune con un .thr (Jaccard), per vedere se il .thr corrisponde al terreno.</summary>
    public string CompareWater(TheaterCells thr)
    {
        if (thr.MaxX != SizeX || thr.MaxY != SizeY) return $"different size ({thr.MaxX}×{thr.MaxY} vs {SizeX}×{SizeY})";
        long a = 0, b = 0, both = 0;
        for (int y = 0; y < SizeY; y++)
            for (int x = 0; x < SizeX; x++)
            {
                bool w1 = (this[x, y] & (uint)(T.Water | T.River)) != 0, w2 = TheaterCells.CoverOf(thr[x, y]) == TheaterCells.Cover.Water;
                if (w1) a++; if (w2) b++; if (w1 && w2) both++;
            }
        long u = a + b - both;
        return $"water in common with the BMS campaign terrain: {(u > 0 ? 100.0 * both / u : 100):0.#}% (Jaccard)";
    }
}

/// <summary>
/// Controllo dei collegamenti tra obiettivi sulla mappa di campagna di BMS (cache .terrain.binpb): per ogni collegamento
/// usato dalle unità di terra cerca, in un corridoio attorno alla retta tra i due obiettivi, un percorso di celle da 1 km
/// che BMS lascia passare. Regole di BMS 4.38: strada o città = si passa; ponte = si passa
/// (se l'obiettivo ponte è intatto); acqua o fiume senza strada/ponte = no; il resto (rilievo) = si passa.
/// </summary>
public static class CampaignTerrainCheck
{
    public sealed record Issue(int CampA, string NameA, int CampB, string NameB, double Km, int WaterX, int WaterY)
    {
        public override string ToString() => $"{NameA} ({CampA}) – {NameB} ({CampB}), {Km:0} km: water at {WaterX},{WaterY} km with no road or bridge";
    }

    private const uint Water = 0x4, River = 0x8, Urban = 0x2000, Road = 0x8000, Bridge = 0x20000;

    /// <summary>Cella della griglia in cui BMS mette l'obiettivo (dalla posizione, celle di 3279.98 ft).</summary>
    public static (int X, int Y) Cell(CampObjective o) =>
        o.HasSim ? (CampaignDoc.SimToGrid(o.SimEast), CampaignDoc.SimToGrid(o.SimNorth)) : (o.X, o.Y);

    /// <summary>
    /// Una cella si attraversa se ha strada o citta', se c'e' un obiettivo ponte (ponte intatto), oppure se non e' acqua/fiume.
    /// Una cella ponte della LandCoverMap senza obiettivo ponte per BMS costa 1000 (= non si passa).
    /// </summary>
    public static bool Passable(uint f, bool bridgeObjective) => (f & (Road | Urban)) != 0 || bridgeObjective || (f & (Water | River)) == 0;

    /// <summary>Coppie di CampId collegate e usate dalle unita' di terra (Costs[2]/[3] = ruote/cingoli: 255 su entrambi = non di terra), una volta sola.</summary>
    public static List<(int A, int B)> GroundLinks(CampaignDoc doc)
    {
        var byId = doc.Objectives.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        var done = new HashSet<(int, int)>();
        var res = new List<(int, int)>();
        foreach (var a in doc.Objectives)
            foreach (var l in a.Links)
            {
                if (!byId.TryGetValue(l.To, out var b) || a.CampId == b.CampId) continue;
                if (l.Costs.Length > 3 && l.Costs[2] == 255 && l.Costs[3] == 255) continue;
                var key = a.CampId < b.CampId ? (a.CampId, b.CampId) : (b.CampId, a.CampId);
                if (done.Add(key)) res.Add(key);
            }
        return res;
    }

    /// <summary>Collegamenti di terra senza passaggio. Celle ponte: quelle con il flag BRIDGE di BMS in cui c'e' un obiettivo.</summary>
    public static (List<Issue> Issues, int Checked) Check(CampaignDoc doc, CampaignTerrainCache map, CancellationToken ct = default)
    {
        var issues = new List<Issue>();
        var byCamp = doc.Objectives.GroupBy(o => (int)o.CampId).ToDictionary(g => g.Key, g => g.First());
        var objCells = doc.Objectives.Select(Cell).ToHashSet();
        bool IsBridge(int x, int y) => (map[x, y] & Bridge) != 0 && objCells.Contains((x, y));
        var links = GroundLinks(doc);
        foreach (var (ia, ib) in links)
        {
            ct.ThrowIfCancellationRequested();
            var a = byCamp[ia]; var b = byCamp[ib];
            var (ax, ay) = Cell(a); var (bx, by) = Cell(b);
            if (Reachable(map, ax, ay, bx, by, IsBridge)) continue;
            var (wx, wy) = FirstWater(map, ax, ay, bx, by, IsBridge);
            issues.Add(new Issue(a.CampId, a.Name, b.CampId, b.Name, Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)), wx, wy));
        }
        return (issues, links.Count);
    }

    private static bool In(CampaignTerrainCache m, int x, int y) => x >= 0 && y >= 0 && x < m.SizeX && y < m.SizeY;

    /// <summary>Rettangolo in cui si cerca il percorso tra due celle: attorno ai due obiettivi, allargato della meta' della distanza (almeno 8 km).</summary>
    public static (int X0, int Y0, int X1, int Y1) Corridor(CampaignTerrainCache m, int ax, int ay, int bx, int by)
    {
        double dist = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
        int mg = Math.Max(8, (int)(dist / 2));
        return (Math.Max(0, Math.Min(ax, bx) - mg), Math.Max(0, Math.Min(ay, by) - mg), Math.Min(m.SizeX - 1, Math.Max(ax, bx) + mg), Math.Min(m.SizeY - 1, Math.Max(ay, by) + mg));
    }

    /// <summary>Ricerca in ampiezza (8 vicini) nel corridoio. bridge(x, y) = c'e' un obiettivo ponte in quella cella (default: flag BRIDGE).</summary>
    public static bool Reachable(CampaignTerrainCache m, int ax, int ay, int bx, int by, Func<int, int, bool>? bridge = null)
    {
        if (!In(m, ax, ay) || !In(m, bx, by)) return true;
        bridge ??= (x, y) => (m[x, y] & Bridge) != 0;
        var (x0, y0, x1, y1) = Corridor(m, ax, ay, bx, by);
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var seen = new bool[w * h];
        var q = new Queue<(int, int)>();
        q.Enqueue((ax, ay)); seen[(ay - y0) * w + ax - x0] = true;
        while (q.Count > 0)
        {
            var (x, y) = q.Dequeue();
            if (x == bx && y == by) return true;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx == 0 && dy == 0) || nx < x0 || ny < y0 || nx > x1 || ny > y1) continue;
                    int k = (ny - y0) * w + nx - x0;
                    if (seen[k]) continue;
                    if (!(nx == bx && ny == by) && !Passable(m[nx, ny], bridge(nx, ny))) continue;
                    seen[k] = true; q.Enqueue((nx, ny));
                }
        }
        return false;
    }

    /// <summary>Prima cella d'acqua non passabile sulla retta tra i due obiettivi (dove manca il passaggio), o il punto medio.</summary>
    private static (int, int) FirstWater(CampaignTerrainCache m, int ax, int ay, int bx, int by, Func<int, int, bool> bridge)
    {
        int steps = Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay));
        for (int i = 1; i < steps; i++)
        {
            int x = ax + (int)Math.Round((double)(bx - ax) * i / steps), y = ay + (int)Math.Round((double)(by - ay) * i / steps);
            if (In(m, x, y) && !Passable(m[x, y], bridge(x, y))) return (x, y);
        }
        return ((ax + bx) / 2, (ay + by) / 2);
    }

    /// <summary>Ritaglio BGRA (px×px, riga 0 = nord) della mappa di campagna attorno a una cella, con la cella segnata in rosso.</summary>
    public static byte[] Zoom(CampaignTerrainCache m, int cx, int cy, int halfKm, int px, IEnumerable<(int X, int Y)>? marks = null)
    {
        var full = new byte[px * px * 4];
        int side = 2 * halfKm + 1;
        for (int r = 0; r < px; r++)
            for (int c = 0; c < px; c++)
            {
                int x = cx - halfKm + c * side / px, y = cy + halfKm - r * side / px;
                int i = (r * px + c) * 4;
                if (!In(m, x, y)) { full[i + 3] = 255; continue; }
                var col = m.CellColor(x, y);
                full[i] = col.B; full[i + 1] = col.G; full[i + 2] = col.R; full[i + 3] = 255;
            }
        foreach (var (x, y) in marks ?? Enumerable.Empty<(int, int)>())
        {
            int cell = Math.Max(2, px / side);
            int c0 = (x - (cx - halfKm)) * px / side, r0 = ((cy + halfKm) - y) * px / side;
            int a0 = r0 - 6, a1 = r0 + cell + 5, b0 = c0 - 6, b1 = c0 + cell + 5;   // anello di 3 px attorno alla cella
            for (int r = a0; r <= a1; r++)
                for (int c = b0; c <= b1; c++)
                {
                    bool ring = r < a0 + 3 || r > a1 - 3 || c < b0 + 3 || c > b1 - 3;
                    if (!ring || r < 0 || c < 0 || r >= px || c >= px) continue;
                    int i = (r * px + c) * 4; full[i] = 255; full[i + 1] = 0; full[i + 2] = 255;
                }
        }
        return full;
    }
}

/// <summary>
/// Sceglie dove mettere la cella ponte: BMS segna come ponte solo il km dell'obiettivo ponte (celle
/// della posizione), e una cella di fiume si attraversa solo li'. Per ogni ponte vicino a un collegamento bloccato prova le celle
/// entro maxShift km e tiene lo spostamento che sblocca piu' collegamenti senza bloccarne nessuno. Greedy, un ponte alla volta.
/// </summary>
public static class BridgeCellPlanner
{
    public sealed record Shift(int CampId, int FromX, int FromY, int ToX, int ToY, int Fixed);

    public static List<Shift> Plan(CampaignTerrainCache map, IReadOnlyList<(int A, int B)> links, Dictionary<int, (int X, int Y)> cell,
        IReadOnlyCollection<int> bridges, int maxShift = 2, CancellationToken ct = default)
    {
        var res = new List<Shift>();
        var links2 = links.Where(l => cell.ContainsKey(l.A) && cell.ContainsKey(l.B)).ToList();
        var bridgeCount = new Dictionary<(int, int), int>();
        foreach (var b in bridges) if (cell.TryGetValue(b, out var c)) bridgeCount[c] = bridgeCount.GetValueOrDefault(c) + 1;
        bool IsBridge(int x, int y) => bridgeCount.ContainsKey((x, y));
        bool Ok((int A, int B) l) { var a = cell[l.A]; var b = cell[l.B]; return CampaignTerrainCheck.Reachable(map, a.X, a.Y, b.X, b.Y, IsBridge); }
        var ok = links2.ToDictionary(l => l, Ok);
        bool Touches((int A, int B) l, (int X, int Y) p)
        {
            var a = cell[l.A]; var b = cell[l.B];
            var (x0, y0, x1, y1) = CampaignTerrainCheck.Corridor(map, a.X, a.Y, b.X, b.Y);
            return p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;
        }
        void Move(int id, (int X, int Y) to)
        {
            var from = cell[id];
            if (--bridgeCount[from] == 0) bridgeCount.Remove(from);
            cell[id] = to; bridgeCount[to] = bridgeCount.GetValueOrDefault(to) + 1;
        }
        var moved = new HashSet<int>();
        for (int iter = 0; iter < 200; iter++)
        {
            ct.ThrowIfCancellationRequested();
            var blocked = ok.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();
            if (blocked.Count == 0) break;
            (int Id, (int X, int Y) To, int Fixed, int Dist)? best = null;
            foreach (var bId in bridges)
            {
                if (moved.Contains(bId) || !cell.TryGetValue(bId, out var bc)) continue;
                // ponti vicini (3 km) a un collegamento bloccato o suo estremo
                if (!blocked.Any(l => Near(cell[l.A], bc, 3) || Near(cell[l.B], bc, 3) || (Touches(l, bc) && LineNear(cell[l.A], cell[l.B], bc, 3)))) continue;
                for (int dy = -maxShift; dy <= maxShift; dy++)
                    for (int dx = -maxShift; dx <= maxShift; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var to = (bc.X + dx, bc.Y + dy);
                        if (to.Item1 < 0 || to.Item2 < 0 || to.Item1 >= map.SizeX || to.Item2 >= map.SizeY) continue;
                        var affected = links2.Where(l => l.A == bId || l.B == bId || Touches(l, bc) || Touches(l, to)).ToList();
                        Move(bId, to);
                        int fixedN = 0, broken = 0;
                        foreach (var l in affected)
                        {
                            bool now = Ok(l);
                            if (now && !ok[l]) fixedN++;
                            else if (!now && ok[l]) { broken++; break; }
                        }
                        Move(bId, bc);
                        if (broken > 0 || fixedN == 0) continue;
                        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        if (best == null || fixedN > best.Value.Fixed || (fixedN == best.Value.Fixed && dist < best.Value.Dist)) best = (bId, to, fixedN, dist);
                    }
            }
            if (best == null) break;
            var (id, tgt, fx, _) = best.Value;
            var fromCell = cell[id];
            Move(id, tgt);
            moved.Add(id);
            foreach (var l in links2.Where(l => l.A == id || l.B == id || Touches(l, fromCell) || Touches(l, tgt)).ToList()) ok[l] = Ok(l);
            res.Add(new Shift(id, fromCell.X, fromCell.Y, tgt.X, tgt.Y, fx));
        }
        return res;
    }

    private static bool Near((int X, int Y) a, (int X, int Y) b, int d) => Math.Abs(a.X - b.X) <= d && Math.Abs(a.Y - b.Y) <= d;

    /// <summary>La cella p dista al piu' d celle dal segmento a-b.</summary>
    private static bool LineNear((int X, int Y) a, (int X, int Y) b, (int X, int Y) p, int d)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y, len2 = vx * vx + vy * vy;
        double t = len2 > 0 ? Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2, 0, 1) : 0;
        double qx = a.X + t * vx - p.X, qy = a.Y + t * vy - p.Y;
        return qx * qx + qy * qy <= d * d;
    }
}
