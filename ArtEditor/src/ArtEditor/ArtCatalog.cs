namespace ArtEditor;

/// <summary>Come si sostituisce un'immagine: semplice (ogni voce alla sua misura), bandiera (4 voci da un'immagine), patch.</summary>
public enum SlotKind { Image, Flag, Patch }

/// <summary>
/// Una "casella" dell'Art: un'immagine che BMS mostra in un punto preciso dell'interfaccia. Puo' stare in piu' archivi
/// (_FHD per l'interfaccia Full HD, Resource per quella classica), ognuno con la sua misura: si sostituisce in tutti.
/// </summary>
public sealed record ArtSlot(string Category, string Title, string Where, SlotKind Kind, string[] Archives, string[] Entries, string? Note = null)
{
    /// <summary>Voce mostrata in anteprima (per le bandiere: quella orizzontale).</summary>
    public string PreviewEntry => Kind == SlotKind.Flag ? Entries.First(e => e.EndsWith("_HRZ")) : Entries[0];
    public override string ToString() => Title;
}

/// <summary>
/// Le caselle dell'Art di un teatro BMS 4.38, divise per categoria. Nomi e misure delle voci studiati sugli archivi della 4.38
/// (Data\Art\_FHD e Data\Art\Resource); quello che e' solo interfaccia (pulsanti, icone) non c'e': non e' del teatro.
/// </summary>
public static class ArtCatalog
{
    public const string Flags = "Team flags";
    public const string Menu = "Theater banner & menu";
    public const string Screens = "Screen backgrounds";
    public const string Campaigns = "Campaign pictures";
    public const string Maps = "Small maps (from the terrain)";
    public const string Medals = "Medals & logos";
    public const string Patches = "Squadron patches";
    public const string Texts = "Texts (Theater.irc)";
    public const string Visual = "Screens (visual editor)";
    public const string FontsCat = "Fonts";
    public const string SoundsCat = "Sounds";

    public static readonly string[] Categories = { Visual, Flags, Menu, Campaigns, Maps, Screens, Medals, Patches, Texts, FontsCat, SoundsCat };

    public static string CategoryHelp(string c) => c switch
    {
        Flags => "Flags of the teams. A team uses the flag slot of its 'Flag' number (set in the Campaign Editor, Teams tab). " +
                 "The slot names are the Korea ones, but each theater puts its own flags there: one image fills the four versions BMS uses " +
                 "(horizontal, small, vertical for the OOB and its darker copy).",
        Menu => "The images that say which theater this is: main menu background and the banner at the bottom of the screens.",
        Campaigns => "Pictures of the campaigns in the campaign selection screen (slot 1 = the first campaign, and so on). Names and descriptions are in Texts.",
        Maps => "Small maps of the theater used in the menus (Instant Action, TE, campaign, priorities, recon). The TerrainTool makes them from the terrain " +
                "(tab 7, Theater art); you can also put your own image.",
        Screens => "Backgrounds of other screens. BMS uses generic pictures here: change them only if you want a full theater look.",
        Medals => "Medals and logos with a theater meaning (the Korea campaign medal, the UN logo).",
        Patches => "Squadron patches (96×96) shown in the squadron lists and in the logbook. Each patch has an id in Patches\\ImageIds.id (70001, 70002...): " +
                   "a squadron of the campaign uses it as that id minus 70001 (70030 → patch 29, chosen in the Campaign Editor's squadron window). New patches go at the end of the list.",
        SoundsCat => "The sounds of the interface (button clicks, mouse over, windows...): listen to them, see how many buttons use each one, and replace a sound " +
                     "with another .ogg file for this theater only (the KTO sounds are not changed). Which sound a button makes is chosen in the visual editor: right-click → Button look and sound.",
        FontsCat => "The interface fonts of BMS (Art\\Fonts): pixel fonts at a fixed height, made from TrueType fonts. A font can be replaced with another TrueType font " +
                    "(only for this theater: the KTO fonts are not changed) or a new one added, then chosen for a text with 'Change font…' in the visual editor.",
        Texts => "Country and team names and the campaign titles and descriptions (Art\\Main\\Theater.irc, also in _FHD).",
        Visual => "The screens of BMS as the theater shows them (Full HD). The KTO (Data\\Art) is the master of every theater: whatever the theater's art does not have comes from it. " +
                  "The editor never changes the KTO: a KTO image you replace is copied into the theater's art; KTO texts and window layouts cannot be changed here.",
        _ => "",
    };

    private static readonly string[] MainA = { @"_FHD\Main.idx", @"Resource\Main.idx" };
    private static readonly string[] BgA = { @"_FHD\MainBg.idx", @"Resource\MainBg.idx" };
    private static readonly string[] SelA = { @"_FHD\Select.idx", @"Resource\Select.idx" };
    private static readonly string[] IntelA = { @"_FHD\Intel.idx", @"Resource\Intel.idx" };
    private static readonly string[] CampBgA = { @"_FHD\CampBg.idx", @"Resource\CampBg.idx" };
    public const string PatchArchive = @"Resource\Patches.idx";
    public const string PatchIds = @"Patches\ImageIds.id";

    /// <summary>Posti delle bandiere: numero della bandiera della squadra → nome del posto (oltre il 7 BMS non li usa per le squadre).</summary>
    public static readonly string[] FlagSlotNames = { "USA", "SKOREA", "JAPAN", "CIS", "CHINA", "NKOREA", "NATO", "FRANCE", "SWISS", "BELGIUM", "BAHRAIN", "BRAZIL", "GERMANY" };

    public static readonly List<ArtSlot> Slots = Build();

    private static List<ArtSlot> Build()
    {
        var l = new List<ArtSlot>();
        for (int i = 0; i < FlagSlotNames.Length; i++)
        {
            string s = FlagSlotNames[i];
            bool team = i < 7;
            l.Add(new ArtSlot(Flags, team ? $"Flag {i + 1} · slot {s}" : $"Extra flag · slot {s}",
                team ? $"Teams with Flag = {i + 1}: TE and campaign team lists, OOB, squadron and unit lists, debriefing."
                     : "Extra slot of BMS: not used by the team flag numbers 1-7.",
                SlotKind.Flag, MainA, new[] { $"OOB_FLAGS_{s}_HRZ", $"SMFLGS_{s}", $"OOB_FLAGS_{s}", $"OOB_FLAGS_{s}_DRK" }));
        }
        l.Add(new ArtSlot(Menu, "Main menu background", "Background of the BMS main menu (UI page after the start).", SlotKind.Image, BgA, new[] { "MAIN_SCRN" }));
        l.Add(new ArtSlot(Menu, "Theater banner (campaign)", "Theater name at the bottom of the campaign and TE screens (Korea: 'KOREA THEATER OPERATION').", SlotKind.Image, MainA, new[] { "KoreaKTO" },
            "Transparent background: use a PNG with transparency (the transparent pixels become BMS's magenta key colour)."));
        l.Add(new ArtSlot(Menu, "Theater banner (TvT)", "Same banner, second version (Korea TvT).", SlotKind.Image, MainA, new[] { "KoreaTvT" },
            "Transparent background: use a PNG with transparency."));
        l.Add(new ArtSlot(Menu, "Exit screen", "Background shown when you leave BMS.", SlotKind.Image, new[] { @"_FHD\Main.idx" }, new[] { "EXIT_BACKGROUND" }));

        string[] camp = { "TIGER_SPIRIT", "ROLLING_FIRE", "IRON_FORTRESS", "BEAR_TRAP", "DOUBLE_DRAGON", "MANTIS_AT_DAWN" };
        for (int i = 0; i < camp.Length; i++)
            l.Add(new ArtSlot(Campaigns, $"Campaign {i + 1} picture", $"Campaign selection screen, campaign {i + 1} (Korea: {Pretty(camp[i])}).", SlotKind.Image, SelA, new[] { $"CAMP_{camp[i]}_NEW" }));

        const string fromTerrain = "Made by the TerrainTool from the terrain (tab 7, Theater art): run it again after changing the terrain.";
        l.Add(new ArtSlot(Maps, "Instant Action map", "Map of the Instant Action screen.", SlotKind.Image, MainA, new[] { "MAP_SMALL" }, "Made by the TerrainTool '2D map (campmap)' job."));
        l.Add(new ArtSlot(Maps, "Campaign small map", "'Team Territory' box of the TE and the campaign map (BMS colours the team areas over it).", SlotKind.Image, MainA, new[] { "CAMP_SMAP" }, fromTerrain));
        l.Add(new ArtSlot(Maps, "Instant Action small map", "Small map of the Instant Action and TE windows.", SlotKind.Image, MainA, new[] { "IA_MAP_TGA" }, fromTerrain));
        l.Add(new ArtSlot(Maps, "Review map", "Map of the review windows (in colour).", SlotKind.Image, MainA, new[] { "REVI_MAP_TGA" }, fromTerrain));
        l.Add(new ArtSlot(Maps, "TE main map", "Map of the TE main screen.", SlotKind.Image, MainA, new[] { "TE_MAINMAP" }, fromTerrain));
        l.Add(new ArtSlot(Maps, "Priorities map", "Map of the campaign Priorities window.", SlotKind.Image, IntelA, new[] { "KOREA_256" }, fromTerrain));
        l.Add(new ArtSlot(Maps, "Recon (JSTARS) map", "Map of the JSTARS / recon window.", SlotKind.Image, IntelA, new[] { "JSTARS_MAP" }, fromTerrain));
        l.Add(new ArtSlot(Maps, "Priorities regions", "Regions of the Priorities window (from the campaign's .pak regions).", SlotKind.Image, IntelA, new[] { "PAK_REGION_OVERLAY" }, fromTerrain));
        l.Add(new ArtSlot(Maps, "Priorities region outlines", "Outlines of the regions (transparent background).", SlotKind.Image, IntelA, new[] { "PAK_OUTLINE_OVERLAY" }, fromTerrain));

        l.Add(new ArtSlot(Screens, "TE screen", "Background of the Tactical Engagement screen.", SlotKind.Image, CampBgA, new[] { "TE_MAIN_TGA" }));
        l.Add(new ArtSlot(Screens, "Campaign screen", "Background of the campaign screen.", SlotKind.Image, CampBgA, new[] { "CB_MAIN_TGA" }));
        l.Add(new ArtSlot(Screens, "Base screen", "Background of the base / squadron screens.", SlotKind.Image, new[] { @"_FHD\CampBg.idx" }, new[] { "BASEBG" }));

        l.Add(new ArtSlot(Medals, "Campaign medal", "Logbook: the medal of the theater campaign (Korea campaign medal).", SlotKind.Image, MainA, new[] { "MEDAL_KOREACAMP" },
            "Transparent background: use a PNG with transparency."));
        l.Add(new ArtSlot(Medals, "UN logo", "Logo shown in the menus (Korea: UN).", SlotKind.Image, MainA, new[] { "UNLOGO_TGA" }, "Transparent background: use a PNG with transparency."));
        return l;
    }

    private static string Pretty(string s) => string.Join(' ', s.Split('_').Select(w => w[0] + w[1..].ToLowerInvariant()));

    /// <summary>Casella di una patch (le patch non sono fisse: dipendono dall'archivio aperto).</summary>
    public static ArtSlot PatchSlot(string entry, int? id) =>
        new(Patches, entry, id != null ? $"Squadron patch id {id} in Patches\\ImageIds.id: squadrons use it as patch {id - TerrainTool.Core.Campaign.SquadronPatches.IdBase} (Campaign Editor, squadron window)." : "Squadron patch without a number in Patches\\ImageIds.id: the squadrons cannot use it.",
            SlotKind.Patch, new[] { PatchArchive }, new[] { entry }, "Transparent background: use a PNG with transparency.");

    /// <summary>Etichetta leggibile di una voce di Theater.irc.</summary>
    public static string TextLabel(string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(key, @"^TXT_(COUNTRY|TEAM)_(\d+)$");
        if (m.Success) return (m.Groups[1].Value == "TEAM" ? "Team " : "Country ") + m.Groups[2].Value + " name";
        m = System.Text.RegularExpressions.Regex.Match(key, @"^TXT_SCENARIO_(\d+)$");
        if (m.Success) return $"Campaign {m.Groups[1].Value} · title";
        m = System.Text.RegularExpressions.Regex.Match(key, @"^TXT_SC_(\d+)_(.+)$");
        if (m.Success)
        {
            string w = m.Groups[2].Value;
            string what = w.StartsWith("LINE_") ? "description line " + w[5..] : w switch
            {
                "CAMP_END" => "max campaign days", "CAMP_STALE" => "stalemate (days without activity)", "CAMP_DIFF" => "difficulty", _ => w.ToLowerInvariant(),
            };
            return $"Campaign {m.Groups[1].Value} · {what}";
        }
        return key;
    }
}
