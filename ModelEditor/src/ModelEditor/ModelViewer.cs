using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Models;

namespace ModelEditor;

/// <summary>
/// Vista 3D di un modello con i suoi DOF (cursori con il nome), switch (caselle), slot (armi montate) e foto della vista.
/// Sola lettura: niente viene scritto nei file di BMS.
/// </summary>
public sealed class ModelViewer : DockPanel
{
    private readonly ComboBox _lod = new() { MinWidth = 150, Margin = new Thickness(0, 0, 10, 0) };
    private readonly ComboBox _set = new() { MinWidth = 90, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _markers = new() { Content = "Slot markers", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        ToolTip = "Blue cubes: the weapon stations (slots) where BMS hangs weapons and pods – Slots tab to mount a weapon" };
    private readonly CheckBox _lights = new() { Content = "Lights", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        ToolTip = "Draw the lights (glowing) – the ones inside light switches show when that switch is on (Switches tab: Lights on)" };
    private readonly CheckBox _ground = new() { Content = "Ground", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _dx = new() { Content = "DirectX (PBR)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontWeight = FontWeights.SemiBold,
        ToolTip = "Draw with DirectX 11 like BMS: PBR materials (normal maps, metal, roughness, contact shadows), sun shadows, antialiasing. Off = the classic view" };
    private readonly CheckBox _nrm = new() { Content = "Normal maps", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed,
        ToolTip = "Surface detail (panel lines, rivets) from the normal maps – off to compare" };
    private readonly CheckBox _shadows = new() { Content = "Shadows", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed };
    private HelixView? _dxView;
    private readonly TextBlock _info = new() { Margin = new Thickness(8, 2, 8, 4), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
    private readonly Viewport3D _view = new();
    private readonly ModelVisual3D _content = new();
    private readonly PerspectiveCamera _cam = new() { FieldOfView = 40, UpDirection = new Vector3D(0, 1, 0) };
    private readonly Border _surface;
    private readonly StackPanel _dofPanel = new();
    private readonly StackPanel _swPanel = new();
    private readonly StackPanel _slotPanel = new();
    private readonly TextBox _dofFilter = new() { Margin = new Thickness(0, 0, 0, 4), ToolTip = "Filter DOFs by name or number" };

    private string? _dir;
    private string _title = "";
    private BmlModel.ParentInfo? _parent;
    private BmlScene? _scene;
    private readonly Dictionary<int, double> _dofValues = new();
    private readonly Dictionary<(int, int), bool> _switchOn = new();
    private readonly Dictionary<int, ModelEntry> _mounted = new();
    private readonly Dictionary<string, BmlModel?> _weaponCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _filling;
    private int _loadNo, _poseNo;

    private Point3D _target;
    private double _yaw = -2.3, _pitch = 0.32, _dist = 50, _radius = 20;
    private Point? _drag; private bool _panning;
    private Rect3D _bounds = Rect3D.Empty;

    /// <summary>Armi che si possono montare negli slot (dalla finestra principale).</summary>
    public List<ModelEntry> Weapons { get; set; } = new();
    public BmsNames Names { get; set; } = new();
    public string? ModelDir => _dir;
    public BmlScene? Scene => _scene;
    public event Action<string>? Status;

    public ModelViewer()
    {
        var bar = new WrapPanel { Margin = new Thickness(8, 6, 8, 2) };
        void L(string s) => bar.Children.Add(new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        L("Detail (LOD)"); bar.Children.Add(_lod);
        L("Livery"); bar.Children.Add(_set);
        bar.Children.Add(_markers); bar.Children.Add(_lights); bar.Children.Add(_ground);
        bar.Children.Add(_dx); bar.Children.Add(_nrm); bar.Children.Add(_shadows);
        bar.Children.Add(Btn("Reset view", ResetView, "Or double-click on the model"));
        bar.Children.Add(Btn("Front", () => SetView(Math.PI, 0.05)));
        bar.Children.Add(Btn("Side", () => SetView(-Math.PI / 2, 0.05)));
        bar.Children.Add(Btn("Top", () => SetView(Math.PI, 1.55)));
        bar.Children.Add(Btn("Screenshot…", Screenshot, "Save the view as a PNG (twice the window size)"));
        _lod.SelectionChanged += (_, _) => { if (!_filling) _ = LoadSceneAsync(false); };
        _set.SelectionChanged += (_, _) => { if (!_filling) _ = LoadSceneAsync(false); };
        _markers.Click += (_, _) => Repose();
        _lights.Click += (_, _) => Repose();
        _ground.Click += (_, _) => Repose();
        _dx.Click += (_, _) => SetRenderer(_dx.IsChecked == true);
        _nrm.Click += (_, _) => { if (_dxView != null) _dxView.NormalMaps = _nrm.IsChecked == true; Repose(); };
        _shadows.Click += (_, _) => { if (_dxView != null) _dxView.Shadows = _shadows.IsChecked == true; Repose(); };

        _view.Camera = _cam;
        _view.Children.Add(new ModelVisual3D
        {
            Content = new Model3DGroup
            {
                Children =
                {
                    new AmbientLight(Color.FromRgb(120, 120, 120)),
                    new DirectionalLight(Color.FromRgb(215, 210, 200), new Vector3D(-0.5, -1, -0.6)),
                    new DirectionalLight(Color.FromRgb(95, 100, 110), new Vector3D(0.6, -0.2, 0.8)),
                },
            },
        });
        _view.Children.Add(_content);
        _surface = new Border { Background = new LinearGradientBrush(Color.FromRgb(196, 214, 232), Color.FromRgb(236, 236, 230), 90), Child = _view, ClipToBounds = true };
        HookMouse(_surface);

        // pannello a destra: DOF, switch, slot
        var side = new TabControl { Width = 360, Margin = new Thickness(4, 0, 4, 4) };
        _dofFilter.TextChanged += (_, _) => FilterDofs();
        var dofTop = new StackPanel();
        dofTop.Children.Add(_dofFilter);
        var dofBtns = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        dofBtns.Children.Add(Btn("All to rest", () => { _dofValues.Clear(); FillDofs(); Repose(); }));
        dofBtns.Children.Add(Btn("Gear down", () => Gear(true), "Gear and doors open (DOFs 19-24 to their limit) and the gear switches on (1-3 and 20-22), as BMS does when the gear is out"));
        dofBtns.Children.Add(Btn("Gear up", () => Gear(false), "Gear retracted: DOFs 19-24 to rest and the gear switches off"));
        dofTop.Children.Add(dofBtns);
        var dofDock = new DockPanel();
        DockPanel.SetDock(dofTop, Dock.Top); dofDock.Children.Add(dofTop);
        dofDock.Children.Add(new ScrollViewer { Content = _dofPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        side.Items.Add(new TabItem { Header = "DOFs", Content = dofDock });

        var swBtns = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        swBtns.Children.Add(Btn("Defaults", () => { _switchOn.Clear(); FillSwitches(); Repose(); }, "As the model is seen in BMS (switches on at start, plus the tail number branch)"));
        swBtns.Children.Add(Btn("All on", () => SetAllSwitches(true)));
        swBtns.Children.Add(Btn("All off", () => SetAllSwitches(false)));
        swBtns.Children.Add(Btn("Lights on", () => SetLights(true), "Nav, strobe, landing, formation lights… (switches 7, 8, 9, 29, 47, 62 and those named as lights)"));
        swBtns.Children.Add(Btn("Lights off", () => SetLights(false)));
        var swDock = new DockPanel();
        DockPanel.SetDock(swBtns, Dock.Top); swDock.Children.Add(swBtns);
        swDock.Children.Add(new ScrollViewer { Content = _swPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        side.Items.Add(new TabItem { Header = "Switches", Content = swDock });

        var slBtns = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        slBtns.Children.Add(Btn("Clear all", () => { _mounted.Clear(); FillSlots(); Repose(); }));
        var slDock = new DockPanel();
        DockPanel.SetDock(slBtns, Dock.Top); slDock.Children.Add(slBtns);
        slDock.Children.Add(new ScrollViewer { Content = _slotPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        side.Items.Add(new TabItem { Header = "Slots", Content = slDock });

        var help = new TextBlock
        {
            Margin = new Thickness(8, 2, 8, 4), Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Text = "Left drag = rotate · right drag = move · wheel = zoom · double-click = reset. Each slider is the value BMS gives to that DOF; every part applies its own multiplier and limits (shown: value → result). " +
                   "Blue cubes = weapon stations (slots); glowing cubes = lights (Lights). Models are read from BMS (read only).",
        };
        DockPanel.SetDock(bar, Dock.Top); Children.Add(bar);
        DockPanel.SetDock(_info, Dock.Top); Children.Add(_info);
        DockPanel.SetDock(help, Dock.Bottom); Children.Add(help);
        DockPanel.SetDock(side, Dock.Right); Children.Add(side);
        Children.Add(_surface);
        if (ReadSetting("renderer.txt") == "dx") Loaded += FirstLoaded;
    }

    private void FirstLoaded(object sender, RoutedEventArgs e) { Loaded -= FirstLoaded; _dx.IsChecked = true; SetRenderer(true); }

    private static string? ReadSetting(string name)
    {
        try { var f = Path.Combine(MainWindow.Root(), "Settings", name); return File.Exists(f) ? File.ReadAllText(f).Trim() : null; } catch { return null; }
    }

    private static void SaveSetting(string name, string value)
    {
        try { var d = Directory.CreateDirectory(Path.Combine(MainWindow.Root(), "Settings")); File.WriteAllText(Path.Combine(d.FullName, name), value); } catch { }
    }

    /// <summary>Vista classica (WPF) o DirectX (PBR). Se DirectX non parte si resta sulla classica e si dice perche'.</summary>
    private void SetRenderer(bool dx)
    {
        if (dx && _dxView == null)
        {
            _dxView = HelixView.TryCreate(out var err);
            if (_dxView == null)
            {
                _dx.IsChecked = false;
                Status?.Invoke("DirectX view not available: " + err + " – the classic view stays.");
                return;
            }
            _dxView.NormalMaps = _nrm.IsChecked == true; _dxView.Shadows = _shadows.IsChecked == true;
        }
        _surface.Child = dx ? _dxView : _view;
        _nrm.Visibility = _shadows.Visibility = dx ? Visibility.Visible : Visibility.Collapsed;
        SaveSetting("renderer.txt", dx ? "dx" : "classic");
        UpdateCamera();
        Repose();
    }

    private static Button Btn(string text, Action a, string? tip = null)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 2), ToolTip = tip };
        b.Click += (_, _) => a();
        return b;
    }

    // ───────────────────────── modello

    public void Clear(string message)
    {
        _dir = null; _parent = null; _scene = null; _content.Content = null;
        _filling = true; _lod.Items.Clear(); _set.Items.Clear(); _filling = false;
        _dofPanel.Children.Clear(); _swPanel.Children.Clear(); _slotPanel.Children.Clear();
        _info.Text = message;
    }

    /// <summary>Apre un modello (cartella Models\&lt;n&gt;).</summary>
    public void Open(string modelDir, string title)
    {
        bool same = string.Equals(_dir, modelDir, StringComparison.OrdinalIgnoreCase);
        _dir = modelDir; _title = title;
        if (!same) { _dofValues.Clear(); _switchOn.Clear(); _mounted.Clear(); }
        try { _parent = BmlModel.ReadParent(modelDir); }
        catch (Exception e) { Clear($"{title}: Parent.dat – {e.Message}"); _dir = modelDir; return; }
        if (_parent == null || _parent.Lods.Count == 0) { Clear($"{title}: Parent.dat has no LOD."); _dir = modelDir; return; }
        int oldLod = _lod.SelectedIndex, oldSet = _set.SelectedIndex;
        _filling = true;
        _lod.Items.Clear(); _set.Items.Clear();
        for (int i = 0; i < _parent.Lods.Count; i++)
        {
            var f = Path.Combine(_parent.Folder, _parent.Lods[i].File);
            string size = File.Exists(f) ? $"{new FileInfo(f).Length / 1024.0 / 1024.0:0.0} MB" : "missing";
            _lod.Items.Add(new ComboBoxItem { Content = $"{i} – {_parent.Lods[i].File} ({_parent.Lods[i].Distance:0} ft, {size})" });
        }
        for (int i = 0; i < _parent.TextureSets; i++) _set.Items.Add(new ComboBoxItem { Content = i == 0 ? "0 (default)" : i.ToString(CultureInfo.InvariantCulture) });
        _lod.SelectedIndex = same && oldLod >= 0 && oldLod < _lod.Items.Count ? oldLod : 0;
        _set.SelectedIndex = same && oldSet >= 0 && oldSet < _set.Items.Count ? oldSet : 0;
        _filling = false;
        _ = LoadSceneAsync(!same);
    }

    /// <summary>Rilegge il modello da disco (dopo aver salvato Parent.dat).</summary>
    public void Reload() { if (_dir != null) { SceneBuilder.ForgetTextures(); HelixView.ForgetTextures(); _weaponCache.Clear(); var d = _dir; _dir = null; Open(d, _title); } }

    private async Task LoadSceneAsync(bool resetView)
    {
        var parent = _parent; var dir = _dir;
        if (parent == null || dir == null) return;
        int lod = Math.Max(0, _lod.SelectedIndex), set = Math.Max(0, _set.SelectedIndex);
        int no = ++_loadNo;
        _info.Text = $"{_title}: loading…";
        Cursor = Cursors.Wait;
        try
        {
            var root = ModelPlaces.RootFor(dir);
            var tex = ModelPlaces.TexDirsFor(dir);
            var scene = await Task.Run(() => BmlScene.Load(parent, lod, set, root, tex));
            if (no != _loadNo) return;
            _scene = scene;
            FillDofs(); FillSwitches(); FillSlots();
            await PoseAsync(resetView);
        }
        catch (Exception e)
        {
            if (no == _loadNo) { _info.Text = $"{_title}: the model could not be read – {e.Message}"; _content.Content = null; }
        }
        finally { if (no == _loadNo) Cursor = null; }
    }

    private void Repose() => _ = PoseAsync(false);

    private async Task PoseAsync(bool resetView)
    {
        var scene = _scene; var dir = _dir;
        if (scene == null || dir == null) return;
        int no = ++_poseNo;
        await Task.Delay(25);                 // i cursori mandano molti valori: si disegna solo l'ultimo
        if (no != _poseNo) return;
        var dofs = new Dictionary<int, double>(_dofValues);
        var sw = new Dictionary<(int, int), bool>(_switchOn);
        var mounted = new Dictionary<int, ModelEntry>(_mounted);
        bool markers = _markers.IsChecked == true, ground = _ground.IsChecked == true, lights = _lights.IsChecked == true;
        int lod = Math.Max(0, _lod.SelectedIndex);
        try
        {
            bool dx = _dx.IsChecked == true && _dxView != null;
            var (group, bounds, model, slots, notes, partsOut, halosOut, markerR) = await Task.Run(() =>
            {
                var slots = new List<(int Slot, double[] M)>();
                var lightPts = new List<BmlScene.LightPoint>();
                var lightParts = new List<(int Node, string Material, int FirstIndex, int IndexCount)>();
                var model = scene.Pose(n => dofs.TryGetValue(n, out var v) ? v : 0, (n, b, on) => sw.TryGetValue((n, b), out var s) ? s : on, slots, lightParts, lights, lightPts);
                var halos = new List<(double, double, double, Color)>();
                if (lights)
                {
                    var tc = ModelChecks.LightColors(model, lightParts);
                    // solo le luci vere (dentro uno switch di luci): i bagliori senza switch hanno gia' la loro geometria
                    foreach (var p in lightPts.Where(p => p.Switch >= 0))
                    {
                        var (cr, cg, cb) = tc.TryGetValue(p.Node, out var c) ? c : p.Rgb;
                        if (Math.Max((int)cr, Math.Max((int)cg, (int)cb)) < 25) (cr, cg, cb) = ((byte)255, (byte)255, (byte)255);
                        double k = 255.0 / Math.Max(1, Math.Max((int)cr, Math.Max((int)cg, (int)cb)));     // colore pieno, la luminosita' la da' l'emissivo
                        halos.Add((p.X, p.Y, p.Z, Color.FromRgb((byte)Math.Min(255, cr * k), (byte)Math.Min(255, cg * k), (byte)Math.Min(255, cb * k))));
                    }
                }
                var parts = new List<SceneBuilder.Part> { new(model) };
                var notes = new List<string>();
                foreach (var (slot, M) in slots)
                    if (mounted.TryGetValue(slot, out var w))
                    {
                        var wm = Weapon(w);
                        if (wm != null) parts.Add(new SceneBuilder.Part(wm, M, true)); else notes.Add($"{w.Name}: no model");
                    }
                foreach (var s in mounted.Keys.Where(k => slots.All(x => x.Slot != k))) notes.Add($"slot {s} is hidden by a switch");
                double r = Math.Max(0.15, Math.Sqrt(model.Meshes.SelectMany(m => m.Positions).Select(v => (double)v * v).DefaultIfEmpty(1).Max()) * 0.012);
                var (g, b) = SceneBuilder.Build(parts, markers ? slots : null, r, ground, halos);
                return (g, b, model, slots, notes, parts, halos, r);
            });
            if (no != _poseNo) return;
            _content.Content = group;
            if (dx && _dxView != null)
                try { await _dxView.ShowAsync(partsOut, scene.Pbr, markers ? slots : null, markerR, ground, halosOut); }
                catch (Exception e) { notes.Add("DirectX view: " + e.Message); }
            if (no != _poseNo) return;
            if (resetView || _bounds.IsEmpty)
            {
                _bounds = bounds;
                _radius = Math.Max(1, Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ) / 2);
                ResetView();
            }
            int noTex = model.Meshes.Count(m => m.Texture == null && !m.Glass);
            _info.Text = $"{_title}  ·  model {Path.GetFileName(dir)} ({(scene.Version == 1 ? "classic textures" : "PBR")})  ·  LOD {lod}: {model.Triangles:N0} triangles  ·  " +
                         $"{bounds.SizeZ:0.#} × {bounds.SizeX:0.#} × {bounds.SizeY:0.#} ft (length × width × height)  ·  {scene.Dofs.Count} DOFs, {scene.Switches.Count} switches, {scene.Slots.Count} slots" +
                         (noTex > 0 ? $"  ·  {noTex} part(s) without texture (grey)" : "") +
                         (notes.Count + scene.Notes.Count > 0 ? "  ·  " + string.Join("; ", scene.Notes.Concat(notes)) : "");
        }
        catch (Exception e) { if (no == _poseNo) _info.Text = $"{_title}: {e.Message}"; }
    }

    private BmlModel? Weapon(ModelEntry w)
    {
        if (w.ModelDir == null) return null;
        lock (_weaponCache)
            if (_weaponCache.TryGetValue(w.ModelDir, out var c)) return c;
        BmlModel? m = null;
        try
        {
            var p = BmlModel.ReadParent(w.ModelDir);
            if (p != null && p.Lods.Count > 0) m = BmlModel.Load(p, 0, 0, Path.GetDirectoryName(w.ModelDir)!, CampaignDb.ModelTextureDirs(w.ModelDir));
        }
        catch { m = null; }
        lock (_weaponCache) _weaponCache[w.ModelDir] = m;
        return m;
    }

    // ───────────────────────── DOF

    private void FillDofs()
    {
        _dofPanel.Children.Clear();
        if (_scene == null) return;
        if (_scene.Dofs.Count == 0) { _dofPanel.Children.Add(new TextBlock { Text = "This LOD has no DOFs.", Foreground = Brushes.Gray }); return; }
        foreach (var d in _scene.Dofs)
        {
            var (mn, mx) = _scene.InputRange(d.Number);
            bool rot = d.Kind == BmlScene.DofKind.Rotate;
            int nodes = _scene.DofNodes.Count(x => x.Number == d.Number);
            var name = new TextBlock { Text = $"{d.Number}  {Names.Dof(d.Number)}", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var kind = $"{d.Kind.ToString().ToLowerInvariant()}" + (d.Limits ? $", limits {Fmt(Math.Min(d.Min, d.Max), rot)}…{Fmt(Math.Max(d.Min, d.Max), rot)}" : "") + (d.Normalise ? $", turns {d.Multiplier:0.###}° over that range" : d.Multiplier != 1 ? $", ×{d.Multiplier:0.###}" : "") +
                       (d.Reverse ? ", reverse" : "") + (d.Kind == BmlScene.DofKind.Rotate && !d.ValidMatrix ? ", INVALID MATRIX" : "") + (nodes > 1 ? $", {nodes} parts (each with its own multiplier and limits)" : "");
            double cur = _dofValues.TryGetValue(d.Number, out var v) ? v : 0;
            var slider = new Slider { Minimum = mn, Maximum = mx, Value = Math.Clamp(cur, mn, mx), Width = 230, SmallChange = (mx - mn) / 100, LargeChange = (mx - mn) / 10, IsMoveToPointEnabled = true };
            if (slider.Maximum <= slider.Minimum) slider.Maximum = slider.Minimum + 1;
            var val = new TextBlock { Width = 95, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Text = Show(d, cur), ToolTip = "Value BMS gives to this DOF → result on the first part" };
            int num = d.Number;
            slider.ValueChanged += (_, e) =>
            {
                _dofValues[num] = e.NewValue;
                val.Text = Show(d, e.NewValue);
                // lo stesso DOF puo' comparire piu' volte: tutti i cursori insieme
                foreach (var s in _dofPanel.Children.OfType<StackPanel>().Where(p => p.Tag is int t && t == num).SelectMany(p => p.Children.OfType<StackPanel>()).SelectMany(p => p.Children.OfType<Slider>()))
                    if (!ReferenceEquals(s, slider) && Math.Abs(s.Value - e.NewValue) > 1e-9) s.Value = e.NewValue;
                Repose();
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(slider); row.Children.Add(val);
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Tag = d.Number, ToolTip = "Double-click the name to put it back to rest" };
            name.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) slider.Value = 0; };
            box.Children.Add(name);
            box.Children.Add(new TextBlock { Text = kind, Foreground = Brushes.Gray, FontSize = 11 });
            box.Children.Add(row);
            _dofPanel.Children.Add(box);
        }
        FilterDofs();
    }

    /// <summary>Valore dato al DOF e, per il primo pezzo, il risultato (gradi per le rotazioni).</summary>
    private static string Show(BmlScene.DofInfo d, double input)
    {
        double eff = BmlScene.Effective(d, input);
        string raw = input.ToString("0.###", CultureInfo.InvariantCulture);
        return d.Kind == BmlScene.DofKind.Rotate ? $"{raw} → {eff * 180 / Math.PI:0.#}°" : $"{raw} → {eff.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    private static string Fmt(double v, bool rot) => rot ? $"{v * 180 / Math.PI:0.#}°" : v.ToString("0.###", CultureInfo.InvariantCulture);

    private void FilterDofs()
    {
        string q = _dofFilter.Text.Trim();
        foreach (var p in _dofPanel.Children.OfType<StackPanel>())
            p.Visibility = q.Length == 0 || (p.Children[0] is TextBlock t && t.Text.Contains(q, StringComparison.OrdinalIgnoreCase)) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Carrello: in BMS i pezzi del carrello stanno dentro switch che il simulatore accende solo con il carrello fuori
    /// (1-3 Nose/Left/Right Gear nei modelli vecchi, 20-22 nei modelli PBR, che switch.xml chiama "Gear Broken") e i DOF
    /// 19-24 lo fanno ruotare: a riposo (0) e' retratto, al limite e' giu'.
    /// </summary>

    private void Gear(bool down)
    {
        if (_scene == null) return;
        // switch del carrello: quelli che nel modello contengono le gambe (DOF 19-21), piu' i numeri soliti
        var sw = _scene.GearSwitches();
        foreach (var s in _scene.Switches.Where(s => sw.Contains((s.Number, s.Branch))))
            if (down) _switchOn[(s.Number, s.Branch)] = true; else _switchOn.Remove((s.Number, s.Branch));   // su: come nel file
        FillSwitches();
        var vals = _scene.GearDownValues();
        foreach (var (n, v) in vals) _dofValues[n] = down ? v : 0;
        FillDofs(); Repose();
    }

    private void SetGroup(int[] nums, bool max)
    {
        if (_scene == null) return;
        foreach (var d in _scene.Dofs.Where(d => nums.Contains(d.Number)))
        {
            var (mn, mx) = _scene.InputRange(d.Number);
            double target = max ? (Math.Abs(mx) >= Math.Abs(mn) ? mx : mn) : 0;
            _dofValues[d.Number] = target;
        }
        FillDofs(); Repose();
    }

    // ───────────────────────── switch

    private void FillSwitches()
    {
        _swPanel.Children.Clear();
        if (_scene == null) return;
        if (_scene.Switches.Count == 0) { _swPanel.Children.Add(new TextBlock { Text = "This LOD has no switches.", Foreground = Brushes.Gray }); return; }
        foreach (var s in _scene.Switches)
        {
            bool sim = _scene.DefaultOn.Contains((s.Number, s.Branch));
            bool on = _switchOn.TryGetValue((s.Number, s.Branch), out var v) ? v : s.StartsOn || sim;
            var cb = new CheckBox
            {
                Content = $"{s.Number}/{s.Branch}  {Names.Switch(s.Number, s.Branch)}" + (s.StartsOn ? "  (on at start)" : sim ? "  (set by BMS in flight)" : ""), IsChecked = on, Margin = new Thickness(0, 0, 0, 3),
                ToolTip = sim ? "Off in the file, but BMS turns one branch on (for example the aircraft number on the tail): shown on by default" : null,
            };
            var key = (s.Number, s.Branch);
            cb.Click += (_, _) => { _switchOn[key] = cb.IsChecked == true; Repose(); };
            _swPanel.Children.Add(cb);
        }
    }

    private void SetLights(bool on)
    {
        if (_scene == null) return;
        foreach (var s in _scene.Switches.Where(s => BmlScene.LightSwitches.Contains(s.Number)))
            if (on) _switchOn[(s.Number, s.Branch)] = true; else _switchOn.Remove((s.Number, s.Branch));
        if (on) _lights.IsChecked = true;
        FillSwitches(); Repose();
    }

    private void SetAllSwitches(bool on)
    {
        if (_scene == null) return;
        foreach (var s in _scene.Switches) _switchOn[(s.Number, s.Branch)] = on;
        FillSwitches(); Repose();
    }

    // ───────────────────────── slot

    private void FillSlots()
    {
        _slotPanel.Children.Clear();
        if (_scene == null) return;
        var nums = _scene.Slots.Select(s => s.Number).Distinct().OrderBy(n => n).ToList();
        if (nums.Count == 0) { _slotPanel.Children.Add(new TextBlock { Text = "This LOD has no slots.", Foreground = Brushes.Gray }); return; }
        _slotPanel.Children.Add(new TextBlock
        {
            Text = "Choose a weapon for a slot to see it mounted (type to search). Slot 0 is the left-most station in BMS order.", TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 0, 0, 6),
        });
        var items = new List<Pick> { new("(empty)", null) };
        items.AddRange(Weapons.Where(w => w.ModelDir != null).Select(w => new Pick($"{w.Name}  (model {w.ModelId})", w)));
        foreach (int n in nums)
        {
            var cb = new ComboBox { Width = 250, IsEditable = true, IsTextSearchEnabled = true, ItemsSource = items };
            cb.SelectedItem = _mounted.TryGetValue(n, out var w) ? items.FirstOrDefault(i => i.Weapon == w) ?? items[0] : items[0];
            int slot = n;
            cb.SelectionChanged += (_, _) =>
            {
                if (cb.SelectedItem is Pick { Weapon: { } m }) _mounted[slot] = m; else if (cb.SelectedItem is Pick) _mounted.Remove(slot); else return;
                Repose();
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(new TextBlock { Text = $"Slot {n}", Width = 55, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(cb);
            _slotPanel.Children.Add(row);
        }
        var copy = Btn("Same weapon on all slots", () =>
        {
            var first = _mounted.Values.FirstOrDefault();
            if (first == null) return;
            foreach (int n in nums) _mounted[n] = first;
            FillSlots(); Repose();
        }, "Copies the first chosen weapon to every slot");
        copy.HorizontalAlignment = HorizontalAlignment.Left;
        _slotPanel.Children.Add(copy);
    }

    private sealed record Pick(string Text, ModelEntry? Weapon) { public override string ToString() => Text; }

    // ───────────────────────── camera e foto

    private void HookMouse(Border surface)
    {
        surface.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;   // anticipo (Preview): la vista DirectX non deve muovere la sua camera da sola
            if (e.ClickCount == 2) { ResetView(); return; }
            _drag = e.GetPosition(surface); _panning = false; surface.CaptureMouse();
        };
        surface.PreviewMouseRightButtonDown += (_, e) => { e.Handled = true; _drag = e.GetPosition(surface); _panning = true; surface.CaptureMouse(); };
        surface.PreviewMouseLeftButtonUp += (_, _) => { _drag = null; surface.ReleaseMouseCapture(); };
        surface.PreviewMouseRightButtonUp += (_, _) => { _drag = null; surface.ReleaseMouseCapture(); };
        surface.PreviewMouseMove += (_, e) =>
        {
            if (_drag is not { } p0) return;
            var p = e.GetPosition(surface);
            double dx = p.X - p0.X, dy = p.Y - p0.Y;
            _drag = p;
            if (_panning)
            {
                var look = _cam.LookDirection; look.Normalize();
                var right = Vector3D.CrossProduct(look, _cam.UpDirection); right.Normalize();
                var up = Vector3D.CrossProduct(right, look);
                double k = _dist * 0.0016;
                _target += -right * dx * k + up * dy * k;
            }
            else
            {
                _yaw -= dx * 0.01;
                _pitch = Math.IEEERemainder(_pitch + dy * 0.01, 2 * Math.PI);
            }
            UpdateCamera();
        };
        surface.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            _dist = Math.Clamp(_dist * (e.Delta > 0 ? 0.88 : 1.14), _radius * 0.1, _radius * 30);
            UpdateCamera();
        };
    }

    private void SetView(double yaw, double pitch) { _yaw = yaw; _pitch = pitch; _dist = _radius * 2.6; Center(); UpdateCamera(); }

    private void ResetView() => SetView(-2.3, 0.32);

    private void Center()
    {
        var b = _bounds;
        if (!b.IsEmpty) _target = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
    }

    private void UpdateCamera()
    {
        var off = new Vector3D(Math.Cos(_pitch) * Math.Sin(_yaw), Math.Sin(_pitch), Math.Cos(_pitch) * Math.Cos(_yaw)) * _dist;
        _cam.Position = _target + off;
        _cam.LookDirection = -off;
        _cam.UpDirection = new Vector3D(-Math.Sin(_pitch) * Math.Sin(_yaw), Math.Cos(_pitch), -Math.Sin(_pitch) * Math.Cos(_yaw));
        _cam.NearPlaneDistance = Math.Max(0.02, _dist / 300);
        _cam.FarPlaneDistance = _dist * 50;
        _dxView?.SetCamera(_cam.Position, _cam.LookDirection, _cam.UpDirection, _cam.NearPlaneDistance, _cam.FarPlaneDistance);
    }

    private void Screenshot()
    {
        if (_surface.ActualWidth < 2 || _content.Content == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG image|*.png", FileName = $"model_{Path.GetFileName(_dir ?? "view")}_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            InitialDirectory = Directory.CreateDirectory(Path.Combine(MainWindow.Root(), "Screenshots")).FullName,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            const double scale = 2;
            int w = (int)(_surface.ActualWidth * scale), h = (int)(_surface.ActualHeight * scale);
            var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(_surface);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(dlg.FileName)) enc.Save(fs);
            Status?.Invoke($"Screenshot saved: {dlg.FileName}");
        }
        catch (Exception e) { Status?.Invoke("Screenshot: " + e.Message); }
    }
}
