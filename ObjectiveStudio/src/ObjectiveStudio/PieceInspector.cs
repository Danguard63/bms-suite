using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Editor;

namespace ObjectiveStudio;

/// <summary>
/// Piece inspector: the buildings (features) of an objective one by one, with their 3D model and textures, the FCD data
/// of Falcon4_FCD.xml (hit points, repair time, damage per warhead kind, ...) and how many objectives of the theater use them.
/// Read only.
/// </summary>
public static class PieceInspector
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static ModelViewWindow? _open;
    private static readonly Dictionary<(EditorData, int), (int Objectives, int Layouts, List<string> Examples)> UsageCache = new();

    public static void Open(Window owner, EditorData d, EdObjective o, int? startCt = null)
    {
        var feats = d.LayoutOf(o).Features;
        var groups = feats.GroupBy(f => f.CtIdx).Select(g => (Ct: g.Key, Name: d.FeatureName(g.Key), Nums: g.Select(f => f.Num).OrderBy(n => n).ToList()))
                          .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var items = groups.Select(g =>
        {
            string? dir = d.FeatureModelDir(g.Ct);
            return new ModelViewWindow.Vehicle($"{g.Name}  ×{g.Nums.Count}", g.Ct, dir, dir == null ? null : d.TextureDirsFor(dir));
        }).ToList();
        int start = startCt is int sc ? groups.FindIndex(g => g.Ct == sc) : -1;
        try { _open?.Close(); } catch (InvalidOperationException) { }
        _open = ModelViewWindow.OpenPieces(owner, $"{o.Name} – {groups.Count} kinds of pieces, {feats.Count} pieces", items, start,
            v => Side(d, o, v, groups.First(g => g.Ct == v.Ct).Nums));
    }

    private static UIElement Side(EditorData d, EdObjective o, ModelViewWindow.Vehicle v, List<int> nums)
    {
        var sp = new StackPanel();
        var (graphics, fcd) = d.FeatureIds(v.Ct);
        var data = d.FeatureData(v.Ct);
        string F(string k) => data.FirstOrDefault(x => x.Field == k).Value ?? "";

        sp.Children.Add(new TextBlock { Text = d.FeatureName(v.Ct), FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(Small($"CT {v.Ct}  ·  FCD {fcd}  ·  model {graphics}" + (v.ModelDir == null ? "  (no model folder)" : "")));
        if (v.ModelDir != null) sp.Children.Add(Small(v.ModelDir));
        sp.Children.Add(Small($"In {o.Name}: {nums.Count} piece(s) – number {string.Join(", ", nums.Take(20))}{(nums.Count > 20 ? " …" : "")}"));

        if (!UsageCache.TryGetValue((d, v.Ct), out var use)) UsageCache[(d, v.Ct)] = use = d.FeatureUsage(v.Ct);
        sp.Children.Add(Small($"Used by {use.Objectives} objective(s) of the theater ({use.Layouts} layout(s))" +
                              (use.Examples.Count > 0 ? ": " + string.Join(", ", use.Examples) + (use.Objectives > use.Examples.Count ? " …" : "") : "")));

        if (data.Count == 0) { sp.Children.Add(Small("No FCD record found for this piece.")); return sp; }

        var used = new HashSet<string>();
        Head(sp, "Strength");
        Row(sp, used, data, "HitPoints", "Hit points", "how much damage it takes to destroy it");
        Row(sp, used, data, "RepairTime", "Repair time", "hours the campaign needs to repair it");
        Row(sp, used, data, "DisplayPriority", "Display priority", "lower = drawn also with low object detail");
        if (int.TryParse(F("Flags"), NumberStyles.Integer, Ci, out int fl))
        {
            used.Add("Flags");
            var bits = Enumerable.Range(0, 32).Where(b => (fl & (1 << b)) != 0).Select(b => b < FlagNames.Length ? FlagNames[b] : $"0x{1 << b:X}");
            Add(sp, "Flags", $"{fl}:  {string.Join(", ", bits)}", "feature flags of the FCD record (names as in the BMS Editor)");
        }
        Row(sp, used, data, "RampHeight", "Ramp height", null);
        Row(sp, used, data, "RampAngle", "Ramp angle", null);
        Row(sp, used, data, "RadarIdx", "Radar", "radar data index (0 = none)");

        // luci: "Has Lights" = le luci del modello (piste, rullaggio, ostacoli) si accendono e spengono con l'interruttore della torre;
        // DynamicLights = luci vere che illuminano intorno (fari degli hangar), testo protobuf nel record FCD
        var dyn = data.FirstOrDefault(x => x.Field == "DynamicLights").Value;
        if ((fl & 4) != 0 || !string.IsNullOrEmpty(dyn))
        {
            Head(sp, "Lights");
            if ((fl & 4) != 0) sp.Children.Add(Small("Has Lights: the lights of the 3D model (runway, taxiway, obstruction lights…) are switched on and off by the tower (ATC), at night and in bad weather."));
            if (!string.IsNullOrEmpty(dyn))
            {
                used.Add("DynamicLights");
                var act = System.Text.RegularExpressions.Regex.Match(dyn, @"^\s*activation:\s*(\w+)");
                if (act.Success) Add(sp, "Switched on", act.Groups[1].Value switch
                {
                    "LIGHT_DEFAULT_ON_WHEN_SWITCH_IS_ON" => "with the tower light switch",
                    "LIGHT_ON_WHEN_PARKING_SPOT_IS_OCCUPIED" => "when its parking spot is occupied",
                    var x => x,
                }, null);
                int k = 0;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(dyn, @"light\s*\{(.*?)\}\s*(?=light\s*\{|$)", System.Text.RegularExpressions.RegexOptions.Singleline))
                {
                    string b = m.Groups[1].Value;
                    string V(string re) { var x = System.Text.RegularExpressions.Regex.Match(b, re); return x.Success ? x.Groups[1].Value : ""; }
                    string Vec(string name) { var x = System.Text.RegularExpressions.Regex.Match(b, name + @"\s*\{([^}]*)\}"); return x.Success ? x.Groups[1].Value.Trim() : "-"; }
                    string type = V(@"type:\s*(\w+)") switch { "LIGHT_FEATURE_SPOT" => "spot (cone)", "LIGHT_FEATURE_POINT" => "point", "LIGHT_DEFAULT_OR_FLOOD" => "flood", var x => x };
                    Add(sp, $"Light {++k}", $"{type}, radius {V(@"radius_ft:\s*([\d.]+)")} ft, colour {{{Vec("color")}}}, at {{{Vec("position")}}}, pointing {{{Vec("direction")}}}" +
                        (V(@"outer_cone_deg:\s*([\d.]+)") is { Length: > 0 } oc ? $", cone {V(@"inner_cone_deg:\s*([\d.]+)")}–{oc}°" : ""), "position/direction in model feet");
                }
            }
        }

        var dam = data.Where(x => x.Field.StartsWith("Dam_", StringComparison.Ordinal)).ToList();
        if (dam.Count > 0)
        {
            Head(sp, "Damage taken per warhead kind (%)");
            sp.Children.Add(Small("100 = full damage, 0 = that kind of weapon does nothing to it."));
            foreach (var (k, val) in dam) { used.Add(k); Bar(sp, k[4..].Replace("Incendairy", "Incendiary"), val); }
        }
        var det = data.Where(x => x.Field.StartsWith("Det_", StringComparison.Ordinal)).ToList();
        if (det.Count > 0)
        {
            Head(sp, "Detection range by kind (km, 0 = default)");
            foreach (var (k, val) in det) { used.Add(k); Add(sp, k[4..], val, null); }
        }
        used.Add("Name"); used.Add("CtIdx");
        var rest = data.Where(x => !used.Contains(x.Field)).ToList();
        if (rest.Count > 0)
        {
            Head(sp, "Other fields");
            foreach (var (k, val) in rest) Add(sp, k, val, null);
        }
        sp.Children.Add(Small("\nRead only: the values come from Falcon4_FCD.xml of the theater."));
        return sp;
    }

    /// <summary>FCD Flags bits, names from the BMS Editor (Edit Flags).</summary>
    private static readonly string[] FlagNames =
    {
        "Enemy Can Repair", "Is Virtual", "Has Lights", "Has Smoke Stack", "Is Flat Container", "Is Elevated Container",
        "Container Top", "Next Is Top", "Do Not Evaluate", "Point Trackable", "Is Tacan",
    };

    private static TextBlock Small(string s) => new() { Text = s, FontSize = 11, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
    private static void Head(StackPanel sp, string s) => sp.Children.Add(new TextBlock { Text = s, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 3) });

    private static void Row(StackPanel sp, HashSet<string> used, IReadOnlyList<(string Field, string Value)> data, string key, string label, string? tip)
    {
        used.Add(key);
        var v = data.FirstOrDefault(x => x.Field == key).Value;
        if (v != null) Add(sp, label, v, tip);
    }

    private static void Add(StackPanel sp, string label, string value, string? tip)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1), ToolTip = tip };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, Foreground = Brushes.DimGray });
        var t = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(t, 1); g.Children.Add(t);
        sp.Children.Add(g);
    }

    private static void Bar(StackPanel sp, string label, string value)
    {
        double.TryParse(value, NumberStyles.Float, Ci, out double pct);
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, Foreground = Brushes.DimGray });
        var track = new Border { Background = new SolidColorBrush(Color.FromRgb(230, 230, 226)), Height = 10, VerticalAlignment = VerticalAlignment.Center };
        var fill = new Border { Background = pct >= 66 ? Brushes.IndianRed : pct >= 25 ? Brushes.Orange : Brushes.SeaGreen, Height = 10,
                                HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Clamp(pct, 0, 100) * 1.5 };
        var cell = new Grid(); cell.Children.Add(track); cell.Children.Add(fill);
        Grid.SetColumn(cell, 1); g.Children.Add(cell);
        var t = new TextBlock { Text = "  " + value, };
        Grid.SetColumn(t, 2); g.Children.Add(t);
        sp.Children.Add(g);
    }
}
