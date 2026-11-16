using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// File di priorita' della campagna (Offense/Defense/Attrit/Intdict/Cas.pri): tre sezioni (tipi di obiettivo, tipi di unita',
/// missioni), righe "indice valore ; NOME", ogni sezione chiusa da -1. Sono gli stessi elenchi che ogni squadra ha nel .tea.
/// </summary>
public sealed class PriFile
{
    public static readonly string[] Names = { "Offense", "Defense", "Attrit", "Intdict", "Cas" };
    /// <summary>Azione aerea a cui corrisponde ogni file (tipo nel .tea: 1 DCA, 2 OCA, 3 Interdict, 4 Attrition, 5 CAS).</summary>
    public static string ActionOf(string name) => name switch { "Offense" => "OCA", "Defense" => "DCA", "Attrit" => "Attrition", "Intdict" => "Interdiction", "Cas" => "CAS", _ => "" };

    public string Path = "";
    public string Name = "";
    public int[] Obj = new int[36], Unit = new int[20], Mission = new int[50];
    /// <summary>Etichette dai commenti (TYPE_AIRBASE, STYPE_UNIT_ARMOR, AMIS_BARCAP1...), per sezione e indice.</summary>
    public Dictionary<int, string>[] Labels = { new(), new(), new() };

    public static PriFile? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var p = new PriFile { Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path) };
        int s = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            var parts = raw.Split(';', 2);
            var l = parts[0].Trim();
            if (l.Length == 0) continue;
            var f = Regex.Split(l, @"\s+");
            if (f[0] == "-1") { s++; continue; }
            if (s > 2 || f.Length < 2 || !int.TryParse(f[0], out int i) || !int.TryParse(f[1], out int v)) continue;
            var arr = s == 0 ? p.Obj : s == 1 ? p.Unit : p.Mission;
            if (i >= 0 && i < arr.Length) arr[i] = v;
            if (parts.Length > 1) p.Labels[s][i] = parts[1].Trim();
        }
        return p;
    }

    /// <summary>Riscrive i valori lasciando com'e' tutto il resto del file (commenti, spaziature).</summary>
    public void Save(string path)
    {
        var lines = File.ReadAllLines(Path);
        int s = 0;
        for (int k = 0; k < lines.Length; k++)
        {
            var parts = lines[k].Split(';', 2);
            var f = Regex.Split(parts[0].Trim(), @"\s+");
            if (f[0] == "-1") { s++; continue; }
            if (s > 2 || f.Length < 2 || !int.TryParse(f[0], out int i)) continue;
            var arr = s == 0 ? Obj : s == 1 ? Unit : Mission;
            if (i < 0 || i >= arr.Length) continue;
            lines[k] = $"{i,2}\t{arr[i],3}\t\t" + (parts.Length > 1 ? ";" + parts[1] : "");
        }
        File.WriteAllLines(path, lines, Encoding.ASCII);
    }

    /// <summary>Quanti valori della squadra coincidono con questo file (su tipi di obiettivo 1..31, unita', missioni).</summary>
    public (int Same, int Total) Match(CampTeam t)
    {
        int same = 0, tot = 0;
        for (int i = 1; i <= 31; i++) { tot++; if (Obj[i] == t.ObjPriority[i]) same++; }
        for (int i = 0; i < 20; i++) { tot++; if (Unit[i] == t.UnitPriority[i]) same++; }
        for (int i = 0; i < Math.Min(Mission.Length, t.MissionPriority.Length); i++) { tot++; if (Mission[i] == t.MissionPriority[i]) same++; }
        return (same, tot);
    }

    // ---- prova col marcatore: un valore diverso per file sulla priorita' delle spiagge (tipo 4, che non conta per l'IA aerea) ----

    public const int MarkerType = 4;
    public static int MarkerOf(string name) => 11 + Array.IndexOf(Names, name);   // Offense 11, Defense 12, Attrit 13, Intdict 14, Cas 15
    public static string? FileOfMarker(int v) => v >= 11 && v < 11 + Names.Length ? Names[v - 11] : null;
}

public static class TeamPriorities
{
    public static void SetObj(TeamFile f, CampTeam t, int i, byte v) { t.ObjPriority[i] = v; f.Data[t.ObjPrioAt + i] = v; }
    public static void SetUnit(TeamFile f, CampTeam t, int i, byte v) { t.UnitPriority[i] = v; f.Data[t.UnitPrioAt + i] = v; }
    public static void SetMission(TeamFile f, CampTeam t, int i, byte v) { t.MissionPriority[i] = v; f.Data[t.MissionPrioAt + i] = v; }

    /// <summary>Copia i valori di un file .pri nella squadra; restituisce l'azione che li rimette com'erano.</summary>
    public static Action CopyFrom(TeamFile f, CampTeam t, PriFile p)
    {
        var o = (byte[])t.ObjPriority.Clone(); var u = (byte[])t.UnitPriority.Clone(); var m = (byte[])t.MissionPriority.Clone();
        for (int i = 1; i < 36; i++) SetObj(f, t, i, (byte)Math.Clamp(p.Obj[i], 0, 255));
        for (int i = 0; i < 20; i++) SetUnit(f, t, i, (byte)Math.Clamp(p.Unit[i], 0, 255));
        for (int i = 0; i < Math.Min(50, t.MissionPriority.Length); i++) SetMission(f, t, i, (byte)Math.Clamp(p.Mission[i], 0, 255));
        return () =>
        {
            for (int i = 0; i < 36; i++) SetObj(f, t, i, o[i]);
            for (int i = 0; i < 20; i++) SetUnit(f, t, i, u[i]);
            for (int i = 0; i < m.Length; i++) SetMission(f, t, i, m[i]);
        };
    }
}
