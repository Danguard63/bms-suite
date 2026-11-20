using System.Text.RegularExpressions;

namespace TerrainTool.Core.Sim;

/// <summary>
/// Short explanations of the values of the BMS Sim files, shown next to them in the Sim Editor. Only what is clear from the
/// files themselves, the BMS data managers' labels and how the suite reads them; a value with no entry shows its own name or
/// the comment above it in the file.
/// </summary>
public static class SimDocs
{
    private static readonly (string Pattern, string Text)[] Radar =
    {
        ("^Indx$", "Radar number and name: its place in RadTypes.lst (the database's radar records point to it)"),
        ("^Prf$", "Pulse repetition frequency (marked unused in the BMS files)"),
        ("^CustomSymbolType$", "Kind of symbol used for this emitter"),
        ("^TimeToLock$", "Time to lock a target (ms)"),
        ("^MaxTwstargets$", "Targets followed at the same time (track while scan)"),
        ("^Timeskillfactor$", "Factor on the times above for the crew's skill (%)"),
        ("^Timetosearch(\\d)$", "Time spent in search mode $1 before the next step (ms)"),
        ("^Timetoacuire$", "Time spent in acquisition before guidance (ms)"),
        ("^Timetoguide$", "Time in guidance before the missile can be fired (ms)"),
        ("^Timetocoast$", "Time the radar keeps a lost target before dropping it (ms)"),
        ("^Rangetosearch(\\d)$", "Range of search mode $1 (feet; 1 nm = 6076 ft, 1 km = 3281 ft)"),
        ("^Rangetoacuire$", "Range of acquisition (feet)"),
        ("^Rangetoguide$", "Range of guidance: the target must be inside it to be engaged (feet)"),
        ("^Sweeptime(search\\d|acuire|guide|coast)$", "Time of one antenna sweep in this mode (ms)"),
        ("^Rwrsound(search\\d|acuire|guide)$", "Sound the RWR plays for this radar in this mode"),
        ("^Rwrsymbol(search\\d|acuire|guide)$", "Symbol the RWR shows for this radar in this mode"),
        ("^RwrsymbolALR93Override$", "Different symbol on the ALR-93 RWR"),
        ("^Band(search\\d|acuire|guide)$", "Radio band of the radar in this mode (letter)"),
        ("^Type(search\\d|acuire|guide)$", "Kind of emission in this mode (P = pulse, C = continuous wave)"),
        ("^PRI(search\\d|acuire|guide)$", "Pulse repetition interval in this mode"),
        ("^TrackWarning$", "1 = the RWR warns when this radar tracks you"),
        ("^LaunchWarning$", "1 = the RWR warns when a missile is guided by this radar"),
        ("^AirFireRate$", "Time between two shots at air targets (ms)"),
        ("^AirSalvoRate$", "Time between two salvos at air targets (ms)"),
        ("^Maxmissilesintheair$", "Missiles this radar guides at the same time"),
        ("^Maxmissilespertarget$", "Missiles fired at one target"),
        ("^AntennaTrackAzimuthAngle$", "Width of the tracking beam, left-right (degrees)"),
        ("^AntennaSideLobesAzimuthAngle$", "Width of the side lobes, left-right (degrees)"),
        ("^AntennaTrackElevationAngle$", "Height of the tracking beam (degrees)"),
        ("^AntennaSideLobesElevationAngle$", "Height of the side lobes (degrees)"),
        ("^MinEngagementAlt$", "Lowest target altitude it engages (feet)"),
        ("^MinEngagementRange$", "Shortest range it engages (feet)"),
        ("^MaxNctrRange$", "Range for non-cooperative target recognition"),
    };

    private static readonly (string Pattern, string Text)[] Missile =
    {
        ("^Final Time", "Flight time of the missile (seconds) before it self-destructs"),
        ("^Pk", "Probability of kill when it hits (0 to 1)"),
        ("^Time To Go Active", "Seconds after launch when an active seeker switches on"),
        ("^Seeker Type$", "Kind of seeker (number used by the BMS missile code)"),
        ("^MACH$", "First number: how many Mach breakpoints; then the breakpoints (columns of the aero tables)"),
        ("^ALPHA$", "First number: how many angle of attack breakpoints (degrees); then the breakpoints"),
        ("^Mach: ", "One row of the table above: a value for each alpha breakpoint, at this Mach"),
        ("^BRNTIME$", "First number: how many burn time breakpoints; then the times (seconds)"),
        ("^ENGINE THRUST", "Thrust (lbs) at each burn time breakpoint"),
        ("^Table Multiplier$", "Multiplies every value of the launch range table below"),
        ("^Number of Altitude Breakpoints$", "First number: how many altitudes; then the launcher's altitudes (feet) of the range table rows"),
        ("^number of ownship vel breakpoints$", "First number: how many launcher speeds; then the speeds"),
        ("^number of aspect breakpoints$", "First number: how many breakpoints; then the target values of the columns of the range table"),
        ("^Alt = ", "One row of the launch range table (feet, × Table Multiplier): one value per column breakpoint above"),
        ("^MinEngagementRange$", "Shortest launch range (feet)"),
        ("^MinEngagementAlt$", "Lowest target altitude (feet)"),
        ("^maxspeed$", "Top speed"),
        ("^proximityfuserange$", "Proximity fuse distance (feet)"),
    };

    private static readonly (string Pattern, string Text)[] Vehicle =
    {
        ("Number of Sensors", "How many sensors the vehicle has (each one on a line below)"),
        ("^SensorType", "Sensor kind and number: kind 0 IRST, 1 radar, 2 RWR, 3 visual; the number is its record in that table"),
        ("^TurretAzimuthOffset$", "Turret rotation offset (degrees)"),
        ("^TrailerDataTractorCt$", "Class table entry of the tractor that tows it (-1 = none)"),
    };

    /// <summary>The explanation of a value of a Sim file (folder: Misdata, Radar, Vehdef, Bombdata…), or "".</summary>
    public static string Describe(string folder, SimTextFile.Item it)
    {
        var table = folder.ToLowerInvariant() switch
        {
            "radar" => Radar,
            "misdata" => Missile.Concat(Radar).ToArray(),
            "vehdef" => Vehicle,
            _ => Array.Empty<(string, string)>(),
        };
        string name = it.Key ?? it.Label;
        foreach (var (p, t) in table)
        {
            var m = Regex.Match(name, p, RegexOptions.IgnoreCase);
            if (m.Success) return m.Result(t);
        }
        return "";
    }
}
