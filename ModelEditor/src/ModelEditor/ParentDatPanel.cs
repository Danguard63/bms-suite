using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Dds;
using TerrainTool.Core.Models;

namespace ModelEditor;

/// <summary>
/// Editor di Parent.dat con "Check data" (come LODEditor) e "Suggest values" (Dofs, Switches, raggio e slot calcolati dal modello).
/// Salvare scrive nella cartella di BMS: BMS chiuso, copia di sicurezza prima.
/// </summary>
public sealed class ParentDatPanel : DockPanel
{
    private readonly TextBox _text = new()
    {
        AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), FontSize = 13,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap,
    };
    private readonly ListBox _results = new() { Height = 190, FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _file = new() { Margin = new Thickness(6, 4, 6, 2), FontWeight = FontWeights.SemiBold };
    private string? _dir;
    private string _loaded = "";
    private static bool _warned;

    public event Action<string>? Status;
    public event Action? Saved;
    public bool Dirty => _dir != null && _text.Text != _loaded;

    public ParentDatPanel()
    {
        var bar = new WrapPanel { Margin = new Thickness(6, 2, 6, 4) };
        bar.Children.Add(Btn("Check data", () => RunCheck(), "Checks Parent.dat against the model files (LODs, DOF / switch / slot numbers, textures, radius)"));
        bar.Children.Add(Btn("Suggest values", Suggest, "Computes Dofs, Switches, the radius and the slot count from the model"));
        bar.Children.Add(Btn("Revert", () => { if (_dir != null) Open(_dir); }, "Reload Parent.dat from disk (changes are lost)"));
        bar.Children.Add(Btn("Save", Save, "Writes Parent.dat into BMS (a backup copy is made first)"));
        bar.Children.Add(Btn("Open folder", () => { if (_dir != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_dir}\"") { UseShellExecute = true }); }));
        var help = new TextBlock
        {
            Margin = new Thickness(6, 0, 6, 4), Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Text = "Dimensions = radius minX maxX minY maxY minZ maxZ · TextureSets = liveries · Switches / Dofs = highest number used + 1 · AddSlot = x y z of a weapon station · " +
                   "AddDynamicVertex = x y z · AddLOD = file distance (ft, growing). Lines starting with // are comments.",
        };
        _results.MouseDoubleClick += (_, _) => GoToLine();
        var top = new StackPanel();
        top.Children.Add(_file); top.Children.Add(bar); top.Children.Add(help);
        DockPanel.SetDock(top, Dock.Top); Children.Add(top);
        DockPanel.SetDock(_results, Dock.Bottom); Children.Add(_results);
        Children.Add(_text);
    }

    private static Button Btn(string t, Action a, string? tip = null)
    {
        var b = new Button { Content = t, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
        b.Click += (_, _) => a();
        return b;
    }

    public void Open(string modelDir)
    {
        _dir = modelDir;
        string f = Path.Combine(modelDir, "Parent.dat");
        _file.Text = f;
        try { _loaded = File.Exists(f) ? File.ReadAllText(f) : ""; }
        catch (Exception e) { _loaded = ""; Status?.Invoke("Parent.dat: " + e.Message); }
        _text.Text = _loaded;
        _results.Items.Clear();
    }

    private List<ModelIssue> RunCheck()
    {
        _results.Items.Clear();
        if (_dir == null) return new();
        List<ModelIssue> l;
        try { l = ModelChecks.Check(_dir, _text.Text, ModelPlaces.TexDirsFor(_dir), true, ModelPlaces.RootFor(_dir)); }
        catch (Exception e) { _results.Items.Add(Item(ModelIssue.Error, e.Message)); return new() { new ModelIssue(ModelIssue.Error, "", e.Message) }; }
        if (l.Count == 0) _results.Items.Add(Item("OK", "No problems found."));
        foreach (var i in l.OrderBy(i => i.Severity == ModelIssue.Error ? 0 : i.Severity == ModelIssue.Warning ? 1 : 2)) _results.Items.Add(Item(i.Severity, i.Text));
        Status?.Invoke($"Check data: {l.Count(i => i.Severity == ModelIssue.Error)} error(s), {l.Count(i => i.Severity == ModelIssue.Warning)} warning(s)");
        return l;
    }

    private static ListBoxItem Item(string sev, string text) => new()
    {
        Content = $"{sev,-8} {text}",
        Foreground = sev == ModelIssue.Error ? Brushes.Firebrick : sev == ModelIssue.Warning ? Brushes.DarkOrange : sev == "OK" ? Brushes.ForestGreen : Brushes.DimGray,
        Tag = text,
    };

    private void GoToLine()
    {
        if (_results.SelectedItem is not ListBoxItem { Tag: string t }) return;
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^line (\d+)");
        if (!m.Success) return;
        int line = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
        if (line < 0 || line >= _text.LineCount) return;
        _text.Focus();
        _text.Select(_text.GetCharacterIndexFromLineIndex(line), _text.GetLineLength(line));
        _text.ScrollToLine(line);
    }

    /// <summary>Valori calcolati dal LOD 0: si possono copiare nel testo con "Apply".</summary>
    private void Suggest()
    {
        if (_dir == null) return;
        var p = ParentDat.Parse(_text.Text, _dir);
        var lods = p.All("AddLOD").Where(l => l.Values.Length >= 2).Select(l => (l.Values[0], ParentDat.Num(l.Values[1], out double d) ? d : 0)).ToList();
        if (lods.Count == 0) { MessageBox.Show(Window.GetWindow(this), "Parent.dat has no AddLOD.", "Suggest values"); return; }
        int maxDof = -1, maxSw = -1, maxSlot = -1;
        double radius = 0;
        var info = new BmlModel.ParentInfo(_dir, 1, lods, 0);
        var sb = new StringBuilder();
        for (int i = 0; i < lods.Count; i++)
        {
            try
            {
                var sc = BmlScene.Load(info, i, 0, ModelPlaces.RootFor(_dir), Array.Empty<string>());
                maxDof = Math.Max(maxDof, sc.Dofs.Count == 0 ? -1 : sc.Dofs.Max(d => d.Number));
                maxSw = Math.Max(maxSw, sc.Switches.Count == 0 ? -1 : sc.Switches.Max(s => s.Number));
                maxSlot = Math.Max(maxSlot, sc.Slots.Count == 0 ? -1 : sc.Slots.Max(s => s.Number));
                if (i == 0)
                {
                    var pos = sc.Pose().Meshes.SelectMany(m => m.Positions).ToArray();
                    for (int k = 0; k + 2 < pos.Length; k += 3) radius = Math.Max(radius, Math.Sqrt(pos[k] * pos[k] + pos[k + 1] * pos[k + 1] + pos[k + 2] * pos[k + 2]));
                }
            }
            catch (Exception e) { sb.AppendLine($"LOD {i}: {e.Message}"); }
        }
        int dofs = maxDof + 1, sws = maxSw + 1;
        string cur(string k) => p.First(k) is { Values.Length: > 0 } l ? l.Values[0] : "(missing)";
        sb.Insert(0, $"Values from the model files:\n\n" +
                     $"Dofs       = {dofs}   (now {cur("Dofs")})\n" +
                     $"Switches   = {sws}   (now {cur("Switches")})\n" +
                     $"radius     = {radius:0.##} ft   (Dimensions now {cur("Dimensions")})\n" +
                     $"slots      = {maxSlot + 1}   (AddSlot lines now {p.All("AddSlot").Count()})\n\n" +
                     "Yes = write Dofs and Switches (only if larger than now) and the radius into the text (not saved yet).\n" +
                     "The bounding box and the slot positions are not changed.\n");
        if (MessageBox.Show(Window.GetWindow(this), sb.ToString(), "Suggest values", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        string text = _text.Text;
        text = SetFirst(text, "Dofs", v => int.TryParse(v.FirstOrDefault(), out int n) && n >= dofs ? null : dofs.ToString(CultureInfo.InvariantCulture));
        text = SetFirst(text, "Switches", v => int.TryParse(v.FirstOrDefault(), out int n) && n >= sws ? null : sws.ToString(CultureInfo.InvariantCulture));
        if (radius > 0)
            text = SetFirst(text, "Dimensions", v => v.Length >= 7 ? string.Join(" ", new[] { Math.Ceiling(radius).ToString(CultureInfo.InvariantCulture) }.Concat(v.Skip(1))) : null);
        _text.Text = text;
        RunCheck();
    }

    /// <summary>Cambia i valori della prima riga con quella chiave (o la aggiunge in cima). make: valori attuali → nuovi, null = lascia.</summary>
    private static string SetFirst(string text, string key, Func<string[], string?> make)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            string s = lines[i];
            int c = s.IndexOf("//", StringComparison.Ordinal);
            string body = c >= 0 ? s[..c] : s;
            int eq = body.IndexOf('=');
            if (eq < 0 || !body[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            var vals = body[(eq + 1)..].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var nv = make(vals);
            if (nv == null) return text;
            lines[i] = key.PadRight(17) + "= " + nv + (c >= 0 ? "   " + s[c..] : "");
            return string.Join("\r\n", lines);
        }
        var add = make(Array.Empty<string>());
        if (add == null) return text;
        int at = key == "Dimensions" ? 0 : Math.Min(lines.Count, 1);
        lines.Insert(at, key.PadRight(17) + "= " + add);
        return string.Join("\r\n", lines);
    }

    private void Save()
    {
        if (_dir == null) return;
        var owner = Window.GetWindow(this);
        bool external = ModelPlaces.IsExternal(_dir);
        if (!external && BmsInstaller.IsBmsRunning()) { MessageBox.Show(owner, "Falcon BMS is running: close it first.", "Save Parent.dat", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var issues = RunCheck();
        int err = issues.Count(i => i.Severity == ModelIssue.Error);
        string f = Path.Combine(_dir, "Parent.dat");
        string msg = (external ? "This folder is outside BMS (for example a Blender export): BMS is not touched.\n\n" :
                      !_warned ? "This changes a file inside your Falcon BMS installation. If you have not done it yet, make a backup of your BMS Data folder first.\n\n" : "") +
                     $"Save\n{f}\n\nThe current file is copied to {BackupStore.Root} first (Backups… to put it back)." +
                     (err > 0 ? $"\n\nCheck data found {err} error(s): save anyway?" : "");
        if (MessageBox.Show(owner, msg, "Save Parent.dat", MessageBoxButton.YesNo, err > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!external) _warned = true;
        try
        {
            var copy = BackupStore.Save(f);
            File.WriteAllText(f, _text.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
            _loaded = _text.Text;
            Status?.Invoke($"Saved {f}" + (copy != null ? $" (previous version: {copy})" : ""));
            Saved?.Invoke();
        }
        catch (Exception e) { MessageBox.Show(owner, e.Message, "Save Parent.dat", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
