using System.Globalization;
using TerrainTool.Core.Data;
using TerrainTool.Core.NewTerrain;
using TerrainTool.Core.Theater;
using TerrainTool.Gis;
using TerrainTool.Core.Dds;

namespace TerrainTool.App;

/// <summary>Riga modificabile della tabella basi (accetta sia punto sia virgola come decimale).</summary>
public sealed class BaseRow
{
    public Airbase Base { get; }
    public BaseRow() : this(new Airbase { Status = "to verify", Tier = "Secondary" }) { }
    public BaseRow(Airbase b) { Base = b; }

    public string Name { get => Base.Name; set => Base.Name = value ?? ""; }
    public string Country { get => Base.Country; set => Base.Country = value ?? ""; }
    public string Icao { get => Base.Icao; set => Base.Icao = value ?? ""; }
    public string Status { get => Base.Status; set => Base.Status = value ?? ""; }
    public string Tier { get => Base.Tier; set => Base.Tier = value ?? ""; }
    public static string[] TierChoices => Tiers.All;
    public string LatText { get => Fmt(Base.Lat); set => Base.Lat = Parse(value); }
    public string LonText { get => Fmt(Base.Lon); set => Base.Lon = Parse(value); }
    public string LatOldText { get => Fmt(Base.LatOld); set => Base.LatOld = Parse(value); }
    public string LonOldText { get => Fmt(Base.LonOld); set => Base.LonOld = Parse(value); }

    internal static string Fmt(double v) => double.IsNaN(v) ? "" : v.ToString("0.#####", CultureInfo.InvariantCulture);

    internal static double Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return double.NaN;
        return double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }
}

public sealed class SourceRow
{
    public required DataSource Source { get; init; }
    public string Name => Source.Name;
    public string Purpose => Source.Purpose;
    public string Login => Source.Login;
    public string Step => Source.Step;
    public string Folder => Source.Folder;
    public string ApproxSize => Source.ApproxSize;
    public bool IsAutomatic => Source.IsAutomatic;
    public bool Include { get; set; }
    /// <summary>File previsti per il teatro ("manual" se si scarica a mano).</summary>
    public string Planned { get; init; } = "";
    /// <summary>File già presenti (inclusi quelli "non disponibili", cioè tile in mare).</summary>
    public string Present { get; init; } = "";
    public int Files { get; init; }
    public string Size { get; init; } = "";
}

public sealed class CheckRow
{
    public required CheckResult Result { get; init; }
    public SpecEntry Entry => Result.Entry;
    public string StatusLabel => Result.StatusLabel;
    public string Producer => NewTerrainSpec.ProducerLabel(Result.Entry.ProducedBy);
    public string Actual => Result.FileCount is int n ? $"{n} files" : NewTerrainChecker.FormatSize(Result.ActualSize);
    public string Expected => NewTerrainChecker.FormatSize(Result.ExpectedSize);
    public string Korea => NewTerrainChecker.FormatSize(Result.KoreaSize);
    public string Text => string.IsNullOrEmpty(Result.Note) ? Result.Entry.Description : Result.Entry.Description + " — " + Result.Note;
}

public sealed class StepRow : System.ComponentModel.INotifyPropertyChanged
{
    public required ProcessingStep Step { get; init; }
    public bool Run { get; set; } = true;
    public string Name => Step.Name;
    public string Description => Step.Description;

    private string _inputs = "", _result = "";
    public string Inputs { get => _inputs; set { _inputs = value; Changed(nameof(Inputs)); } }
    public string Result { get => _result; set { _result = value; Changed(nameof(Result)); } }
    public string? PreviewPath { get; set; }
    public List<string> Notes { get; } = new();

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}

public sealed class TexRow : System.ComponentModel.INotifyPropertyChanged
{
    public required TextureJob Job { get; init; }
    public bool Run { get; set; } = true;
    public string Name => Job.Name;
    public string Description => Job.Description;
    private string _inputs = "", _result = "";
    public string Inputs { get => _inputs; set { _inputs = value; Changed(nameof(Inputs)); } }
    public string Result { get => _result; set { _result = value; Changed(nameof(Result)); } }
    public byte[]? Preview { get; set; }
    public List<string> Notes { get; } = new();
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}
