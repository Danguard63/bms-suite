using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Dds;
using TerrainTool.Core.Editor;

namespace ObjectiveStudio;

/// <summary>
/// Rete degli obiettivi sulla mappa dell'Objective Studio (con "Links" acceso): link troppo lunghi in evidenza, e con il tasto destro
/// "Add intersection here" (spezza il link con un incrocio nuovo), "Remove link", "Link from this objective…" (poi clic su un
/// altro obiettivo: aggiunge il link, o lo toglie se c'e' gia'), "Remove new objective". Le modifiche vanno subito in
/// network_edits.json del progetto; "Apply object changes" del Campaign Editor le scrive nei file della campagna.
/// </summary>
public sealed partial class EditorMap
{
    public const double MaxLinkKm = 50;           // il piu' lungo in KTO; verso 59 km il costo arriva a 255 e i cingolati non passano
    private const double FtPerKm = 3280.84;
    private NetworkEdits? _net;
    public NetworkEdits Net => _net ??= NetworkEdits.Load(NetworkEdits.PathFor(_d.Project));
    private readonly Stack<string> _netUndo = new();
    private int _linkFrom;                        // CampId da cui parte un link nuovo (0 = nessuno)
    private Point? _rightDown;
    public event Action? NetworkChanged;

    private static readonly Pen LinkLongPen = Fz(new Pen(new SolidColorBrush(Color.FromRgb(255, 0, 210)), 2.6));
    private static readonly Pen LinkNewPen = Fz(new Pen(new SolidColorBrush(Color.FromRgb(60, 255, 120)), 2));
    private static readonly Pen LinkNewAirPen = Fz(new Pen(new SolidColorBrush(Color.FromRgb(60, 255, 120)), 2) { DashStyle = DashStyles.Dash });
    private static readonly Brush NewFill = Fz(new SolidColorBrush(Color.FromRgb(60, 255, 120)));

    /// <summary>Posizione (piedi, x nord / y est) e nome di ogni nodo: obiettivi esistenti e nuovi.</summary>
    private Dictionary<int, (double X, double Y, string Name)> Nodes()
    {
        var n = new Dictionary<int, (double, double, string)>();
        foreach (var o in _d.Objectives) n.TryAdd(o.Id, (o.X, o.Y, o.Name));
        foreach (var a in Net.Added) n[a.CampId] = (a.NorthFt, a.EastFt, a.Name);
        return n;
    }

    /// <summary>I link come saranno dopo le modifiche: quelli della campagna, meno i tolti e gli spezzati, piu' gli aggiunti.</summary>
    private List<(int A, int B, bool Ground, bool OneWay, bool Edited)> EffectiveLinks()
    {
        var res = new Dictionary<(int, int), (bool G, bool O, bool E)>();
        static (int, int) K(int a, int b) => a < b ? (a, b) : (b, a);
        if (Tree != null) foreach (var (a, b, g, o) in Tree.Links) res[K(a, b)] = (g, o, false);
        foreach (var x in Net.Added)
            if (x.SplitA != 0 && x.SplitB != 0)
            {
                bool g = !res.TryGetValue(K(x.SplitA, x.SplitB), out var was) || was.G;   // i pezzi restano del tipo del link spezzato
                res.Remove(K(x.SplitA, x.SplitB));
                res[K(x.SplitA, x.CampId)] = (g, false, true);
                res[K(x.CampId, x.SplitB)] = (g, false, true);
            }
        foreach (var l in Net.Links)
            if (l.Add) res[K(l.A, l.B)] = (true, false, true); else res.Remove(K(l.A, l.B));
        return res.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value.G, kv.Value.O, kv.Value.E)).ToList();
    }

    private static double Km((double X, double Y, string) a, (double X, double Y, string) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) / FtPerKm;

    /// <summary>Disegna la rete: grigio terra, blu tratteggiato aereo, arancio senso unico, magenta = piu' lungo di 50 km, verde = modificato.</summary>
    private void DrawNetwork(DrawingContext dc, double W, double H, double dpi)
    {
        var nodes = Nodes();
        var view = new Rect(-50, -50, W + 100, H + 100);
        int sel = Selected?.Id ?? int.MinValue;
        var mine = new List<(Point, Point)>(); var blocked = new List<(Point, Point)>(); var longs = new List<(Point, Point, double)>();
        foreach (var (a, b, ground, oneWay, edited) in EffectiveLinks())
        {
            if (!nodes.TryGetValue(a, out var na) || !nodes.TryGetValue(b, out var nb)) continue;
            var pa = ToScreen(na.X, na.Y); var pb = ToScreen(nb.X, nb.Y);
            if (!view.IntersectsWith(new Rect(pa, pb))) continue;
            double km = Km(na, nb);
            if (Tree != null && Tree.Blocked.Contains(a < b ? (a, b) : (b, a))) blocked.Add((pa, pb));
            if (ground && km > MaxLinkKm) longs.Add((pa, pb, km));
            if (a == sel || b == sel || a == _linkFrom || b == _linkFrom) { mine.Add((pa, pb)); continue; }
            dc.DrawLine(edited ? (ground ? LinkNewPen : LinkNewAirPen) : oneWay ? LinkOneWayPen : ground ? LinkPen : LinkAirPen, pa, pb);
        }
        foreach (var (pa, pb, km) in longs)
        {
            dc.DrawLine(LinkLongPen, pa, pb);
            var m = new Point((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
            var ft = new FormattedText($"{km:0} km", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, dpi);
            dc.DrawRectangle(TextBg, null, new Rect(m.X - 2, m.Y - 1, ft.Width + 4, ft.Height + 2));
            dc.DrawText(ft, m);
        }
        foreach (var (pa, pb) in mine) dc.DrawLine(LinkSelPen, pa, pb);
        foreach (var (pa, pb) in blocked) dc.DrawLine(LinkBlockedPen, pa, pb);
        // obiettivi nuovi: icona dell'incrocio in verde
        foreach (var x in Net.Added)
        {
            var c = ToScreen(x.NorthFt, x.EastFt);
            if (!view.Contains(c)) continue;
            ObjectiveIcons.Draw(dc, 15, c, 14, NewFill);
            if (_labels)
            {
                var ft = new FormattedText(x.Name + " (new)", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, NewFill, dpi);
                dc.DrawRectangle(TextBg, null, new Rect(c.X + 8, c.Y + 4, ft.Width + 4, ft.Height + 2));
                dc.DrawText(ft, new Point(c.X + 10, c.Y + 5));
            }
        }
        if (_linkFrom != 0 && nodes.TryGetValue(_linkFrom, out var lf) && _mouseNet is Point mp)
            dc.DrawLine(new Pen(Brushes.Yellow, 1.5) { DashStyle = DashStyles.Dash }, ToScreen(lf.X, lf.Y), mp);
    }
    private Point? _mouseNet;

    /// <summary>Link piu' vicino al punto (entro 7 pixel), o null.</summary>
    private (int A, int B, double Km)? LinkAt(Point p)
    {
        var nodes = Nodes();
        (int, int, double)? best = null; double bd = 7;
        foreach (var (a, b, _, _, _) in EffectiveLinks())
        {
            if (!nodes.TryGetValue(a, out var na) || !nodes.TryGetValue(b, out var nb)) continue;
            var pa = ToScreen(na.X, na.Y); var pb = ToScreen(nb.X, nb.Y);
            var ab = pb - pa; double len2 = ab.X * ab.X + ab.Y * ab.Y;
            double t = len2 < 1 ? 0 : Math.Clamp(((p.X - pa.X) * ab.X + (p.Y - pa.Y) * ab.Y) / len2, 0, 1);
            var q = pa + t * ab;
            double d = (p - q).Length;
            if (d < bd) { bd = d; best = (a, b, Km(na, nb)); }
        }
        return best;
    }

    /// <summary>Obiettivo nuovo sotto il mouse (entro 10 pixel): il suo CampId, o 0.</summary>
    private int NewAt(Point p)
    {
        foreach (var x in Net.Added)
            if ((ToScreen(x.NorthFt, x.EastFt) - p).Length < 10) return x.CampId;
        return 0;
    }

    private void NetEdit(string what, Action change)
    {
        _netUndo.Push(JsonSerializer.Serialize(Net));
        change();
        Net.Save(NetworkEdits.PathFor(_d.Project));
        HoverText?.Invoke($"{what} — network_edits.json: {Net.Added.Count} new objectives, {Net.Links.Count} link changes. Write them into the campaign with 'Apply object changes' in the Campaign Editor.");
        NetworkChanged?.Invoke();
        InvalidateVisual();
    }

    public void UndoNetwork()
    {
        if (_netUndo.Count == 0) return;
        _net = JsonSerializer.Deserialize<NetworkEdits>(_netUndo.Pop()) ?? new();
        _net.Save(NetworkEdits.PathFor(_d.Project));
        HoverText?.Invoke("Network edit undone");
        NetworkChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Menu del tasto destro (clic senza trascinare) sulla mappa con i link accesi.</summary>
    private void ShowNetworkMenu(Point p)
    {
        if (!_links || Tree == null) return;
        // installed theater: the road network is campaign data, edited in the Campaign Editor (here only shown)
        if (_d.Project.IsInstalledTheater) { HoverText?.Invoke("Links are shown only: add or remove them in the Campaign Editor"); return; }
        var m = new ContextMenu();
        void Item(string h, Action a, bool bold = false)
        {
            var mi = new MenuItem { Header = h, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
        }
        var nodes = Nodes();
        int newId = NewAt(p);
        var obj = newId == 0 ? PickAt(p) : null;
        int nodeId = newId != 0 ? newId : obj?.Id ?? 0;
        if (nodeId != 0)
        {
            string nm = nodes.TryGetValue(nodeId, out var nn) ? nn.Name : nodeId.ToString();
            Item($"Link from {nm}… (then click another objective: adds the link, or removes it if it is there)", () => { _linkFrom = nodeId; HoverText?.Invoke($"Click the objective to link with {nm} (Esc = cancel)"); InvalidateVisual(); }, true);
            if (newId != 0)
                Item($"Remove the new objective {nm}", () => NetEdit($"{nm} removed", () =>
                {
                    Net.Added.RemoveAll(x => x.CampId == newId);
                    Net.Links.RemoveAll(l => l.A == newId || l.B == newId);
                }));
        }
        else if (LinkAt(p) is var (a, b, km))
        {
            string na = nodes[a].Name, nb = nodes[b].Name;
            Item($"Add intersection here (splits {na} – {nb}, {km:0} km)", () => AddIntersection(a, b, ToWorld(p)), true);
            Item($"Remove link {na} – {nb}", () => NetEdit($"Link {na} – {nb} removed", () => Net.SetLink(a, b, false)));
        }
        if (_netUndo.Count > 0) { if (m.Items.Count > 0) m.Items.Add(new Separator()); Item("Undo last network edit", UndoNetwork); }
        if (m.Items.Count == 0) return;
        m.PlacementTarget = this;
        m.IsOpen = true;
    }

    /// <summary>Incrocio nuovo nel punto cliccato: copia del record dell'incrocio esistente piu' vicino, numeri liberi in tutti i file della campagna.</summary>
    private void AddIntersection(int a, int b, (double X, double Y) at)
    {
        var tpl = _d.Objectives.Where(o => o.Type == 15).OrderBy(o => (o.X - at.X) * (o.X - at.X) + (o.Y - at.Y) * (o.Y - at.Y)).FirstOrDefault();
        if (tpl == null) { MessageBox.Show(Window.GetWindow(this)!, "No junction in this theater to copy.", "Add intersection"); return; }
        var near = _d.Objectives.Where(o => o.Type is 8 or 28 or 29).OrderBy(o => (o.X - at.X) * (o.X - at.X) + (o.Y - at.Y) * (o.Y - at.Y)).FirstOrDefault();
        string name = Prompt("Add intersection", "Name of the new intersection:", near != null ? $"{near.Name} Junction" : "Junction");
        if (string.IsNullOrWhiteSpace(name)) return;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var (files, db) = CampaignSources();
            var (campId, num) = Net.NextIds(files, db.ClassOf);
            NetEdit($"Intersection '{name}' added (CampId {campId}, id {num}), link {a}–{b} split", () =>
                Net.Added.Add(new AddedObjective(campId, num, name.Trim(), tpl.Id, at.X, at.Y, a, b)));
        }
        finally { Mouse.OverrideCursor = null; }
    }

    /// <summary>Tutti i file della campagna del teatro (per i numeri liberi) e il database per leggerne le unita'.</summary>
    private (List<string> Files, CampaignDb Db) CampaignSources()
    {
        var p = _d.Project;
        string addOn = new System.IO.DirectoryInfo(p.OldTheaterFolder).Name;
        string? data = CampaignWorkspace.BmsDataDir(p.KoreaNewTerrain);
        var dirs = new[]
        {
            System.IO.Path.Combine(p.ProjectFolder, "CampaignEdits"),
            System.IO.Path.Combine(p.ProjectFolder, "Output", "Install", addOn, "Campaign"),
            data != null ? System.IO.Path.Combine(data, addOn, "Campaign") : null,
            System.IO.Path.Combine(p.OldTheaterFolder, "Campaign"),
        }.Where(d => d != null && System.IO.Directory.Exists(d)).Select(d => d!);
        var files = dirs.SelectMany(d => System.IO.Directory.EnumerateFiles(d))
            .Where(f => f.EndsWith(".cam", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tac", StringComparison.OrdinalIgnoreCase)).ToList();
        var dbDirs = new[]
        {
            System.IO.Path.Combine(p.ProjectFolder, "Output", "Install", addOn, "Terrdata", ObjectsJob.FolderName),
            data != null ? System.IO.Path.Combine(data, addOn, "Terrdata", ObjectsJob.FolderName) : null,
        }.Where(d => d != null && System.IO.Directory.Exists(d)).Select(d => d!);
        return (files, CampaignDb.Load(dbDirs));
    }

    /// <summary>Clic sinistro con un link in partenza: sull'altro obiettivo aggiunge il link, o lo toglie se c'e' gia'. True se gestito.</summary>
    private bool LinkClick(Point p)
    {
        if (_linkFrom == 0) return false;
        int to = NewAt(p);
        if (to == 0) to = PickAt(p)?.Id ?? 0;
        int from = _linkFrom;
        _linkFrom = 0;
        if (to == 0 || to == from) { HoverText?.Invoke("Link cancelled"); InvalidateVisual(); return true; }
        var nodes = Nodes();
        bool exists = EffectiveLinks().Any(l => l.A == Math.Min(from, to) && l.B == Math.Max(from, to));
        string na = nodes.GetValueOrDefault(from).Name, nb = nodes.GetValueOrDefault(to).Name;
        double km = Km(nodes[from], nodes[to]);
        NetEdit(exists ? $"Link {na} – {nb} removed" : $"Link {na} – {nb} added ({km:0} km{(km > MaxLinkKm ? ", longer than 50 km!" : "")})",
            () => Net.SetLink(from, to, !exists));
        return true;
    }

    private static string Prompt(string title, string text, string value)
    {
        var tb = new TextBox { Text = value, Margin = new Thickness(0, 6, 0, 8), MinWidth = 320 };
        var w = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current.MainWindow };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Content = new StackPanel
        {
            Margin = new Thickness(12),
            Children = { new TextBlock { Text = text }, tb, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } },
        };
        w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return w.ShowDialog() == true ? tb.Text : "";
    }
}
