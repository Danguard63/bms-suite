namespace TerrainTool.Core.Campaign;

/// <summary>Controlli di coerenza di una campagna/missione: collegamenti, padri, proprietari, unita' e loro obiettivi.</summary>
public static class CampaignValidator
{
    /// <param name="objType">tipo dell'obiettivo (1 = base aerea, 15 = incrocio, ...) dal suo indice CT; -1 se sconosciuto</param>
    /// <param name="isWater">facoltativo: true se il punto (km est, km nord) e' in acqua</param>
    /// <param name="unitSubType">sottotipo CT di un tipo di unita' (indice CT): 1 antiaereo, 13 rifornimenti (regole BMS 4.37+ / Korea)</param>
    public static List<CampIssue> Run(CampaignDoc d, Func<CampObjective, int> objType, double maxLinkKm = 50, Func<double, double, bool>? isWater = null,
                                      Func<int, int>? unitSubType = null)
    {
        var res = new List<CampIssue>();
        void Add(IssueLevel lv, string cat, string msg, double x, double y, VuId? o = null, VuId? u = null) => res.Add(new CampIssue(lv, cat, msg, x, y, o, u));
        string N(CampObjective o) => $"{o} ({TypeName(objType(o))}, CampId {o.CampId})";
        int teams = d.Teams?.Teams.Count ?? 8;

        // --- obiettivi ---
        foreach (var g in d.Objectives.GroupBy(o => o.Id).Where(g => g.Count() > 1))
            foreach (var o in g) Add(IssueLevel.Error, "Objectives", $"{N(o)}: same VU id {o.Id} as {g.Count() - 1} other objective(s)", o.X, o.Y, o.Id);
        foreach (var g in d.Objectives.GroupBy(o => o.CampId).Where(g => g.Count() > 1))
            Add(IssueLevel.Warning, "Objectives", $"CampId {g.Key} used by {g.Count()} objectives: {string.Join(", ", g.Take(4))}", g.First().X, g.First().Y, g.First().Id);

        var drawn = new HashSet<(VuId, VuId)>();
        foreach (var o in d.Objectives)
        {
            int t = objType(o);
            if (d.TheaterSizeX > 0 && (o.X < 0 || o.Y < 0 || o.X >= d.TheaterSizeX || o.Y >= d.TheaterSizeY))
                Add(IssueLevel.Error, "Objectives", $"{N(o)}: outside the theater ({o.X}, {o.Y} km)", o.X, o.Y, o.Id);
            if (o.Owner >= teams) Add(IssueLevel.Error, "Owners", $"{N(o)}: owner team {o.Owner} does not exist", o.X, o.Y, o.Id);
            else if (d.Teams != null && o.Owner > 0 && d.Teams.Teams[o.Owner].Name.Length == 0)
                Add(IssueLevel.Warning, "Owners", $"{N(o)}: owner team {o.Owner} has no name", o.X, o.Y, o.Id);
            if (!o.Parent.IsNull)
            {
                if (o.Parent == o.Id) Add(IssueLevel.Error, "Parents", $"{N(o)}: is its own parent", o.X, o.Y, o.Id);
                else if (d.Obj(o.Parent) is not { } par) Add(IssueLevel.Error, "Parents", $"{N(o)}: parent {o.Parent} does not exist", o.X, o.Y, o.Id);
                else if (Km(o, par) > 200) Add(IssueLevel.Info, "Parents", $"{N(o)}: parent {par} is {Km(o, par):0} km away", o.X, o.Y, o.Id);
            }
            if (o.Links.Count == 0 && t is not (14 or 27 or 16 or -1))
                Add(t is 1 or 2 ? IssueLevel.Info : IssueLevel.Warning, "Links", $"{N(o)}: no links (ground units cannot reach it)", o.X, o.Y, o.Id);
            foreach (var g in o.Links.GroupBy(l => l.To).Where(g => g.Count() > 1))
                Add(IssueLevel.Warning, "Links", $"{N(o)}: link to {d.Obj(g.Key)?.ToString() ?? g.Key.ToString()} repeated {g.Count()} times", o.X, o.Y, o.Id);
            foreach (var l in o.Links)
            {
                if (l.To == o.Id) { Add(IssueLevel.Error, "Links", $"{N(o)}: linked to itself", o.X, o.Y, o.Id); continue; }
                var b = d.Obj(l.To);
                if (b == null) { Add(IssueLevel.Error, "Links", $"{N(o)}: link to {l.To}, which does not exist", o.X, o.Y, o.Id); continue; }
                if (!d.Linked(b, o)) Add(IssueLevel.Warning, "Links", $"{N(o)} → {b}: one-way link (no way back; BMS Korea has ~110 of these, mostly bridges)", (o.X + b.X) / 2.0, (o.Y + b.Y) / 2.0, o.Id);
                var key = o.Index < b.Index ? (o.Id, b.Id) : (b.Id, o.Id);
                if (!drawn.Add(key)) continue;
                double km = Km(o, b);
                if (km > maxLinkKm) Add(IssueLevel.Warning, "Links", $"{o} – {b}: link is {km:0} km long (the longest in KTO is 50 km; near 59 km the link cost reaches 255 and tracked units can no longer use it)", (o.X + b.X) / 2.0, (o.Y + b.Y) / 2.0, o.Id);
                if (isWater != null && CrossesWater(o, b, isWater))
                    Add(IssueLevel.Warning, "Links", $"{o} – {b}: link crosses water", (o.X + b.X) / 2.0, (o.Y + b.Y) / 2.0, o.Id);
            }
            if (isWater != null && t is not (19 or 27 or 4 or 6 or -1) && isWater(o.SimEast / CampaignDoc.FeetPerKm, o.SimNorth / CampaignDoc.FeetPerKm))
                Add(IssueLevel.Warning, "Objectives", $"{N(o)}: in the water", o.X, o.Y, o.Id);
        }

        // --- linee del fronte: come le calcolerebbe BMS ---
        if (d.ObjectivesOwn && d.Teams != null)
        {
            var saved = d.Objectives.ToDictionary(o => o, o => o.Flags);
            bool wasDirty = d.Dirty;
            int diff = d.RecomputeFrontLines();
            if (diff > 0)
            {
                Add(IssueLevel.Info, "Front", $"{diff} objectives have a front line (1st/2nd/3rd) different from the one BMS would compute from owners, links and relations " +
                    "('Auto front lines' fixes it at the next change)", 0, 0);
                foreach (var (o, f) in saved) o.Flags = f;
                d.Dirty = wasDirty;
            }
        }

        // --- squadre: rapporti ---
        if (d.Teams != null)
        {
            var T = d.Teams.Teams;
            for (int a = 1; a < T.Count; a++)
            {
                if (T[a].CTeam >= T.Count) Add(IssueLevel.Error, "Teams", $"{TeamName(d, a)}: fights with team {T[a].CTeam}, which does not exist", 0, 0);
                for (int b = a + 1; b < T.Count; b++)
                {
                    if (d.Objectives.All(o => o.Owner != a && o.Owner != b) && (d.Units?.Units.All(u => u.Owner != a && u.Owner != b) ?? true)) continue;
                    short sab = T[a].Stance[b], sba = T[b].Stance[a];
                    if (sab != sba) Add(IssueLevel.Warning, "Teams", $"{TeamName(d, a)} → {TeamName(d, b)}: {StanceName(sab)}, but {TeamName(d, b)} → {TeamName(d, a)}: {StanceName(sba)}", 0, 0);
                    if (T[a].CTeam == T[b].CTeam && (sab >= 4 || sba >= 4)) Add(IssueLevel.Error, "Teams", $"{TeamName(d, a)} and {TeamName(d, b)} are on the same side but hostile/at war", 0, 0);
                }
            }
        }

        // --- unita' ---
        if (d.Units != null)
        {
            var u = d.Units.Units;
            foreach (var g in u.GroupBy(x => x.Id).Where(g => g.Count() > 1))
                Add(IssueLevel.Error, "Units", $"VU id {g.Key} used by {g.Count()} units", g.First().X, g.First().Y, null, g.Key);
            foreach (var x in u)
            {
                string un = $"{x.Kind} {x.NameId} (team {TeamName(d, x.Owner)})";
                if (x.Owner >= teams || x.Owner == 0) Add(IssueLevel.Error, "Units", $"{un}: owner team {x.Owner} is not valid", x.X, x.Y, null, x.Id);
                bool offMap = x.Kind == UnitKind.Squadron && x.Airbase.IsNull;   // squadroni fuori mappa (es. CIS in Corea)
                if (offMap) { Add(IssueLevel.Info, "Air", $"{un}: off-map squadron (no airbase, at {x.X}, {x.Y} km)", x.X, x.Y, null, x.Id); continue; }
                if (d.TheaterSizeX > 0 && (x.X < 0 || x.Y < 0 || x.X >= d.TheaterSizeX || x.Y >= d.TheaterSizeY))
                    Add(IssueLevel.Error, "Units", $"{un}: outside the theater ({x.X}, {x.Y} km)", x.X, x.Y, null, x.Id);
                switch (x.Kind)
                {
                    case UnitKind.Battalion:
                        CheckAssigned(x, un);
                        if (!x.Parent.IsNull)
                        {
                            var br = d.Unit(x.Parent);
                            if (br == null) Add(IssueLevel.Error, "Units", $"{un}: its brigade {x.Parent} does not exist", x.X, x.Y, null, x.Id);
                            else if (br.Kind != UnitKind.Brigade) Add(IssueLevel.Error, "Units", $"{un}: parent is a {br.Kind}, not a brigade", x.X, x.Y, null, x.Id);
                            else
                            {
                                if (!br.Elements.Contains(x.Id)) Add(IssueLevel.Error, "Units", $"{un}: brigade {br.NameId} does not list it among its battalions", x.X, x.Y, null, x.Id);
                                if (br.Owner != x.Owner) Add(IssueLevel.Error, "Units", $"{un}: belongs to team {TeamName(d, x.Owner)} but its brigade to {TeamName(d, br.Owner)}", x.X, x.Y, null, x.Id);
                                double km = Math.Sqrt(Math.Pow(br.X - x.X, 2) + Math.Pow(br.Y - x.Y, 2));
                                if (km > 60) Add(IssueLevel.Info, "Units", $"{un}: {km:0} km from its brigade", x.X, x.Y, null, x.Id);
                            }
                        }
                        if (isWater != null && isWater(x.X + 0.5, x.Y + 0.5)) Add(IssueLevel.Warning, "Units", $"{un}: in the water", x.X, x.Y, null, x.Id);
                        break;
                    case UnitKind.Brigade:
                        CheckAssigned(x, un);
                        if (x.Elements.Count == 0) Add(IssueLevel.Warning, "Units", $"{un}: brigade without battalions", x.X, x.Y, null, x.Id);
                        if (x.Elements.Count > 5) Add(IssueLevel.Error, "Units", $"{un}: {x.Elements.Count} battalions (Mission Commander allows 5, Korea brigades have 2-4)", x.X, x.Y, null, x.Id);
                        foreach (var e in x.Elements)
                        {
                            var b = d.Unit(e);
                            if (b == null) Add(IssueLevel.Error, "Units", $"{un}: battalion {e} does not exist", x.X, x.Y, null, x.Id);
                            else if (b.Parent != x.Id) Add(IssueLevel.Error, "Units", $"{un}: battalion {b.NameId} points to another brigade", x.X, x.Y, null, x.Id);
                            else if (unitSubType != null && unitSubType(b.Type - 100) is 1 or 13)
                                Add(IssueLevel.Warning, "Units", $"{un}: battalion {b.NameId} is {(unitSubType(b.Type - 100) == 1 ? "air defense" : "supply")} – in BMS 4.37+ (and Korea) it is a support element, not a battalion of the brigade: use Fix support elements", x.X, x.Y, null, x.Id);
                        }
                        break;
                    case UnitKind.Squadron:
                        var ab = d.Obj(x.Airbase) ?? null;
                        var abUnit = ab == null ? d.Unit(x.Airbase) : null;   // portaerei: lo squadrone sta su una task force
                        if (ab == null && abUnit == null) Add(IssueLevel.Error, "Air", $"{un}: airbase {x.Airbase} does not exist", x.X, x.Y, null, x.Id);
                        else if (ab != null)
                        {
                            int at = objType(ab);
                            if (at == 3) Add(IssueLevel.Info, "Air", $"{un}: based at the army base {ab} (helicopters only)", x.X, x.Y, null, x.Id);
                            else if (at is not (1 or 2 or 30 or -1)) Add(IssueLevel.Error, "Air", $"{un}: based at {N(ab)}, which is not an airbase", x.X, x.Y, null, x.Id);
                            if (!Allied(d, ab.Owner, x.Owner)) Add(IssueLevel.Error, "Air", $"{un}: based at {ab}, owned by {TeamName(d, ab.Owner)}", x.X, x.Y, null, x.Id);
                        }
                        break;
                }
            }
        }
        return res;

        void CheckAssigned(CampUnit x, string un)
        {
            // Korea: tutte le brigate e tutti i battaglioni hanno un obiettivo (nessuna eccezione in Save0-5)
            if (x.AssignedObj.IsNull) { Add(IssueLevel.Error, "Units", $"{un}: no objective assigned (in the Korea campaigns every brigade and battalion has one)", x.X, x.Y, null, x.Id); return; }
            var o = d.Obj(x.AssignedObj);
            if (o == null) { Add(IssueLevel.Error, "Units", $"{un}: assigned objective {x.AssignedObj} does not exist", x.X, x.Y, null, x.Id); return; }
            bool own = Allied(d, o.Owner, x.Owner);
            if (!own && x.Orders is 0 or 2 or 6) Add(IssueLevel.Warning, "Units", $"{un}: ordered to {CampUnit.OrdersName(x.Orders)} {o}, which belongs to {TeamName(d, o.Owner)}", x.X, x.Y, o.Id, x.Id);
            if (own && x.Orders is 1 or 3) Add(IssueLevel.Info, "Units", $"{un}: ordered to {CampUnit.OrdersName(x.Orders)} {o}, which is already friendly", x.X, x.Y, o.Id, x.Id);
            double km = Math.Sqrt(Math.Pow(o.X - x.X, 2) + Math.Pow(o.Y - x.Y, 2));
            if (km > 150) Add(IssueLevel.Info, "Units", $"{un}: {km:0} km from its objective {o}", x.X, x.Y, o.Id, x.Id);
        }
    }

    public static bool Allied(CampaignDoc d, int a, int b)
    {
        if (a == b) return true;
        if (d.Teams == null || a >= d.Teams.Teams.Count || b >= d.Teams.Teams.Count) return false;
        var ta = d.Teams.Teams[a]; var tb = d.Teams.Teams[b];
        return ta.CTeam == tb.CTeam || ta.Stance[b] is 1 or 2;
    }

    public static string StanceName(int s) => s >= 0 && s < TeamFile.StanceNames.Length ? TeamFile.StanceNames[s] : s.ToString();

    public static string TeamName(CampaignDoc d, int t) =>
        d.Teams != null && t < d.Teams.Teams.Count && d.Teams.Teams[t].Name.Length > 0 ? d.Teams.Teams[t].Name : $"team {t}";

    private static double Km(CampObjective a, CampObjective b) =>
        a.HasSim && b.HasSim
            ? Math.Sqrt(Math.Pow(a.SimNorth - b.SimNorth, 2) + Math.Pow(a.SimEast - b.SimEast, 2)) / CampaignDoc.FeetPerKm
            : Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static bool CrossesWater(CampObjective a, CampObjective b, Func<double, double, bool> isWater)
    {
        double ax = a.SimEast / CampaignDoc.FeetPerKm, ay = a.SimNorth / CampaignDoc.FeetPerKm;
        double bx = b.SimEast / CampaignDoc.FeetPerKm, by = b.SimNorth / CampaignDoc.FeetPerKm;
        double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        int n = (int)Math.Ceiling(len / 0.5);
        int wet = 0, run = 0;
        for (int i = 1; i < n; i++)
        {
            double t = (double)i / n;
            if (isWater(ax + (bx - ax) * t, ay + (by - ay) * t)) { run++; wet = Math.Max(wet, run); } else run = 0;
        }
        return wet * 0.5 >= 2;   // almeno 2 km di fila in acqua (i ponti corti passano)
    }

    public static string TypeName(int t) => Editor.EditorData.TypeName(t);
}
