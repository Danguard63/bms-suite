using System.Globalization;
using System.Text;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// TePlanes.lst della cartella Campaign: gli aerei che si possono scegliere nel Tactical Engagement.
/// Ogni voce: "// nome" e poi Type SubType Specific (del mezzo nel CT) UnitSubType (tipo di squadrone) ID text UnitIcon (risorse dell'interfaccia).
/// </summary>
public sealed class TePlanesFile
{
    public sealed class Entry
    {
        public string Name = "";
        public int Type, SubType, Specific, UnitSubType, Id, Text, Icon;
        public (int, int, int, int) Key => (Type, SubType, Specific, UnitSubType);
        public string Line => string.Create(CultureInfo.InvariantCulture, $"\t{Type}\t{SubType}\t{Specific}\t{UnitSubType}\t{Id}\t{Text}\t{Icon}");
    }

    public string Header = "// Type  SubType  Specific  UnitSubType  ID  text  UnitIcon";
    public List<Entry> Entries { get; } = new();

    public static TePlanesFile Load(string path)
    {
        var f = new TePlanesFile();
        string? pendingName = null;
        bool first = true;
        foreach (var raw in File.ReadAllLines(path, Encoding.Latin1))
        {
            string l = raw.Trim();
            if (l.Length == 0) continue;
            if (l.StartsWith("//"))
            {
                if (first && l.Contains("Type", StringComparison.OrdinalIgnoreCase) && l.Contains("Specific", StringComparison.OrdinalIgnoreCase)) f.Header = l;
                else pendingName = l[2..].Trim();
                first = false;
                continue;
            }
            first = false;
            var p = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 7 || !p.Take(7).All(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))) continue;
            int I(int i) => int.Parse(p[i], CultureInfo.InvariantCulture);
            f.Entries.Add(new Entry { Name = pendingName ?? "", Type = I(0), SubType = I(1), Specific = I(2), UnitSubType = I(3), Id = I(4), Text = I(5), Icon = I(6) });
            pendingName = null;
        }
        return f;
    }

    /// <summary>Come i file di BMS: intestazione, la voce vuota (tutti zero) per prima, poi le altre nell'ordine della lista, ognuna col suo commento.</summary>
    public void Save(string path)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n");
        foreach (var e in Entries.Where(e => e.Type == 0)) sb.Append(e.Line).Append("\r\n");
        foreach (var e in Entries.Where(e => e.Type != 0))
        {
            sb.Append("// ").Append(e.Name).Append("\r\n");
            sb.Append(e.Line).Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString(), Encoding.Latin1);
    }
}

/// <summary>
/// TeUnits.lst (battaglioni) e TeNavalUnits.lst (navi) della cartella Campaign: le unita' che il Tactical Engagement offre
/// per ogni produttore di armamenti ("Side" = il byte Equipment della squadra: 1 Chinese, 2 DPRK, 3 ROK, 4 Soviet, 5 US).
/// Ogni riga: Side SubType Specific del tipo di unita' nel CT.
/// </summary>
public sealed class TeUnitsFile
{
    public List<(int Side, int SubType, int Specific)> Entries { get; } = new();

    public static readonly string[] SideNames = { "", "Chinese", "DPRK", "ROK", "Soviet", "US" };

    public static TeUnitsFile Load(string path)
    {
        var f = new TeUnitsFile();
        foreach (var raw in File.ReadAllLines(path, Encoding.Latin1))
        {
            string l = raw.Trim();
            if (l.Length == 0 || l.StartsWith("//")) continue;
            var p = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3) continue;
            if (int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int a) &&
                int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) &&
                int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int c)) f.Entries.Add((a, b, c));
        }
        return f;
    }

    public bool Has(int side, int sub, int spec) => Entries.Contains((side, sub, spec));

    public void Set(int side, int sub, int spec, bool on)
    {
        if (on && !Has(side, sub, spec)) Entries.Add((side, sub, spec));
        else if (!on) Entries.RemoveAll(e => e == (side, sub, spec));
    }

    /// <summary>Come i file di BMS: un blocco per Side con la sua intestazione; dentro il blocco l'ordine resta quello del file (le nuove in fondo).</summary>
    public void Save(string path)
    {
        var sb = new StringBuilder();
        sb.Append("// Side SubType Specific\r\n");
        foreach (var side in Entries.Select(e => e.Side).Distinct().OrderBy(s => s))
        {
            string name = side > 0 && side < SideNames.Length ? SideNames[side] : "Side " + side;
            string title = $"// Side {side} = {name}";
            sb.Append("\r\n").Append(new string('/', 22)).Append("\r\n");
            sb.Append(title.PadRight(20)).Append("//\r\n");
            sb.Append(new string('/', 22)).Append("\r\n\r\n");
            foreach (var e in Entries.Where(e => e.Side == side))
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"{e.Side}\t{e.SubType}\t{e.Specific}\r\n"));
        }
        File.WriteAllText(path, sb.ToString(), Encoding.Latin1);
    }
}
