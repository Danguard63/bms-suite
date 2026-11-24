using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Sound;

/// <summary>
/// The radio voice files of a theater's Sounds folder that make ATC say an airbase's name (BMS 4.38):
/// <list type="bullet">
/// <item>Campaign\stations+ils.dat: per airbase (CampID) TOWER_FRAG, GROUND_FRAG (the plain name), APPROACH_FRAG and the callsign
/// (index of the name in TTSEval.dat line 24, for the text-to-speech radio); TacanList::GetCallsignFromCampID / GetAirbase*Frag;</item>
/// <item>fragfile.xml: fragment → one Falcon.tlk clip per voice (ATC voices: 12 female, 13 male);</item>
/// <item>Falcon.tlk: the clips; F4Talk.csv: the text of each fragment (radio subtitles); TTSEval.dat line 24: names for TTS.</item>
/// </list>
/// New names are added at the end (new fragments and clips), as the Israel theater does (fragments 3699 and up). Nothing of BMS
/// is written here: Save writes the four files into a folder of the caller's choice.
/// </summary>
public sealed class AtcVoices
{
    public const int FemaleVoice = 12, MaleVoice = 13;
    public const string TlkName = "Falcon.tlk", FragName = "fragfile.xml", TalkName = "F4Talk.csv", TtsEvalName = "TTSEval.dat";
    /// <summary>The line of TTSEval.dat with the airbase names (the callsign of stations+ils.dat is the index in it).</summary>
    public const int TtsAirbaseLine = 24;

    public string SourceDir { get; private set; } = "";
    public TlkFile Tlk { get; private set; } = new();
    private string _frag = "";
    private List<string> _talk = new(), _tts = new();
    private readonly Dictionary<int, Dictionary<int, int>> _speakers = new();
    private readonly Dictionary<int, string> _text = new();
    private string _fragFile = FragName, _talkFile = TalkName, _ttsFile = TtsEvalName, _tlkFile = TlkName;
    public int Added { get; private set; }
    public bool HasTts => _tts.Count > 0;

    public static string? Find(string dir, string name) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;

    public static AtcVoices Load(string soundDir)
    {
        var v = new AtcVoices { SourceDir = soundDir };
        string tlk = Find(soundDir, TlkName) ?? throw new FileNotFoundException($"{TlkName} not found in {soundDir}");
        string frag = Find(soundDir, FragName) ?? throw new FileNotFoundException($"{FragName} not found in {soundDir}");
        v._tlkFile = Path.GetFileName(tlk); v._fragFile = Path.GetFileName(frag);
        v.Tlk = TlkFile.Load(tlk);
        v._frag = File.ReadAllText(frag);
        foreach (Match m in Regex.Matches(v._frag, @"<Frag id\s*=\s*""(\d+)""\s*>(.*?)</Frag>", RegexOptions.Singleline))
        {
            var sp = new Dictionary<int, int>();
            foreach (Match s in Regex.Matches(m.Groups[2].Value, @"voice\s*=\s*""(\d+)""\s+tlkId\s*=\s*""(\d+)"""))
                sp[int.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture)] = int.Parse(s.Groups[2].Value, CultureInfo.InvariantCulture);
            v._speakers[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = sp;
        }
        if (Find(soundDir, TalkName) is { } talk)
        {
            v._talkFile = Path.GetFileName(talk);
            v._talk = File.ReadAllLines(talk, Encoding.Latin1).ToList();
            foreach (var l in v._talk.Skip(1))
            {
                var c = SplitCsv(l);
                if (c.Count < 16 || !int.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) continue;
                // the ATC voices' text first (column of voice 12), then the summary, then any voice
                string t = c[14].Length > 0 ? c[14] : c.Count > 16 && c[16].Length > 0 ? c[16] : c.Skip(2).Take(14).FirstOrDefault(x => x.Length > 0) ?? "";
                v._text[id] = t;
            }
        }
        if (Find(soundDir, TtsEvalName) is { } tts) { v._ttsFile = Path.GetFileName(tts); v._tts = File.ReadAllLines(tts, Encoding.Latin1).ToList(); }
        return v;
    }

    private static List<string> SplitCsv(string l)
    {
        var res = new List<string>(); var sb = new StringBuilder(); bool q = false;
        foreach (char ch in l)
        {
            if (ch == '"') q = !q;
            else if (ch == ',' && !q) { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        res.Add(sb.ToString());
        return res;
    }

    /// <summary>What a fragment says (F4Talk.csv), "" if unknown.</summary>
    public string Text(int frag) => _text.TryGetValue(frag, out var t) ? t : "";

    /// <summary>The clip of a fragment for a voice, null if the fragment has none for it.</summary>
    public byte[]? Clip(int frag, int voice) =>
        _speakers.TryGetValue(frag, out var sp) && sp.TryGetValue(voice, out int id) && id >= 0 && id < Tlk.Clips.Count ? Tlk.Clips[id] : null;

    /// <summary>A fragment already saying exactly this text with both ATC voices (to reuse it), or -1.</summary>
    public int FindFrag(string text) =>
        _text.Where(kv => kv.Value.Equals(text, StringComparison.OrdinalIgnoreCase) && Clip(kv.Key, FemaleVoice) != null && Clip(kv.Key, MaleVoice) != null)
             .Select(kv => kv.Key).DefaultIfEmpty(-1).Min();

    /// <summary>Adds a fragment with its two ATC clips (8000 Hz PCM); returns its number.</summary>
    public int AddFrag(string text, short[] female, short[] male)
    {
        int frag = Math.Max(_speakers.Keys.DefaultIfEmpty(-1).Max(), _text.Keys.DefaultIfEmpty(-1).Max()) + 1;
        int a = Tlk.Add(SpeexClip.Encode(female)), b = Tlk.Add(SpeexClip.Encode(male));
        _speakers[frag] = new Dictionary<int, int> { [FemaleVoice] = a, [MaleVoice] = b };
        int end = _frag.LastIndexOf("</FragFile>", StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException($"{_fragFile}: </FragFile> not found");
        string nl = _frag.Contains("\r\n") ? "\r\n" : "\n";
        _frag = _frag[..end] + $"  <Frag id=\"{frag}\">{nl}    <Speaker voice=\"{FemaleVoice}\" tlkId=\"{a}\" />{nl}    <Speaker voice=\"{MaleVoice}\" tlkId=\"{b}\" />{nl}  </Frag>{nl}" + _frag[end..];
        // F4Talk.csv: Fragment, # Voices, Voice 0..13, Summary, Eval (as the Israel theater writes its names)
        string t = text.Replace(",", " ").Replace("\"", "");
        if (_talk.Count > 0) _talk.Add($"{frag},2,,,,,,,,,,,,,{t},{t},,");
        _text[frag] = text;
        Added++;
        return frag;
    }

    /// <summary>Index of a name in TTSEval.dat line 24 (added at the end if missing), or -1 when the theater has no TTSEval.dat.</summary>
    public int TtsIndex(string name, bool add)
    {
        int li = _tts.FindIndex(l => l.StartsWith(TtsAirbaseLine.ToString(CultureInfo.InvariantCulture) + "|", StringComparison.Ordinal));
        if (li < 0) return -1;
        var parts = _tts[li].Split('|').ToList();
        var names = parts.Skip(2).ToList();
        int i = names.FindIndex(x => x.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0 || !add) return i;
        names.Add(name.Replace("|", " "));
        _tts[li] = $"{TtsAirbaseLine}|{names.Count}|" + string.Join("|", names);
        return names.Count - 1;
    }

    public string TtsName(int index)
    {
        int li = _tts.FindIndex(l => l.StartsWith(TtsAirbaseLine.ToString(CultureInfo.InvariantCulture) + "|", StringComparison.Ordinal));
        if (li < 0) return "";
        var names = _tts[li].Split('|').Skip(2).ToList();
        return index >= 0 && index < names.Count ? names[index] : "";
    }

    /// <summary>Writes the four files into outDir (Falcon.tlk, fragfile.xml, F4Talk.csv, TTSEval.dat with the names of the source); returns them.</summary>
    public List<string> Save(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var res = new List<string>();
        string P(string n) { string f = Path.Combine(outDir, n); res.Add(f); return f; }
        Tlk.Save(P(_tlkFile));
        File.WriteAllText(P(_fragFile), _frag, new UTF8Encoding(true));
        if (_talk.Count > 0) File.WriteAllLines(P(_talkFile), _talk, Encoding.Latin1);
        if (_tts.Count > 0) File.WriteAllLines(P(_ttsFile), _tts, Encoding.Latin1);
        return res;
    }
}
