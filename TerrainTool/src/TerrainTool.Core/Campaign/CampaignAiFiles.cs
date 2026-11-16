using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// Falcon4.aii della cartella Campaign: parametri dell'AI della campagna ([ATM] aerei, [GTM] terra, [NTM] navi, [Other], [Campaign]).
/// Si cambiano solo i valori: righe, commenti e ordine restano come nel file.
/// </summary>
public sealed class AiiFile
{
    public sealed class Param
    {
        public string Section = "", Key = "", Value = "";
        public int Line;
        public string Raw = "";
    }

    public string Path { get; private set; } = "";
    private readonly List<string> _lines = new();
    public List<Param> Params { get; } = new();
    private string _eol = "\r\n";

    public static AiiFile Load(string path)
    {
        var f = new AiiFile { Path = path };
        string text = File.ReadAllText(path, Encoding.Latin1);
        f._eol = text.Contains("\r\n") ? "\r\n" : "\n";
        f._lines.AddRange(text.Replace("\r\n", "\n").Split('\n'));
        string sec = "";
        for (int i = 0; i < f._lines.Count; i++)
        {
            string l = f._lines[i].Trim();
            if (l.StartsWith('[') && l.EndsWith(']')) { sec = l[1..^1]; continue; }
            if (l.Length == 0 || l.StartsWith('/') || l.StartsWith(';') || l.StartsWith('*')) continue;
            int eq = l.IndexOf('=');
            if (eq <= 0) continue;
            f.Params.Add(new Param { Section = sec, Key = l[..eq].Trim(), Value = l[(eq + 1)..].Trim(), Line = i, Raw = f._lines[i] });
        }
        return f;
    }

    public Param? Get(string key) => Params.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    public string? Value(string key) => Get(key)?.Value;

    public void Set(string key, string value)
    {
        var p = Get(key) ?? throw new KeyNotFoundException(key);
        p.Value = value.Trim();
        // stessa forma della riga originale ("Key=5" o "Key = 5")
        string raw = _lines[p.Line];
        int eq = raw.IndexOf('=');
        string after = raw[(eq + 1)..];
        string pad = after.Length - after.TrimStart().Length > 0 ? " " : "";
        _lines[p.Line] = raw[..(eq + 1)] + pad + p.Value;
    }

    public void Save(string path)
    {
        File.WriteAllText(path, string.Join(_eol, _lines), Encoding.Latin1);
        Path = path;
    }

    /// <summary>Descrizioni (dal PMC Editing Wiki dove ci sono; le altre dedotte dal nome e segnate "≈").</summary>
    public static readonly Dictionary<string, string> Help = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ImmediatePlanMinTime"] = "≈ minutes ahead the ATO plans immediate missions (minimum)",
        ["ImmediatePlanMaxTime"] = "≈ minutes ahead the ATO plans immediate missions (maximum)",
        ["LongrangePlanMinTime"] = "missions whose time on target is closer than this are cancelled (minutes)",
        ["LongrangePlanMaxTime"] = "not used",
        ["AircraftAssignmentRatio"] = "not used",
        ["MaxFlymissionThreat"] = "threat limit for normal missions (missions above it are not flown)",
        ["MaxFlymissionHighThreat"] = "threat limit for missions flagged High Threat (SEAD, strike…)",
        ["MaxFlymissionNoThreat"] = "threat limit for missions flagged No Threat (100 = more tasking)",
        ["MinSeadescortThreat"] = "target-area threat above which a SEAD escort is added to the package",
        ["MinAvoidThreat"] = "ingress/egress threat above which the planner routes around it (within AirPathMax) or cancels",
        ["MinAssemblyPtDist"] = "minimum distance of the assembly point from the mission start (km)",
        ["BreakpointDist"] = "distance of the breakpoint from home plate (km)",
        ["TargetClearRadius"] = "not used",
        ["LoiterTurnDistance"] = "length of the CAP / patrol loiter racetrack (km)",
        ["SweepRadius"] = "distance covered by a sweep (km)",
        ["MinimumBDAPriority"] = "minimum target priority that gets a BDA flight",
        ["MinimumAWACSDistance"] = "closest an AWACS orbits to the FLOT (km behind it)",
        ["MaximumAWACSDistance"] = "farthest an AWACS orbits from the FLOT (km behind it)",
        ["MinimumJSTARDistance"] = "closest a JSTARS orbits to the FLOT (km)",
        ["MaximumJSTARDistance"] = "farthest a JSTARS orbits from the FLOT (km)",
        ["MinimumTankerDistance"] = "closest a tanker track is to the FLOT (km)",
        ["MaximumTankerDistance"] = "farthest a tanker track is from the FLOT (km)",
        ["MinimumECMDistance"] = "closest an ECM (jammer) orbit is to the FLOT (km)",
        ["MaximumECMDistance"] = "farthest an ECM (jammer) orbit is from the FLOT (km)",
        ["FirstColonel"] = "start of the pilot-name range used for colonels",
        ["FirstCommander"] = "start of the pilot-name range used for commanders",
        ["FirstWingman"] = "start of the pilot-name range used for wingmen",
        ["LastWingman"] = "end of the pilot-name range used for wingmen",
        ["MaxEscortAAStrength"] = "not used",
        ["BARCAP_RequestInterval"] = "time between BARCAP planning requests (minutes)",
        ["BARCAP_CoastlineMinDist"] = "≈ minimum distance of a BARCAP from the coastline (km)",
        ["BARCAP_CoastlineMaxDist"] = "≈ maximum distance of a BARCAP from the coastline (km)",
        ["OnCallFlightsPerRequest"] = "on-call CAS flights per FAC mission",
        ["AirTaskTime"] = "not used",
        ["AirPlanTime"] = "average sortie rate = 60 / AirPlanTime",
        ["VictoryConditionTime"] = "how often the victory conditions of a TE are checked",
        ["FlightMoveCheckInterval"] = "how often flights are moved (seconds)",
        ["FlightCombatCheckInterval"] = "how often combat between flights is checked (seconds)",
        ["AirUpdateCheckInterval"] = "how often squadrons and packages are updated (seconds)",
        ["PackageCyclesToWait"] = "cycles a package waits for tankers and other support",
        ["AirPathMax"] = "maximum search range for a safe path between two waypoints",
        ["MinimumIgnoreRange"] = "closest range at which an aircraft is ignored (km)",
        ["MaxSARDistance"] = "farthest behind the FLOT that a SAR is flown (km)",
        ["MaxBAIDistance"] = "farthest behind the FLOT that a BAI is flown (km)",
        ["PilotAssignTime"] = "time before takeoff a pilot is assigned to a mission (ms)",
        ["AircraftTurnaroundMinutes"] = "minutes after a mission before the aircraft can fly again",
        ["PrimaryObjPriority"] = "≈ priority of primary objectives for the ground AI",
        ["SecondaryObjPriority"] = "≈ priority of secondary objectives for the ground AI",
        ["MaximumOffenses"] = "≈ offensives a team can run at the same time",
        ["MinimumOffensiveUnits"] = "≈ minimum units to start an offensive",
        ["MinimumAdjustedLevel"] = "≈ strength (%) below which a unit is pulled back",
        ["MinimumViableLevel"] = "≈ strength (%) a unit needs to be used in an offensive",
        ["MaximumAssignmentDist"] = "≈ farthest a ground unit is assigned from its position (km)",
        ["MaximumOffensiveLinks"] = "≈ link depth searched for offensive objectives",
        ["MaximumDefensiveLinks"] = "≈ link depth searched for defensive objectives",
        ["BrigadeBreakBase"] = "≈ base morale/strength at which a brigade breaks",
        ["RoleScoreModifier"] = "≈ weight of the unit role score in assignments",
        ["TaskGroundTime"] = "≈ ground re-tasking interval (minutes)",
        ["PlanGroundTime"] = "≈ ground planning interval (minutes)",
        ["ObjectiveRepairInterval"] = "≈ minutes between objective repairs",
        ["ResupplyInterval"] = "≈ minutes between resupply of units",
        ["MoraleRegainRate"] = "≈ morale regained per cycle",
        ["TERegainRateMultiplier"] = "≈ morale regain multiplier in TE",
        ["FootMoveCheckInterval"] = "≈ how often foot units move (seconds)",
        ["TrackedMoveCheckInterval"] = "≈ how often tracked units move (seconds)",
        ["GroundCombatCheckInterval"] = "≈ how often ground combat is checked (seconds)",
        ["GroundUpdateCheckInterval"] = "≈ how often ground units are updated (seconds)",
        ["GroundPathMax"] = "≈ maximum search for a ground path",
        ["ObjGroundPathMaxSearch"] = "≈ maximum search for an objective-to-objective path",
        ["ObjGroundPathMaxCost"] = "≈ maximum cost of an objective-to-objective path",
        ["MinFullOffensiveInitiative"] = "≈ initiative needed for a full offensive",
        ["MinCounterAttackInitiative"] = "≈ initiative needed for a counter-attack",
        ["MinSecureInitiative"] = "≈ initiative needed to secure objectives",
        ["MinExpToFirePreGuide"] = "≈ experience needed to fire before guidance",
        ["OffensiveReinforcementTime"] = "≈ reinforcement cycle during an offensive (minutes)",
        ["DefensiveReinforcementTime"] = "≈ reinforcement cycle in defense (minutes)",
        ["ConsolidateReinforcementTime"] = "≈ reinforcement cycle while consolidating (minutes)",
        ["ActionPrepTime"] = "≈ preparation time before an action (minutes)",
        ["TaskNavalTime"] = "naval retask time (minutes)",
        ["PlanNavalTime"] = "naval replan time (minutes)",
        ["NavalCombatCheckInterval"] = "how often naval units fire (seconds)",
        ["NavalMoveCheckInterval"] = "how often a task force moves (seconds)",
        ["LowAltitudeCutoff"] = "≈ altitude (ft) below which a flight counts as low",
        ["GroundSearchDistance"] = "how far to look for surface units or objectives (km)",
        ["AirSearchDistance"] = "how far to look for air threats (km)",
        ["NewCloudPercentage"] = "≈ chance of new clouds (2D weather)",
        ["DeleteCloudPercentage"] = "≈ chance of clouds disappearing (2D weather)",
        ["PlayerBubbleObjects"] = "objects kept in the player bubble",
        ["CampaignResyncTime"] = "how often the campaign resyncs (seconds)",
        ["BubbleRebuildTime"] = "how often the player bubble is rebuilt (seconds)",
        ["PriorityEventqueueLength"] = "length of the priority event queue",
        ["StandardEventqueueLength"] = "length of the stored event queues",
        ["StatisticsRecalculationInterval"] = "how often unit statistics are recalculated",
        ["LowAirRangeModifier"] = "% of air range used as low-air range",
        ["MinimumStrength"] = "minimum strength points for collateral damage",
        ["MaximumDamageTries"] = "tries to find a random vehicle/feature to damage",
        ["ReaggregationRatio"] = "ratio above which units reaggregate (%)",
        ["ReinforcementTime"] = "length of a reinforcement cycle (minutes)",
        ["SimBubbleSize"] = "radius of the sim campaign lists (feet)",
        ["InitiativeLeakPerHour"] = "initiative adjusted automatically each hour",
        ["StartOffBonusRepl"] = "replacement bonus when a team goes on the offensive",
        ["StartOffBonusSup"] = "supply bonus when a team goes on the offensive",
        ["StartOffBonusFuel"] = "fuel bonus when a team goes on the offensive",
        ["ActionRate"] = "how often a new offensive action can start (hours)",
        ["ActionTimeOut"] = "maximum length of an offensive action (hours)",
        ["DataRateModRepl"] = "modifier of the replacement production rate",
        ["DataRateModSup"] = "modifier of the fuel and supply production rate",
        ["RelSquadBonus"] = "replacements of squadrons relative to ground units",
        ["NoActionBonusProd"] = "≈ production bonus when no action is running",
        ["NoTypeBonusRepl"] = "≈ replacement bonus by unit type",
        ["NewInitiativePoints"] = "≈ use the new initiative point system",
        ["CampBugFixes"] = "≈ enable the campaign bug fixes",
        ["2DHitChanceAir"] = "≈ hit chance of 2D (aggregated) air combat",
        ["2DHitChanceGround"] = "≈ hit chance of 2D (aggregated) ground combat",
    };
}

/// <summary>
/// MissionData_XX.xml (e _00 … _05) della cartella Campaign: come l'ATO pianifica ogni tipo di missione
/// (quote in centinaia di piedi, loiter, numero di aerei, tempi). Si cambiano solo i valori, il resto del file resta uguale.
/// </summary>
public sealed class MissionDataFile
{
    public string Path { get; private set; } = "";
    private string _text = "";
    private readonly bool _bom;
    public List<string> Missions { get; } = new();

    private MissionDataFile(bool bom) { _bom = bom; }

    public static MissionDataFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var f = new MissionDataFile(bom) { Path = path, _text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)) };
        foreach (Match m in Regex.Matches(f._text, @"<(AMIS_\w+)>")) if (!f.Missions.Contains(m.Groups[1].Value)) f.Missions.Add(m.Groups[1].Value);
        return f;
    }

    private Match Block(string mission) => Regex.Match(_text, $@"<{mission}>(.*?)</{mission}>", RegexOptions.Singleline);

    public Dictionary<string, string> Fields(string mission)
    {
        var res = new Dictionary<string, string>();
        var b = Block(mission);
        if (!b.Success) return res;
        foreach (Match m in Regex.Matches(b.Groups[1].Value, @"<(\w+)>([^<]*)</\1>")) res[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        return res;
    }

    public List<string> FieldOrder(string mission)
    {
        var b = Block(mission);
        return b.Success ? Regex.Matches(b.Groups[1].Value, @"<(\w+)>[^<]*</\1>").Select(m => m.Groups[1].Value).ToList() : new List<string>();
    }

    public string? Get(string mission, string field) => Fields(mission).GetValueOrDefault(field);

    public bool Set(string mission, string field, string value)
    {
        var b = Block(mission);
        if (!b.Success) return false;
        var inner = b.Groups[1];
        var fm = Regex.Match(inner.Value, $@"<{field}>[^<]*</{field}>");
        if (!fm.Success) return false;
        int at = inner.Index + fm.Index;
        _text = _text[..at] + $"<{field}>{value.Trim()}</{field}>" + _text[(at + fm.Length)..];
        return true;
    }

    public void Save(string path)
    {
        var enc = new UTF8Encoding(false);
        var body = enc.GetBytes(_text);
        using var fs = File.Create(path);
        if (_bom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF });
        fs.Write(body);
        Path = path;
    }

    /// <summary>Nomi leggibili dei tipi di missione.</summary>
    public static string Title(string amis) => amis switch
    {
        "AMIS_BARCAP1" => "BARCAP (1)", "AMIS_BARCAP2" => "BARCAP (2)", "AMIS_HAVCAP" => "HAVCAP", "AMIS_TARCAP" => "TARCAP",
        "AMIS_RESCORT" => "Rescue escort", "AMIS_AMBUSHCAP" => "Ambush CAP", "AMIS_SWEEP" => "Fighter sweep", "AMIS_ALERT" => "Alert (QRA)",
        "AMIS_INTERCEPT" => "Intercept", "AMIS_ESCORT" => "Escort", "AMIS_SEADSTRIKE" => "SEAD strike", "AMIS_SEADESCORT" => "SEAD escort",
        "AMIS_OCASTRIKE" => "OCA strike", "AMIS_INTSTRIKE" => "Interdiction strike", "AMIS_STRIKE" => "Strike", "AMIS_DEEPSTRIKE" => "Deep strike",
        "AMIS_STSTRIKE" => "Stealth strike", "AMIS_STRATBOMB" => "Strategic bombing", "AMIS_FAC" => "FAC", "AMIS_ONCALLCAS" => "On-call CAS",
        "AMIS_PRPLANCAS" => "Pre-planned CAS", "AMIS_CAS" => "CAS", "AMIS_SAD" => "Search & destroy", "AMIS_INT" => "Interdiction",
        "AMIS_BAI" => "BAI", "AMIS_AWACS" => "AWACS", "AMIS_JSTAR" => "JSTARS", "AMIS_TANKER" => "Tanker", "AMIS_RECON" => "Recon",
        "AMIS_BDA" => "BDA", "AMIS_ECM" => "ECM", "AMIS_AIRCAV" => "Air cavalry", "AMIS_AIRLIFT" => "Airlift", "AMIS_SAR" => "SAR",
        "AMIS_ASW" => "ASW", "AMIS_ASHIP" => "Anti-ship", "AMIS_PATROL" => "Patrol", "AMIS_RECONPATROL" => "Recon patrol",
        "AMIS_ABORT" => "Abort", "AMIS_TRAINING" => "Training", "AMIS_RELOCATE" => "Relocate",
        _ => amis.StartsWith("AMIS_") ? amis[5..] : amis,
    };

    public static bool IsReal(string amis) => amis != "AMIS_NONE" && !amis.StartsWith("AMIS_DUMMY");

    /// <summary>Descrizioni dei campi (unità come le usa BMS; "≈" = dedotto dal nome).</summary>
    public static readonly Dictionary<string, string> Help = new()
    {
        ["MisType"] = "mission type number (do not change)",
        ["TgtType"] = "≈ kind of target (location / objective / unit…)",
        ["MisRole"] = "≈ role (air-to-air, air-to-ground…)",
        ["MisProfile"] = "≈ route profile (low / high / mixed)",
        ["TgtProfile"] = "≈ profile over the target",
        ["TgtDescription"] = "≈ how the target is described in the briefing",
        ["RouteWpAction"] = "≈ action at the route waypoints",
        ["TgtWpAction"] = "≈ action at the target waypoint",
        ["TgtMinAlt"] = "lowest altitude at the target (hundreds of feet: 150 = 15,000 ft)",
        ["TgtMaxAlt"] = "highest altitude at the target (hundreds of feet)",
        ["MisAltLowProfile"] = "route altitude when flown low (hundreds of feet)",
        ["MisAltHighProfile"] = "route altitude when flown high (hundreds of feet)",
        ["SeparationTime"] = "≈ time between flights of the package (minutes)",
        ["LoiterTime"] = "time on station (minutes)",
        ["LoiterTimeMinimum"] = "≈ minimum time on station (minutes)",
        ["LoiterDist"] = "≈ length of the orbit / racetrack (km)",
        ["LoiterDirection"] = "≈ orientation of the orbit",
        ["LoiterOffset"] = "≈ offset of the orbit from the target (km)",
        ["LoiterTrackDist"] = "≈ track distance of the orbit (km)",
        ["LoiterTimeOnEscort"] = "≈ time the escort stays (minutes)",
        ["MisStrength"] = "aircraft per flight",
        ["MinPlanTime"] = "≈ planned at least this many minutes before takeoff",
        ["MaxPlanTime"] = "≈ planned at most this many minutes before takeoff",
        ["HoldingTime"] = "≈ time at the holding point (minutes)",
        ["HoldingDist"] = "≈ distance of the holding point (km)",
        ["MaxAirThreat"] = "≈ air threat allowed (0 = default)",
        ["MaxGndThreat"] = "≈ ground threat allowed (0 = default)",
        ["MinDistToFlot"] = "≈ minimum distance from the front (km)",
        ["MaxDistToFlot"] = "≈ maximum distance from the front (km)",
        ["EscortType"] = "≈ escort requested (mission type number, 0 = none)",
        ["MinDistSimilar"] = "≈ minimum distance from a similar mission (km)",
        ["MinTimeSimilar"] = "≈ minimum time from a similar mission (minutes)",
        ["FormationTypePrePush"] = "formation before the push point",
        ["FormationSpacingPrePush"] = "spacing before the push point",
        ["FormationTypePreTgt"] = "formation before the target",
        ["FormationSpacingPreTgt"] = "spacing before the target",
        ["FormationTypePostTgt"] = "formation after the target",
        ["FormationSpacingPostTgt"] = "spacing after the target",
        ["FormationTypePostSplit"] = "formation after the split",
        ["FormationSpacingPostSplit"] = "spacing after the split",
        ["SpecialCapabilities"] = "≈ special capabilities needed",
        ["Flags"] = "planning flags (bit field: change only if you know them)",
    };

    public static int ParseInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
}
