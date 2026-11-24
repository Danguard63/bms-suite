using System.ComponentModel;
using System.IO;
using System.Media;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Database;
using TerrainTool.Core.Sound;

namespace CampaignEditor;

/// <summary>
/// ATC airbase voices: what BMS's ATC says for every airbase of stations+ils.dat (the Tower / Ground / Approach fragments of
/// Falcon.tlk) and new names made with a Windows text-to-speech voice and a radio effect, for the two ATC voices (12 female,
/// 13 male). Work files: &lt;work folder&gt;\Sounds (Falcon.tlk, fragfile.xml, F4Talk.csv, TTSEval.dat) and stations+ils.dat
/// saved as the Station ILS window does. "Install into BMS" gives the theater its own Sounds folder (a copy of Data\Sounds the
/// first time, as the Israel theater ships it) and points its .tdf to it, with a backup.
/// </summary>
public sealed class AtcVoicesWindow : Window
{
    public sealed class Row : INotifyPropertyChanged
    {
        public Station S = null!;
        public int CampId => S.CampId;
        public string Objective { get; set; } = "";
        public string NowSays { get; set; } = "";
        private string _name = "", _say = "", _status = "";
        public string NewName { get => _name; set { _name = value; Changed(nameof(NewName)); } }
        public string SayAs { get => _say; set { _say = value; Changed(nameof(SayAs)); } }
        public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private readonly CampaignWorkspace _ws;
    private readonly string _workSounds, _stationsName;
    private readonly string? _stationsSrc;
    private readonly StationsFile _st;
    private AtcVoices? _v;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Extended, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly ComboBox _female = new() { Width = 220, Margin = new Thickness(4, 0, 12, 0) }, _male = new() { Width = 220, Margin = new Thickness(4, 0, 12, 0) };
    private readonly CheckBox _tts = new() { Content = "Also the TTS name (callsign)", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Also sets the callsign of the line to the new name in TTSEval.dat (line 24), as the Israel theater does: the name read by BMS's text-to-speech radio. Off by default: the callsign may be used elsewhere too." };
    private readonly TextBlock _status = new() { Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 14, Margin = new Thickness(4, 4, 4, 0), Visibility = Visibility.Collapsed };
    private readonly WrapPanel _act = new() { Margin = new Thickness(4) };
    private bool _busy;
    private List<Row> _rows = new();
    private bool _dirty;

    public AtcVoicesWindow(CampaignWorkspace ws, CampaignDoc? doc)
    {
        _ws = ws;
        Title = "ATC airbase voices – " + ws.Name;
        Width = 1250; Height = 820;
        _workSounds = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ws.EditsFolder))!, "Sounds");
        _stationsSrc = ws.SearchDirs().Select(d => AtcVoices.Find(d, "stations+ils.dat")).FirstOrDefault(f => f != null);
        _stationsName = _stationsSrc != null ? Path.GetFileName(_stationsSrc) : "stations+ils.dat";
        _st = _stationsSrc != null ? StationsFile.Load(_stationsSrc) : new StationsFile();
        var names = doc?.Objectives.GroupBy(o => (int)o.CampId).ToDictionary(g => g.Key, g => g.First().Name) ?? new Dictionary<int, string>();

        // voices of Windows (System.Speech)
        var voices = new List<InstalledVoice>();
        try { using var s = new SpeechSynthesizer(); voices = s.GetInstalledVoices().Where(v => v.Enabled).ToList(); } catch (Exception) { }
        foreach (var v in voices) { _female.Items.Add(v.VoiceInfo.Name); _male.Items.Add(v.VoiceInfo.Name); }
        _male.Items.Add("(the female voice, lower)");
        int Pick(VoiceGender g, ComboBox cb) => voices.FindIndex(v => v.VoiceInfo.Gender == g && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en") is int i and >= 0 ? i
            : voices.FindIndex(v => v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en") is int j and >= 0 && cb == _female ? j : -1;
        _female.SelectedIndex = Pick(VoiceGender.Female, _female) is int fi && fi >= 0 ? fi : (voices.Count > 0 ? 0 : -1);
        _male.SelectedIndex = Pick(VoiceGender.Male, _male) is int mi && mi >= 0 ? mi : _male.Items.Count - 1;

        void C(string h, string path, double w, bool ro = true)
            => _grid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, Mode = ro ? BindingMode.OneWay : BindingMode.TwoWay },
                Width = w == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w), IsReadOnly = ro });
        C("CampID", nameof(Row.CampId), 60); C("Objective", nameof(Row.Objective), 220); C("ATC says now (Tower)", nameof(Row.NowSays), 220);
        C("New name", nameof(Row.NewName), 190, false); C("Say it as (optional)", nameof(Row.SayAs), 170, false); C("Status", nameof(Row.Status), 0);
        _grid.CellEditEnding += (_, _) => _dirty = true;

        var bar = new WrapPanel { Margin = new Thickness(4) };
        bar.Children.Add(new TextBlock { Text = "Voice 12 (female):", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_female);
        bar.Children.Add(new TextBlock { Text = "Voice 13 (male):", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_male);
        bar.Children.Add(_tts);
        var act = _act;
        act.Children.Add(UnitText.Btn("▶ Now (12)", () => PlayNow(AtcVoices.FemaleVoice), "Plays what ATC says now for the selected airbase (Tower fragment, female voice)"));
        act.Children.Add(UnitText.Btn("▶ Now (13)", () => PlayNow(AtcVoices.MaleVoice), "The same with the male voice"));
        act.Children.Add(UnitText.Btn("▶ Preview new (12)", () => Preview(true), "Makes '<new name> Tower' with the female voice and the radio effect, through the Speex codec as in the game"));
        act.Children.Add(UnitText.Btn("▶ Preview new (13)", () => Preview(false), "The same with the male voice"));
        act.Children.Add(UnitText.Btn("Names from the objectives", FillNames, "Short ATC names from the objectives for the selected lines that have none (Ctrl+A selects all): codes in brackets and words like 'International Airport', 'Air Base', 'Airfield' at the end are left out"));
        var gen = UnitText.Btn("Make voices for selected", Generate, "For every selected line with a new name: the name, '<name> Approach' and '<name> Tower' in the two ATC voices, as new fragments; the line's Tower / Ground / Approach frags point to them");
        gen.FontWeight = FontWeights.SemiBold;
        act.Children.Add(gen);
        act.Children.Add(UnitText.Btn("Save", () => Save(), "Writes the four sound files into " + _workSounds + " and stations+ils.dat into the campaign work folder (nothing into BMS)"));
        bool canInstall = ws.BmsTheater != null && AtcVoiceInstaller.Refusal(ws.BmsTheater) == null;
        var inst = UnitText.Btn("Install into BMS…", Install, ws.BmsTheater == null ? "Only for a theater installed in BMS" : AtcVoiceInstaller.Refusal(ws.BmsTheater) ?? "Gives the theater its own Sounds folder (BMS's sounds copied the first time), writes the voice files and stations+ils.dat, sets sounddir in its .tdf; every replaced file is backed up");
        inst.IsEnabled = canInstall;
        act.Children.Add(inst);
        var res = UnitText.Btn("Restore…", Restore, "Puts back the theater as it was before the last 'Install into BMS'");
        res.IsEnabled = ws.BmsTheater != null;
        act.Children.Add(res);

        var top = new StackPanel();
        top.Children.Add(new TextBlock { Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap,
            Text = "BMS's ATC says an airbase's name with three fragments of its stations+ils.dat line (Tower, Ground = the name, Approach), recorded in Falcon.tlk " +
                   "for voice 12 (female) and 13 (male). Without its own Sounds folder a theater uses BMS's, which has only the Korea names. Write a new name " +
                   "(and how to say it, if the voice reads it wrong), listen, then 'Make voices for selected'. Windows voices: Settings → Time & language → Speech → Add voices." });
        top.Children.Add(bar);
        top.Children.Add(act);
        var dp = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); dp.Children.Add(top);
        var bottom = new StackPanel();
        bottom.Children.Add(_progress);
        bottom.Children.Add(_status);
        DockPanel.SetDock(bottom, Dock.Bottom); dp.Children.Add(bottom);
        dp.Children.Add(_grid);
        Content = dp;

        try
        {
            string src = AtcVoices.Find(_workSounds, AtcVoices.TlkName) != null ? _workSounds : SoundSource();
            _v = AtcVoices.Load(src);
            _status.Text = $"{_v.Tlk.Clips.Count} clips in {Path.Combine(src, AtcVoices.TlkName)}" + (src == _workSounds ? " (your work copy)" : "") +
                           $"; {_st.Stations.Count} lines in {_stationsSrc ?? "(no stations+ils.dat found)"}.";
        }
        catch (Exception ex) { _status.Text = "Voice files not read: " + ex.Message; }
        _rows = _st.Stations.Select(s => new Row
        {
            S = s,
            Objective = names.TryGetValue(s.CampId, out var n) ? n : s.Comment1.Trim().TrimStart('#').Trim(),
            NowSays = _v == null ? "" : s.TowerFrag <= 0 ? "(no name: fragment 0)"
                : _v.Clip(s.TowerFrag, AtcVoices.FemaleVoice) == null ? $"(fragment {s.TowerFrag} not in this theater's voices)" : _v.Text(s.TowerFrag),
        }).ToList();
        _grid.ItemsSource = _rows;
        Closing += (_, e) =>
        {
            if (_busy) { e.Cancel = true; _status.Text = "Still making voices: wait for the end."; return; }
            if (!_dirty) return;
            var r = MessageBox.Show(this, "Voices made but not saved. Save them now?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) e.Cancel = true;
            else if (r == MessageBoxResult.Yes && !Save()) e.Cancel = true;
        };
    }

    /// <summary>The theater's voice files as BMS reads them: its sounddir, else BMS's Data\Sounds.</summary>
    private string SoundSource()
    {
        if (_ws.BmsTheater is { } t) return t.SoundDir;
        string? data = _ws.ReferenceCampaignDir != null ? Path.GetDirectoryName(_ws.ReferenceCampaignDir) : null;
        return data != null ? Path.Combine(data, "Sounds") : throw new DirectoryNotFoundException("BMS Data folder not known");
    }

    private Row? Sel => _grid.SelectedItem as Row;

    // the player stays referenced while it plays (a SoundPlayer collected by the GC stops at once: no sound)
    private SoundPlayer? _player;

    private void Play(short[] pcm)
    {
        if (pcm.Length == 0) { _status.Text += "  (the clip is empty)"; return; }
        _player?.Stop();
        _player?.Dispose();
        _player = new SoundPlayer(new MemoryStream(RadioEffect.WriteWav(pcm)));
        _player.Load();
        _player.Play();
    }

    private void PlayNow(int voice)
    {
        if (Sel is not { } r || _v == null) { _status.Text = "Choose a line."; return; }
        var c = _v.Clip(r.S.TowerFrag, voice);
        if (c == null) { _status.Text = $"Fragment {r.S.TowerFrag} has no clip for voice {voice}."; return; }
        var pcm = SpeexClip.Decode(c);
        _status.Text = $"Fragment {r.S.TowerFrag}: \"{_v.Text(r.S.TowerFrag)}\" (voice {voice}), {pcm.Length / 8000.0:0.00} s.";
        Play(pcm);
    }

    /// <summary>A phrase in a Windows voice, 8000 Hz PCM, with the radio effect.</summary>
    private short[] Speak(string text, bool female) => Speak(text, female, _female.SelectedItem as string, _male.SelectedItem as string);

    /// <summary>A phrase in a Windows voice (names chosen on the window's thread), 8000 Hz PCM, with the radio effect. Safe on a worker thread.</summary>
    private static short[] Speak(string text, bool female, string? femaleVoice, string? maleVoice)
    {
        string? name = female ? femaleVoice : maleVoice;
        bool lower = !female && (name == null || name.StartsWith("("));
        if (lower) name = femaleVoice;
        using var s = new SpeechSynthesizer();
        if (name != null) s.SelectVoice(name);
        var ms = new MemoryStream();
        s.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(8000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        if (lower)
        {
            var pb = new PromptBuilder();
            pb.AppendSsmlMarkup($"<prosody pitch=\"x-low\">{System.Security.SecurityElement.Escape(text)}</prosody>");
            s.Speak(pb);
        }
        else s.Speak(text);
        var b = ms.ToArray();
        var pcm = new short[b.Length / 2];
        Buffer.BlockCopy(b, 0, pcm, 0, pcm.Length * 2);
        return RadioEffect.Apply(pcm);
    }

    private string Say(Row r) => (r.SayAs.Trim().Length > 0 ? r.SayAs : r.NewName).Trim();

    private void Preview(bool female)
    {
        if (Sel is not { } r || r.NewName.Trim().Length == 0) { _status.Text = "Choose a line with a new name."; return; }
        try
        {
            var pcm = SpeexClip.Decode(SpeexClip.Encode(Speak(Say(r) + " Tower", female)));
            _status.Text = $"\"{Say(r)} Tower\" ({(female ? "voice 12" : "voice 13")}): {pcm.Length / 8000.0:0.00} s.";
            Play(pcm);
        }
        catch (Exception ex) { _status.Text = "Voice not made: " + ex.Message; }
    }

    private void FillNames()
    {
        var sel = _grid.SelectedItems.OfType<Row>().ToList();
        if (sel.Count == 0) { _status.Text = "Choose the lines (Ctrl+A: all of them)."; return; }
        int n = 0;
        foreach (var r in sel.Where(r => r.NewName.Trim().Length == 0 && r.Objective.Length > 0)) { r.NewName = AtcName(r.Objective); n++; }
        _status.Text = $"{n} of {sel.Count} lines: short ATC names made from the objectives (\"Kuwait International Airport\" → \"Kuwait\"). Check them, then 'Make voices for selected'.";
    }

    // words that say what kind of airfield it is, not which one: ATC says "Kuwait Tower", not "Kuwait International Airport Tower"
    private static readonly string[] Generic =
    {
        "international airport", "intl airport", "regional airport", "domestic airport", "military airport", "airport",
        "naval air station", "naval air base", "naval base", "air station",
        "air base", "airbase", "air force base", "air field", "airfield", "air strip", "airstrip", "highway strip",
        "heliport", "helipad", "afb", "ab", "aerodrome", "field", "international", "intl", "intl.", "regional", "domestic", "military", "base",
    };

    /// <summary>A short name for the ATC: codes in brackets removed, then the generic words at the end ("Air Base", "International Airport"…).</summary>
    public static string AtcName(string objective)
    {
        string s = System.Text.RegularExpressions.Regex.Replace(objective, @"\s*[\(\[][^\)\]]*[\)\]]\s*", " ").Trim();
        bool cut = true;
        while (cut)
        {
            cut = false;
            foreach (var g in Generic)
                if (s.Length > g.Length + 1 && s.EndsWith(" " + g, StringComparison.OrdinalIgnoreCase))
                { s = s[..^(g.Length + 1)].TrimEnd(' ', '-', ',', '.'); cut = true; break; }
        }
        return s.Length > 0 ? s : objective.Trim();
    }

    private async void Generate()
    {
        if (_v == null || _busy) return;
        var sel = _grid.SelectedItems.OfType<Row>().Where(r => r.NewName.Trim().Length > 0).ToList();
        if (sel.Count == 0) { _status.Text = "Choose lines with a new name."; return; }
        if (_female.SelectedItem == null) { MessageBox.Show(this, "No Windows text-to-speech voice is installed.", Title); return; }
        string? fv = _female.SelectedItem as string, mv = _male.SelectedItem as string;
        bool tts = _tts.IsChecked == true;
        var v = _v;
        int made = 0, reused = 0, done = 0;
        _busy = true; _act.IsEnabled = false; _grid.IsReadOnly = true;
        _progress.Minimum = 0; _progress.Maximum = sel.Count; _progress.Value = 0; _progress.Visibility = Visibility.Visible;
        try
        {
            foreach (var r in sel)
            {
                string name = r.NewName.Trim(), say = Say(r);
                _status.Text = $"Making voices {done + 1} of {sel.Count}: {name} (name, Approach, Tower; voices 12 and 13)…";
                var (g, ap, tw, cs) = await Task.Run(() =>
                {
                    int Frag(string text, string spoken)
                    {
                        int f = v.FindFrag(text);
                        if (f >= 0) { reused++; return f; }
                        made++;
                        return v.AddFrag(text, Speak(spoken, true, fv, mv), Speak(spoken, false, fv, mv));
                    }
                    int g = Frag(name, say), ap = Frag(name + " Approach", say + " Approach"), tw = Frag(name + " Tower", say + " Tower");
                    int cs = tts && v.HasTts ? v.TtsIndex(name, true) : -1;
                    return (g, ap, tw, cs);
                });
                r.S.GroundFrag = g; r.S.ApproachFrag = ap; r.S.TowerFrag = tw;
                if (cs >= 0) { r.S.TcnCallsign = cs; r.S.AtisFrag = cs; }
                r.NowSays = name + " Tower";
                r.Status = $"frags {tw} / {g} / {ap}" + (cs >= 0 ? $", callsign {cs}" : "");
                _dirty = true;
                _progress.Value = ++done;
            }
            _status.Text = $"Done: {sel.Count} airbases, {made} new fragments ({made * 2} clips), {reused} reused. Save writes them; Install into BMS puts them into the theater.";
        }
        catch (Exception ex) { _status.Text = $"Stopped after {done} of {sel.Count}: " + ex.Message; }
        finally
        {
            _busy = false; _act.IsEnabled = true; _grid.IsReadOnly = false;
            _progress.Visibility = Visibility.Collapsed;
            _grid.Items.Refresh();
        }
    }

    private List<string> _saved = new();
    private string? _savedStations;

    private bool Save()
    {
        if (_v == null) return false;
        try
        {
            if (_ws.IsProtected(_workSounds)) throw new InvalidOperationException("Refused: " + _workSounds + " is inside BMS.");
            _saved = _v.Save(_workSounds);
            _savedStations = _ws.SaveCampaignFile(_stationsName, f => _st.Save(f));
            _dirty = false;
            _status.Text = $"Saved {string.Join(", ", _saved.Select(Path.GetFileName))} in {_workSounds}, and {_savedStations}.";
            return true;
        }
        catch (Exception ex) { MessageBox.Show(this, "Save failed: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }

    private static string BackupRoot => Path.Combine(BackupStore.Root, "AtcVoices");

    private void Install()
    {
        if (_ws.BmsTheater is not { } t || _v == null) return;
        if (DbInstaller.BmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title); return; }
        if ((_dirty || _saved.Count == 0) && !Save()) return;
        string target = AtcVoiceInstaller.TargetDir(t);
        bool first = AtcVoices.Find(target, AtcVoices.TlkName) == null;
        if (MessageBox.Show(this, $"Install the ATC voices of {t.Name}:\n\n" +
                (first ? $"• {target} is made as a copy of BMS's Data\\Sounds (about 100 MB, once)\n" : $"• into {target}\n") +
                $"• {string.Join(", ", _saved.Select(Path.GetFileName))}\n• {Path.GetFileName(_savedStations)} into {t.CampaignDir}\n• sounddir / subtitlesdir in {Path.GetFileName(t.Tdf)}\n\n" +
                $"Every replaced file is copied first into {BackupRoot}. Restore puts the theater back.", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var log = AtcVoiceInstaller.Install(t, _saved, BackupRoot, new Progress<string>(s => _status.Text = s), _savedStations != null ? new[] { _savedStations } : null);
            _status.Text = string.Join("  ·  ", log);
            MessageBox.Show(this, string.Join("\n", log) + "\n\nRestart BMS and choose the theater to hear the new names.", Title);
        }
        catch (Exception ex) { MessageBox.Show(this, "Install failed: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { Cursor = null; }
    }

    private void Restore()
    {
        var b = AtcVoiceInstaller.Backups(BackupRoot).FirstOrDefault(x => File.ReadLines(Path.Combine(x.Dir, "manifest.txt")).FirstOrDefault() == "theater;" + _ws.BmsTheater?.Name);
        if (b.Dir == null) { MessageBox.Show(this, "No installation of ATC voices to undo for this theater.", Title); return; }
        if (DbInstaller.BmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title); return; }
        if (MessageBox.Show(this, $"Undo the installation of {b.Time:g} ({b.Dir})? Files it added are deleted (the copy of BMS's sounds too), files it replaced are put back.",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try { _status.Text = AtcVoiceInstaller.Restore(b.Dir); }
        catch (Exception ex) { MessageBox.Show(this, "Restore failed: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
