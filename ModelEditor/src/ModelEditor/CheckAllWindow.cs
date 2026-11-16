using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TerrainTool.Core.Models;

namespace ModelEditor;

/// <summary>Controlla tutti i modelli di Terrdata\objects\Models e mostra i problemi (doppio clic = apre il modello).</summary>
public sealed class CheckAllWindow : Window
{
    private readonly string _modelsRoot;
    private readonly Func<int, string> _nameOf;
    private readonly Action<int> _open;
    private readonly ListView _list = new();
    private readonly ProgressBar _bar = new() { Height = 14, Margin = new Thickness(6, 0, 6, 2) };
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 0, 6, 6) };
    private readonly CheckBox _deep = new() { Content = "Read every LOD (slower, finds broken files)", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _gear = new() { Content = "Test the landing gear (Gear down on every model)", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        ToolTip = "Puts the gear down like the 3D view and reports gear that does not come out, wheels that stay inside, left/right different or parts sticking out" };
    private readonly CheckBox _lightsTest = new() { Content = "Test the lights", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        ToolTip = "Light switches without lights, lights floating away from the aircraft, wing-tip nav lights without a twin on the other side, red/green swapped" };
    public BmsNames? Names { get; init; }
    private readonly ComboBox _sev = new() { Width = 130, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBox _search = new() { Width = 180, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _run = new() { Content = "Run", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
    private List<Row> _rows = new();
    private CancellationTokenSource? _cts;

    public sealed record Row(string Model, string Name, string Severity, string Problem);

    public CheckAllWindow(string modelsRoot, Func<int, string> nameOf, Action<int> open)
    {
        _modelsRoot = modelsRoot; _nameOf = nameOf; _open = open;
        Title = "Check all models — " + modelsRoot;
        Width = 1250; Height = 750; MinWidth = 800; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var s in new[] { "All", "Errors", "Errors + warnings", "Landing gear only", "Lights only" }) _sev.Items.Add(s);
        _sev.SelectedIndex = 2;
        _sev.SelectionChanged += (_, _) => Fill();
        _search.TextChanged += (_, _) => Fill();
        _run.Click += async (_, _) => await RunAsync();

        var bar = new WrapPanel { Margin = new Thickness(6) };
        void L(string t) => bar.Children.Add(new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        bar.Children.Add(_deep); bar.Children.Add(_gear); bar.Children.Add(_lightsTest); bar.Children.Add(_run);
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 16, 0) };
        cancel.Click += (_, _) => _cts?.Cancel();
        bar.Children.Add(cancel);
        L("Show"); bar.Children.Add(_sev);
        L("Search"); bar.Children.Add(_search);
        var export = new Button { Content = "Export…", Padding = new Thickness(8, 2, 8, 2) };
        export.Click += (_, _) => Export();
        bar.Children.Add(export);

        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Model", Width = 70, DisplayMemberBinding = new Binding("Model") });
        gv.Columns.Add(new GridViewColumn { Header = "Used by", Width = 230, DisplayMemberBinding = new Binding("Name") });
        gv.Columns.Add(new GridViewColumn { Header = "", Width = 70, DisplayMemberBinding = new Binding("Severity") });
        gv.Columns.Add(new GridViewColumn { Header = "Problem", Width = 830, DisplayMemberBinding = new Binding("Problem") });
        _list.View = gv;
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is Row r && int.TryParse(r.Model, out int n)) _open(n); };

        var bottom = new StackPanel();
        bottom.Children.Add(_bar); bottom.Children.Add(_status);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;
        _status.Text = "Run checks every model folder (read only). Double-click a line to open that model.";
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task RunAsync()
    {
        if (!Directory.Exists(_modelsRoot)) { _status.Text = _modelsRoot + " not found"; return; }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _run.IsEnabled = false;
        bool deep = _deep.IsChecked == true, gear = _gear.IsChecked == true, lightsTest = _lightsTest.IsChecked == true;
        var prog = new Progress<(int Done, int Total)>(p => { _bar.Maximum = p.Total; _bar.Value = p.Done; _status.Text = $"{p.Done} / {p.Total} models…"; });
        var t0 = DateTime.Now;
        try
        {
            var l = await Task.Run(() => ModelChecks.CheckAll(_modelsRoot, deep, prog, cts.Token, gear, lightsTest, Names));
            _rows = l.Select(i => new Row(i.Model, int.TryParse(i.Model, out int n) ? _nameOf(n) : "", i.Severity, i.Text)).ToList();
            int models = l.Select(i => i.Model).Distinct().Count();
            _status.Text = $"Done in {(DateTime.Now - t0).TotalSeconds:0} s: {l.Count(i => i.Severity == ModelIssue.Error)} error(s), {l.Count(i => i.Severity == ModelIssue.Warning)} warning(s), " +
                           $"{l.Count(i => i.Severity == ModelIssue.Info)} note(s) in {models} model(s).";
            Fill();
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled."; }
        catch (Exception e) { _status.Text = "ERROR: " + e.Message; }
        finally { _run.IsEnabled = true; }
    }

    private IEnumerable<Row> Visible()
    {
        int s = _sev.SelectedIndex; string q = _search.Text.Trim();
        return _rows.Where(r => s == 3 ? r.Problem.StartsWith("Gear:", StringComparison.Ordinal) : s == 4 ? r.Problem.StartsWith("Lights:", StringComparison.Ordinal) : s == 0 || r.Severity == ModelIssue.Error || (s == 2 && r.Severity == ModelIssue.Warning))
                    .Where(r => q.Length == 0 || r.Model == q || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Problem.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void Fill() => _list.ItemsSource = Visible().ToList();

    private void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Text (tab separated)|*.txt", FileName = $"model_check_{DateTime.Now:yyyyMMdd_HHmm}.txt" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllLines(dlg.FileName, new[] { "Model\tUsed by\tSeverity\tProblem" }.Concat(Visible().Select(r => $"{r.Model}\t{r.Name}\t{r.Severity}\t{r.Problem}")));
        _status.Text = "Saved " + dlg.FileName;
    }
}
