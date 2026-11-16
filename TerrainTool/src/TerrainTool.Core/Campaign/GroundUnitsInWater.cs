using System.Globalization;
using System.Text;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// Unita' di terra (brigate e battaglioni) messe dall'add-on in un km che per BMS e' acqua (mare, lago, fiume) sulla mappa di
/// campagna (cache .terrain.binpb). Con la costa vera del terreno nuovo capita spesso (es. Hawk e camion in mare davanti ad
/// Al Ruwais, rifornimenti sul fiume a Bubiyan): BMS non trova un percorso (GetUnitGridPathNew failed) e l'unita' resta ferma.
/// Si spostano nel km di terra passabile piu' vicino (entro MaxKm), preferendo la stessa zona di terra dell'obiettivo assegnato
/// e altrimenti la terraferma (zone grandi), non un isolotto. Le unita' gia' su un'isola restano dove sono (presidi veri).
/// </summary>
public sealed class GroundUnitsInWater
{
    public sealed record Move(string File, int CampId, string Unit, byte Owner, int FromX, int FromY, int ToX, int ToY, string Near);

    public const int MaxKm = 12;
    /// <summary>Zone di terra piu' piccole di cosi' (km²) sono isole: non ci si porta un'unita' che non e' gia' li'.</summary>
    public const int IslandKm2 = 2000;

    private readonly CampaignTerrainCache _map;
    private readonly int[,] _label;
    private readonly List<int> _size = new() { 0 };

    public GroundUnitsInWater(CampaignTerrainCache map)
    {
        _map = map;
        _label = new int[map.SizeX, map.SizeY];
        var q = new Queue<(int, int)>();
        for (int x = 0; x < map.SizeX; x++)
            for (int y = 0; y < map.SizeY; y++)
            {
                if (_label[x, y] != 0 || !Passable(x, y)) continue;
                int id = _size.Count, n = 0;
                _label[x, y] = id; q.Enqueue((x, y));
                while (q.Count > 0)
                {
                    var (a, b) = q.Dequeue(); n++;
                    for (int i = -1; i <= 1; i++)
                        for (int j = -1; j <= 1; j++)
                        {
                            int u = a + i, v = b + j;
                            if (u < 0 || v < 0 || u >= map.SizeX || v >= map.SizeY || _label[u, v] != 0 || !Passable(u, v)) continue;
                            _label[u, v] = id; q.Enqueue((u, v));
                        }
                }
                _size.Add(n);
            }
    }

    /// <summary>Km in cui un'unita' di terra puo' stare: strada o citta' (anche se c'e' acqua), oppure senza mare/fiume.</summary>
    public bool Passable(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _map.SizeX || y >= _map.SizeY) return false;
        uint f = _map[x, y];
        return (f & 0x8000) != 0 || (f & 0x2000) != 0 || (f & 0xC) == 0;
    }

    private int Region(int x, int y) => x < 0 || y < 0 || x >= _map.SizeX || y >= _map.SizeY ? 0 : _label[x, y];

    /// <summary>Trova (e con apply sposta) le unita' di terra di un file di campagna che stanno in un km d'acqua.</summary>
    public List<Move> Fix(CampaignDoc doc, string fileName, bool apply)
    {
        var res = new List<Move>();
        if (doc.Units == null || doc.Units.Error != null) return res;
        foreach (var u in doc.Units.Units.Where(u => u.Kind is UnitKind.Battalion or UnitKind.Brigade))
        {
            if (Passable(u.X, u.Y)) continue;
            // zona preferita: quella dell'obiettivo assegnato, se e' terra
            int want = 0;
            bool local = false;   // presidio dell'obiettivo vicino (es. porto di un'isola): va bene anche un'isola
            if (doc.Obj(u.AssignedObj) is { } ao) { want = Region(ao.X, ao.Y); local = Math.Abs(ao.X - u.X) <= 5 && Math.Abs(ao.Y - u.Y) <= 5; }
            (int X, int Y)? best = null; int bestRank = int.MaxValue; double bestD = double.MaxValue;
            for (int r = 1; r <= MaxKm && best == null; r++)   // per anelli: il primo anello con una cella buona vince
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        int x = u.X + dx, y = u.Y + dy, reg = Region(x, y);
                        if (reg == 0) continue;
                        int rank = want != 0 && reg == want ? 0 : _size[reg] >= IslandKm2 ? 1 : 2;
                        double d = dx * dx + dy * dy;
                        if (rank < bestRank || (rank == bestRank && d < bestD)) { bestRank = rank; bestD = d; best = (x, y); }
                    }
            // se il primo anello trovato e' solo un isolotto, si guarda ancora un po' piu' in la' per la terraferma
            if (best != null && bestRank == 2 && !local)
            {
                int r0 = (int)Math.Ceiling(Math.Sqrt(bestD));
                for (int r = r0 + 1; r <= MaxKm; r++)
                {
                    bool found = false;
                    for (int dx = -r; dx <= r; dx++)
                        for (int dy = -r; dy <= r; dy++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                            int x = u.X + dx, y = u.Y + dy, reg = Region(x, y);
                            if (reg == 0 || _size[reg] < IslandKm2) continue;
                            double d = dx * dx + dy * dy;
                            if (!found || d < bestD) { bestD = d; best = (x, y); found = true; }
                        }
                    if (found) break;
                }
            }
            if (best == null) continue;
            string near = doc.Objectives.Count == 0 ? "" : doc.Objectives.OrderBy(o => (o.X - u.X) * (o.X - u.X) + (o.Y - u.Y) * (o.Y - u.Y)).First().Name;
            res.Add(new Move(fileName, u.CampId, "", u.Owner, u.X, u.Y, best.Value.X, best.Value.Y, near));
            if (apply) doc.Units.SetPosition(u, best.Value.X, best.Value.Y);
        }
        return res;
    }

    public static void SaveCsv(string path, IEnumerable<Move> moves, Func<Move, string>? unitName = null)
    {
        var sb = new StringBuilder("File;CampId;Unit;Team;From x;From y;To x;To y;Km;Near\r\n");
        foreach (var m in moves)
            sb.Append(m.File).Append(';').Append(m.CampId).Append(';').Append((unitName?.Invoke(m) ?? m.Unit).Replace(';', ',')).Append(';').Append(m.Owner).Append(';')
              .Append(m.FromX).Append(';').Append(m.FromY).Append(';').Append(m.ToX).Append(';').Append(m.ToY).Append(';')
              .Append(Math.Sqrt(Math.Pow(m.ToX - m.FromX, 2) + Math.Pow(m.ToY - m.FromY, 2)).ToString("0.0", CultureInfo.InvariantCulture)).Append(';')
              .Append(m.Near.Replace(';', ',')).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}
