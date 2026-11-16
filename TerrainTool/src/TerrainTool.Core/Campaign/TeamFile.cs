using System.Text;

namespace TerrainTool.Core.Campaign;

/// <summary>Una squadra (team) del file .tea: chi e', alleanze, bandiera, colore, nome.</summary>
public sealed class CampTeam
{
    public int Index;
    public byte Who, CTeam;
    public byte[] Member = new byte[8];
    public short[] Stance = new short[8];
    public byte Flag, Color, Equipment;
    public string Name = "", Motto = "";
    internal int FlagAt, ColorAt, EquipmentAt, NameAt, MemberAt, StanceAt, CTeamAt;
    /// <summary>Priorita' della squadra (come nei file .pri): tipi di obiettivo (36), tipi di unita' (20), missioni (41 o 50).</summary>
    public byte[] ObjPriority = new byte[36], UnitPriority = new byte[20], MissionPriority = Array.Empty<byte>();
    public int ObjPrioAt, UnitPrioAt, MissionPrioAt;
    /// <summary>Azione aerea in corso: tipo (difensiva / offensiva) e obiettivo.</summary>
    public byte GroundActionType, DefAirActionType, OffAirActionType;
    public VuId GroundActionObj, DefAirActionObj, OffAirActionObj;
    public override string ToString() => Name.Length > 0 ? Name : $"Team {Index}";
}

/// <summary>
/// File .tea: i16 numero squadre, poi per ogni squadra Team + Air/Ground/Naval tasking manager.
/// Formato studiato su Mission Commander (BMSUtils). Si modificano solo bandiera, colore, nome e alleanze.
/// </summary>
public sealed class TeamFile
{
    public int Version;
    public byte[] Data = Array.Empty<byte>();
    public List<CampTeam> Teams = new();
    public string? Error;

    public static TeamFile Parse(byte[] data, int v)
    {
        var t = new TeamFile { Version = v, Data = (byte[])data.Clone() };
        var b = t.Data; int o = 0;
        try
        {
            int n = Math.Min((int)BitConverter.ToInt16(b, 0), 8); o = 2;
            for (int i = 0; i < n; i++)
            {
                var tm = new CampTeam { Index = i };
                o += 8 + 2;
                tm.Who = b[o]; tm.CTeamAt = o + 1; tm.CTeam = b[o + 1]; o += 2;
                o += 2;
                tm.MemberAt = o; Array.Copy(b, o, tm.Member, 0, 8); o += 8;
                tm.StanceAt = o; for (int k = 0; k < 8; k++) { tm.Stance[k] = BitConverter.ToInt16(b, o); o += 2; }
                o += 8;                                   // first colonel/commander/wingman, last wingman
                o += v > 11 ? 4 : 4;                      // esperienze
                o += 6;                                   // initiative, supply, fuel
                if (v > 53) o += 10;
                if (v < 40) o += 4;
                o += 32;                                  // current/start stats
                if (v < 100 && v >= 83) o += 1;
                o += 2 + 20 * 8 + 20 * 4;                 // reinforcement, bonus
                tm.ObjPrioAt = o; Array.Copy(b, o, tm.ObjPriority, 0, 36); o += 36;
                tm.UnitPrioAt = o; Array.Copy(b, o, tm.UnitPriority, 0, 20); o += 20;
                tm.MissionPrioAt = o; tm.MissionPriority = b.AsSpan(o, v < 30 ? 40 : v < 102 ? 41 : 50).ToArray();
                o += tm.MissionPriority.Length;
                if (v < 34) o += 5;
                o += 4;                                   // max_vehicle
                if (v > 4)
                {
                    tm.FlagAt = o; tm.Flag = b[o]; o += 1;
                    if (v > 32) { tm.ColorAt = o; tm.Color = b[o]; o += 1; }
                    tm.EquipmentAt = o; tm.Equipment = b[o]; o += 1;
                    tm.NameAt = o; tm.Name = CStr(b, o, 20); o += 20;
                }
                if (v > 32) { tm.Motto = CStr(b, o, 200); o += 200; }
                if (v > 33)
                {
                    if (v > 50) { tm.GroundActionObj = VuId.Read(b, o + 8); tm.GroundActionType = b[o + 16]; }
                    o += v > 50 ? 19 : v > 41 ? 27 : 23;
                    tm.DefAirActionObj = VuId.Read(b, o + 8); tm.DefAirActionType = b[o + 24];
                    tm.OffAirActionObj = VuId.Read(b, o + 36); tm.OffAirActionType = b[o + 52];
                    o += 28 + 28;
                }
                if (v < 100 && v >= 83) o += 4;
                // air tasking manager
                o += 13 + 2;
                if (v >= 28) { if (v >= 63) o += 2; o += 3; }
                int ab = b[o]; o += 1 + ab * 40;
                o += 1;
                int req = BitConverter.ToInt16(b, o); o += 2;
                o += req * (v < 35 ? 64 : 76);
                o += 15 + 15;                             // ground, naval
                t.Teams.Add(tm);
            }
            if (o != b.Length) t.Error = $"team file: read {o} of {b.Length} bytes";
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            t.Error = "team file: " + ex.Message;
        }
        return t;
    }

    private static string CStr(byte[] b, int o, int len)
    {
        int z = Array.IndexOf(b, (byte)0, o, len);
        return Encoding.Latin1.GetString(b, o, (z < 0 ? o + len : z) - o).Trim();
    }

    /// <summary>Rapporti: 0 nessuno, 1 alleato, 2 amico, 3 neutrale, 4 ostile, 5 in guerra (come Mission Commander).</summary>
    public static readonly string[] StanceNames = { "None", "Allied", "Friendly", "Neutral", "Hostile", "At war" };

    public void SetStance(CampTeam t, int other, short value)
    {
        if (other < 0 || other > 7) return;
        BitConverter.TryWriteBytes(Data.AsSpan(t.StanceAt + other * 2, 2), value);
        t.Stance[other] = value;
    }

    /// <summary>Schieramento (cteam): la squadra con cui combatte. I membri di ogni squadra si ricalcolano come fa MC:
    /// la squadra L ha come membri tutte le squadre K con cteam = L.</summary>
    public void SetCTeam(CampTeam t, byte side)
    {
        Data[t.CTeamAt] = side; t.CTeam = side;
        RebuildMembers();
    }

    public void RebuildMembers()
    {
        foreach (var l in Teams)
            for (int k = 0; k < 8; k++)
            {
                byte m = (byte)(k < Teams.Count && Teams[k].CTeam == l.Index ? 1 : 0);
                l.Member[k] = m; Data[l.MemberAt + k] = m;
            }
    }

    public void SetFlag(CampTeam t, byte flag) { if (t.FlagAt > 0) { Data[t.FlagAt] = flag; t.Flag = flag; } }
    /// <summary>Produttore dei mezzi della squadra (1 cinese, 2 nordcoreano, 3 sudcoreano, 4 sovietico/russo, 5 USA/occidentale).</summary>
    public void SetEquipment(CampTeam t, byte eq) { if (t.EquipmentAt > 0) { Data[t.EquipmentAt] = eq; t.Equipment = eq; } }
    public void SetColor(CampTeam t, byte color) { if (t.ColorAt > 0) { Data[t.ColorAt] = color; t.Color = color; } }

    public void SetName(CampTeam t, string name)
    {
        if (t.NameAt <= 0) return;
        var nb = Encoding.Latin1.GetBytes(name);
        Array.Clear(Data, t.NameAt, 20);
        Array.Copy(nb, 0, Data, t.NameAt, Math.Min(19, nb.Length));
        t.Name = Encoding.Latin1.GetString(nb, 0, Math.Min(19, nb.Length));
    }
}
