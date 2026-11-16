using System.Globalization;

namespace TerrainTool.Core.Theater;

/// <summary>Polilinee di coste (C) e confini (B) per la mappa di verifica (Natural Earth 1:50m).</summary>
public sealed class BaseMapLine
{
    public char Kind { get; init; }
    public List<(double Lat, double Lon)> Points { get; } = new();
}

public static class BaseMap
{
    public static List<BaseMapLine> Load(string path)
    {
        var ci = CultureInfo.InvariantCulture;
        var res = new List<BaseMapLine>();
        if (!File.Exists(path)) return res;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length < 3 || line[0] == '#') continue;
            var l = new BaseMapLine { Kind = line[0] };
            foreach (var tok in line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int c = tok.IndexOf(',');
                if (c < 0) continue;
                l.Points.Add((double.Parse(tok[(c + 1)..], ci), double.Parse(tok[..c], ci)));
            }
            if (l.Points.Count > 1) res.Add(l);
        }
        return res;
    }
}
