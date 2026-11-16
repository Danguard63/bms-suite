using System.Text.RegularExpressions;

namespace TerrainTool.Core.Campaign;

public enum VehicleRole
{
    Other, Infantry, Manpads, Aaa, Sam, AdRadar, AdSupport, Tank, Ifv, Artillery, Ssm, Support,
    Fighter, Attack, Bomber, AirSupport, Helicopter, Ship, Submarine,
}

/// <summary>
/// Ruolo dei mezzi dal database: tipo CT (1 a piedi, 2 trainato, 3 cingolato, 4 ruotato) e sottotipo (1 AAA, 2 SAM, 3 corazzati,
/// 4 artiglieria, 7 trasporto truppe...), corretti con le capacita' del VCD, perche' la CT da sola mette il Bradley tra i carri,
/// Scud e FROG tra i camion e i radar tra i SAM: carro = forza contro la fanteria >= 80 (T-55..M1A2), il resto IFV/APC;
/// radar e posti comando = nessuna arma.
/// </summary>
public static class VehicleRoles
{
    private static readonly Regex Manpads = new(@"STINGER|MISTRAL|SA-?(7|14|16|18|24)\b|HN-?5|IGLA|STRELA|BLOWPIPE|JAVELIN SAM|STARSTREAK|QW-?\d", RegexOptions.IgnoreCase);
    private static readonly Regex IfvName = new(@"WARRIOR|MARDER|\bBMP|\bBMD|\bBTR|\bM2A\d|\bM3A\d|AMX-?10P|\bZBD|\bCV-?90|\bPUMA\b|\bVCC|\bDARDO|\bK21\b|\bLAV\b|\bYPR|\bVTT", RegexOptions.IgnoreCase);
    private static readonly Regex Ssm = new(@"SCUD|FROG|TOCHKA|ISKANDER|\bSS-?\d|LANCE|ATACMS|HWASONG|NODONG|\bDF-?\d|PLUTON|HADES", RegexOptions.IgnoreCase);

    public static VehicleRole Of(CampaignDb.CtEntry c, CampaignDb.VcdStats? s, string name)
    {
        bool armed = s?.HasWeapons ?? true;
        switch (c.Domain)
        {
            case 2: return VehicleRole.Other;                      // aerei: si usa il tipo CT (aereo / elicottero)
            case 3:
                if (c.Type == 1) return Manpads.IsMatch(name) || (s?.StrAir ?? 0) > 0 && (s?.StrFoot ?? 0) == 0 ? VehicleRole.Manpads : VehicleRole.Infantry;
                if (Ssm.IsMatch(name)) return VehicleRole.Ssm;
                if (c.Type == 2) return c.SubType == 1 ? VehicleRole.Aaa : VehicleRole.Artillery;
                switch (c.SubType)
                {
                    case 1: return armed ? VehicleRole.Aaa : VehicleRole.AdRadar;
                    case 2:
                        if (!armed) return (s?.RadarIdx ?? 0) > 0 || (s?.DetAir ?? 0) >= 20 ? VehicleRole.AdRadar : VehicleRole.AdSupport;
                        return VehicleRole.Sam;
                    case 3 when c.Type == 4: return VehicleRole.Artillery;                 // BM-21, BM-30 (lanciarazzi ruotati)
                    case 3: case 7: case 9: return (s?.StrFoot ?? 0) >= 80 && !IfvName.IsMatch(name) ? VehicleRole.Tank : VehicleRole.Ifv;
                    case 4 when c.Type == 4: return VehicleRole.Ifv;                       // ricognizione ruotata (LAV-RV)
                    case 4: case 8 when c.Type == 3: return VehicleRole.Artillery;         // semoventi, M106 mortaio
                    case 8: return (s?.StrTracked ?? 0) >= 20 ? VehicleRole.Ifv : VehicleRole.Support;   // HMMWV TOW, WZ-551 / camion
                    default: return VehicleRole.Support;                                   // genio, recupero, comando
                }
            case 4: return VehicleRole.Ship;
            case 7: return VehicleRole.Submarine;
        }
        return VehicleRole.Other;
    }

    /// <summary>Si sostituisce solo con lo stesso ruolo (per i mezzi aerei e navali si usa ancora il tipo CT).</summary>
    public static bool Compatible(VehicleRole a, VehicleRole b) => a == b || a == VehicleRole.Other || b == VehicleRole.Other;

    public static string Name(VehicleRole r) => r switch
    {
        VehicleRole.Manpads => "MANPADS", VehicleRole.Aaa => "AAA gun", VehicleRole.Sam => "SAM", VehicleRole.AdRadar => "AD radar",
        VehicleRole.AdSupport => "AD support", VehicleRole.Ifv => "IFV / APC", VehicleRole.Ssm => "SS missile", VehicleRole.AirSupport => "Air support",
        _ => r.ToString(),
    };
}
