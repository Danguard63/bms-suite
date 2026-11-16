using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArtEditor;

/// <summary>
/// Sezione Sounds: i suoni dell'interfaccia (SoundRc.irc: SND_PUSH, SND_RADIO...) come li usa il teatro, con l'ascolto e quante volte
/// ogni suono e' scelto dai pulsanti. Si puo' sostituire un suono con un altro file .ogg per il teatro o rimettere quello della KTO.
/// </summary>
public sealed class SoundsPanel : DockPanel
{
    public sealed record SoundRow(string Name, string File, string Options, string State, bool Changed, int Uses);

    private ArtSet? _art;
    private UiArt? _ui;
    private readonly ListView _list = new();
    private readonly TextBox _filter = new() { Width = 200, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _play, _replace, _reset;
    private readonly HashSet<string> _changedNow = new(StringComparer.OrdinalIgnoreCase);
    private List<SoundRow> _rows = new();

    public SoundRow? Selected => _list.SelectedItem as SoundRow;
    public event Action<SoundRow?>? SelectionChanged;
    public event Action<string>? Changed;

    public SoundsPanel()
    {
        Button B(string t, string tip, Action a) { var b = new Button { Content = t, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) }; b.Click += (_, _) => a(); return b; }
        _play = B("▶ Listen", "Plays the sound", Play);
        _replace = B("Replace with an .ogg file…", "The new sound is used by this theater only: the KTO sounds are not changed", Replace);
        _reset = B("Put back the KTO sound", "The theater uses the KTO sound again", Reset);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 6) };
        bar.Children.Add(_play); bar.Children.Add(_replace); bar.Children.Add(_reset);
        bar.Children.Add(new TextBlock { Text = "   Find ", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_filter);
        SetDock(bar, Dock.Top); Children.Add(bar);

        var gv = new GridView();
        GridViewColumn C(string h, string p, double w) => new() { Header = h, DisplayMemberBinding = new System.Windows.Data.Binding(p), Width = w };
        gv.Columns.Add(C("Sound", nameof(SoundRow.Name), 210));
        gv.Columns.Add(C("Used by buttons", nameof(SoundRow.Uses), 100));
        gv.Columns.Add(C("From", nameof(SoundRow.State), 170));
        gv.Columns.Add(C("File (from Data)", nameof(SoundRow.File), 360));
        _list.View = gv;
        Children.Add(_list);
        _list.SelectionChanged += (_, _) => { UpdateButtons(); SelectionChanged?.Invoke(Selected); };
        _list.MouseDoubleClick += (_, _) => Play();
        _filter.TextChanged += (_, _) => Fill(Selected?.Name);
        UpdateButtons();
    }

    public void SetArt(ArtSet? art, UiArt? ui) { _art = art; _ui = ui; Reload(); }

    public void Reload(string? select = null)
    {
        if (_art == null || _ui == null) { _rows.Clear(); _list.ItemsSource = null; return; }
        var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in _ui.AllWindows().SelectMany(w => w.Items))
            foreach (var a in it.All("SOUNDBITE"))
                if (a.Length > 0) uses[a[0]] = uses.TryGetValue(a[0], out int n) ? n + 1 : 1;
        var kto = ArtSet.ParseSounds(_art.SoundBaseLines()).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().File, StringComparer.OrdinalIgnoreCase);
        _rows = ArtSet.ParseSounds(_art.SoundLines()).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Select(x =>
        {
            bool same = kto.TryGetValue(x.Name, out var kf) && kf.Equals(x.File, StringComparison.OrdinalIgnoreCase);
            bool changed = _changedNow.Contains(x.Name);
            string state = changed ? "changed (not saved)" : same ? "KTO (Data\\Sounds)" : kto.ContainsKey(x.Name) ? "theater" : "theater (new)";
            return new SoundRow(x.Name, x.File, x.Opts, state, changed, uses.TryGetValue(x.Name, out int u) ? u : 0);
        }).OrderByDescending(r => r.Uses).ThenBy(r => r.Name).ToList();
        Fill(select ?? Selected?.Name);
    }

    private void Fill(string? select)
    {
        string f = _filter.Text.Trim();
        var rows = f.Length == 0 ? _rows : _rows.Where(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || r.File.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(r => r.Name.Equals(select, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var r = Selected;
        _play.IsEnabled = r != null;
        _replace.IsEnabled = r != null && _art != null;
        _reset.IsEnabled = r != null && _art != null && !r.State.StartsWith("KTO") && !r.State.EndsWith("(new)");
    }

    private Window? OwnerWin => Window.GetWindow(this);

    private void Play()
    {
        if (_art == null || Selected is not { } r) return;
        var f = _art.SoundFile(r.File);
        if (f == null) { MessageBox.Show(OwnerWin!, $"The file {r.File} was not found (it should be in the Data folder of BMS).", "Sounds"); return; }
        try { Sound.Play(f); }
        catch (Exception ex) { MessageBox.Show(OwnerWin!, "Cannot play the sound: " + ex.Message, "Sounds"); }
    }

    private void Replace()
    {
        if (_art == null || Selected is not { } r) return;
        var d = new Microsoft.Win32.OpenFileDialog { Title = $"New sound for {r.Name} (.ogg)", Filter = "Ogg Vorbis sound (*.ogg)|*.ogg" };
        if (d.ShowDialog(OwnerWin) != true) return;
        string info;
        try
        {
            var (sec, ch, rate) = Sound.Info(d.FileName);
            info = $"{sec:0.0} s, {(ch == 1 ? "mono" : ch == 2 ? "stereo" : ch + " channels")}, {rate} Hz";
            if (sec > 5 && r.Uses > 0 && MessageBox.Show(OwnerWin!, $"The sound lasts {sec:0.0} seconds: button sounds are usually short clicks (well under a second). Use it anyway?",
                    "Sounds", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(OwnerWin!, "This is not an Ogg Vorbis sound BMS can play (" + ex.Message + ").\n\nBMS uses .ogg (Vorbis) files: a .wav or .mp3 can be converted for example with Audacity (free): File → Export → Export as OGG.", "Sounds");
            return;
        }
        string path = _art.SetSound(r.Name, d.FileName);
        _changedNow.Add(r.Name);
        Reload(r.Name);
        string where = _art.WorkRelToData() == null
            ? "\n\nNote: this art folder is not inside the Data folder of BMS, so the path written for BMS is a guess; 'Copy into a theater' fixes it for the theater you copy to."
            : "";
        Changed?.Invoke($"{r.Name}: new sound {Path.GetFileName(d.FileName)} ({info}) → {path}. Save to write it.{where}");
    }

    private void Reset()
    {
        if (_art == null || Selected is not { } r) return;
        _art.ResetSound(r.Name);
        _changedNow.Add(r.Name);
        Reload(r.Name);
        Changed?.Invoke($"{r.Name}: the KTO sound will be used again. Save to write it.");
    }

    public void Saved() { _changedNow.Clear(); Reload(); }
}
