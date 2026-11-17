using System.Globalization;
using System.IO;
using PartCombiner.Core;

namespace PartCombiner;

/// <summary>One naming style in the list, with example names for the current source.</summary>
public sealed class SplitStyleChoice(SplitViewModel owner, SplitStyle style, string title) : ObservableObject
{
    public SplitStyle Style { get; } = style;
    public string Title { get; } = title;
    public string Example => owner.ExampleFor(Style);
    public bool IsArchive => FileSplitter.IsArchiveStyle(Style);

    public bool IsSelected
    {
        get => owner.Style == Style;
        set { if (value) owner.Style = Style; }
    }

    internal void Refresh() => Raise(nameof(IsSelected), nameof(Example));
}

public sealed class SplitViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Func<SevenZipEngine?> _engine;
    private CancellationTokenSource? _cts;
    private string _source = "";
    private long _sourceSize = -1;
    private bool _isBusy;
    private double _progress;
    private string _status = "";
    private bool _statusIsError;
    private string? _lastFirstPart;
    private int _sizeRequest;

    public SplitViewModel(AppSettings settings, Func<SevenZipEngine?> engine)
    {
        _settings = settings;
        _engine = engine;
        _partsText = Math.Clamp(settings.SplitParts, 2, FileSplitter.MaxParts).ToString(CultureInfo.CurrentCulture);
        Styles =
        [
            new(this, SplitStyle.Numbered, "Numbered"),
            new(this, SplitStyle.PartSuffix, ".part"),
            new(this, SplitStyle.Alphabetic, "Letters"),
            new(this, SplitStyle.SevenZip, "7z archive"),
            new(this, SplitStyle.Zip, "Zip archive"),
        ];
        _status = "Choose a file or folder to split";
    }

    public IReadOnlyList<SplitStyleChoice> Styles { get; }

    /// <summary>Raised when the user may want to see the result (the main window reveals it if "Open folder when done").</summary>
    public event EventHandler<string>? Finished;

    // ----- Source -----

    public string Source
    {
        get => _source;
        private set
        {
            if (!SetField(ref _source, value)) return;
            _sourceSize = -1;
            Raise(nameof(HasSource), nameof(HasNoSource), nameof(SourceName), nameof(SourceDetails), nameof(SourceIsFolder), nameof(SourceGlyph));
            RefreshPreview();
            _ = MeasureAsync();
        }
    }

    public bool HasSource => _source.Length > 0;
    public bool HasNoSource => !HasSource;
    public bool SourceIsFolder => HasSource && Directory.Exists(_source);
    public string SourceName => HasSource ? Path.GetFileName(_source.TrimEnd('\\')) : "";
    public string SourceGlyph => SourceIsFolder ? "" : "";

    public string SourceDetails
    {
        get
        {
            if (!HasSource) return "";
            var size = _sourceSize >= 0 ? Bytes.Format(_sourceSize) : "measuring…";
            return SourceIsFolder ? $"Folder  ·  {size}  ·  {Path.GetDirectoryName(_source.TrimEnd('\\'))}"
                                  : $"{size}  ·  {Path.GetDirectoryName(_source)}";
        }
    }

    public void SetSource(string path)
    {
        if (IsBusy) return;
        Source = Path.GetFullPath(path);
        _lastFirstPart = null;
        Status = "Ready to split";
        StatusIsError = false;
        Raise(nameof(CanReveal));
        // A folder only works as an archive; switch to 7z so the choice makes sense right away.
        if (SourceIsFolder && !FileSplitter.IsArchiveStyle(Style))
            Style = SplitStyle.SevenZip;
    }

    private async Task MeasureAsync()
    {
        int request = ++_sizeRequest;
        var path = _source;
        long size = await Task.Run(() =>
        {
            try
            {
                if (File.Exists(path)) return new FileInfo(path).Length;
                if (Directory.Exists(path))
                    return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Sum(f => f.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return 0L;
        });
        if (request != _sizeRequest) return; // a newer source was picked meanwhile
        _sourceSize = size;
        Raise(nameof(SourceDetails));
        RefreshPreview();
    }

    // ----- Options -----

    private string _partsText;

    /// <summary>The part count as typed (kept as text so typing isn't fought by the binding).</summary>
    public string PartsText
    {
        get => _partsText;
        set
        {
            if (!SetField(ref _partsText, value ?? "")) return;
            if (Parts is int n) _settings.SplitParts = n;
            RefreshPreview();
        }
    }

    public int? Parts => int.TryParse(_partsText.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) ? n : null;

    public void StepParts(int delta)
    {
        int current = Parts ?? 2;
        PartsText = Math.Clamp(current + delta, 2, FileSplitter.MaxParts).ToString(CultureInfo.CurrentCulture);
    }

    public SplitStyle Style
    {
        get => _settings.SplitStyle;
        set
        {
            if (_settings.SplitStyle == value) return;
            _settings.SplitStyle = value;
            Raise(nameof(Style), nameof(CrcAvailable), nameof(PasswordHint));
            foreach (var s in Styles) s.Refresh();
            RefreshPreview();
        }
    }

    public bool WriteCrc
    {
        get => _settings.SplitWriteCrc;
        set { if (_settings.SplitWriteCrc != value) { _settings.SplitWriteCrc = value; Raise(nameof(WriteCrc)); } }
    }

    public bool CrcAvailable => !FileSplitter.IsArchiveStyle(Style);

    private string _password = "";

    /// <summary>Set from the password box in code-behind. Never saved to settings.</summary>
    public string Password
    {
        get => _password;
        set
        {
            if (_password == value) return;
            _password = value ?? "";
            Raise(nameof(PasswordHint));
            RefreshPreview();
        }
    }

    public string PasswordHint => FileSplitter.IsArchiveStyle(Style)
        ? (_password.Length > 0 ? "Encrypted with AES-256. Without it, the parts can't be opened." : "Encrypts the parts with AES-256")
        : "Only with the 7z or Zip style";

    public string OutputFolder
    {
        get => _settings.SplitOutputFolder ?? "";
        set
        {
            _settings.SplitOutputFolder = string.IsNullOrWhiteSpace(value) ? null : value;
            Raise(nameof(OutputFolder), nameof(OutputFolderDisplay), nameof(HasCustomOutput));
        }
    }

    public string OutputFolderDisplay => HasCustomOutput ? OutputFolder : "Same folder as the original";
    public bool HasCustomOutput => !string.IsNullOrEmpty(_settings.SplitOutputFolder);

    // ----- Preview -----

    private string _preview = "";
    private bool _previewIsError;

    public string Preview { get => _preview; private set => SetField(ref _preview, value); }
    public bool PreviewIsError { get => _previewIsError; private set => SetField(ref _previewIsError, value); }

    internal string ExampleFor(SplitStyle style)
    {
        var baseName = HasSource ? FileSplitter.BaseName(_source, style)
            : style switch { SplitStyle.SevenZip => "movie.7z", SplitStyle.Zip => "movie.zip", _ => "movie.mkv" };
        int count = Math.Clamp(Parts ?? 3, 2, FileSplitter.MaxParts);
        if (style == SplitStyle.Alphabetic) count = Math.Min(count, FileSplitter.MaxLetterParts);
        string Name(int i) => FileSplitter.PartName(baseName, style, i, count);
        return count > 2 ? $"{Name(0)},  {Name(1)}  …  {Name(count - 1)}" : $"{Name(0)},  {Name(1)}";
    }

    /// <summary>7-Zip was found or changed: the archive styles may have become possible.</summary>
    public void EngineChanged() => RefreshPreview();

    private void RefreshPreview()
    {
        foreach (var s in Styles) s.Refresh();
        Raise(nameof(CanSplit));

        if (!HasSource)
        {
            Preview = "";
            return;
        }
        if (Parts is not int parts)
        {
            Show("Type how many parts you want", error: true);
            return;
        }
        var problem = FileSplitter.Validate(Options(parts), _engine() is not null);
        if (problem is not null)
        {
            Show(problem, error: true);
            return;
        }
        if (FileSplitter.IsArchiveStyle(Style))
        {
            var archive = FileSplitter.BaseName(_source, Style);
            Show($"Packed into {archive}, then cut into {parts:N0} equal parts", error: false);
            return;
        }
        if (_sourceSize < 0)
        {
            Show($"{parts:N0} parts", error: false);
            return;
        }
        long size = FileSplitter.PartSize(_sourceSize, parts);
        long last = _sourceSize - size * (parts - 1);
        Show(last == size
            ? $"{parts:N0} parts of {Bytes.Format(size)}"
            : $"{parts:N0} parts of {Bytes.Format(size)} (the last one {Bytes.Format(last)})", error: false);

        void Show(string text, bool error)
        {
            Preview = text;
            PreviewIsError = error;
        }
    }

    private SplitOptions Options(int parts) => new()
    {
        Source = _source,
        Parts = parts,
        Style = Style,
        OutputDirectory = HasCustomOutput ? OutputFolder : null,
        WriteCrc = WriteCrc && CrcAvailable,
        Password = _password.Length > 0 ? _password : null,
    };

    // ----- Run -----

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetField(ref _isBusy, value)) Raise(nameof(IsIdle), nameof(CanSplit)); }
    }

    public bool IsIdle => !_isBusy;

    public bool CanSplit => !IsBusy && HasSource && Parts is int p && FileSplitter.Validate(Options(p), _engine() is not null) is null;

    public double Progress { get => _progress; private set => SetField(ref _progress, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public bool StatusIsError { get => _statusIsError; private set => SetField(ref _statusIsError, value); }
    public bool CanReveal => _lastFirstPart is not null && File.Exists(_lastFirstPart);
    public string? LastFirstPart => _lastFirstPart;

    public Task? CurrentRun { get; private set; }

    public Task SplitAsync()
    {
        if (!CanSplit) return Task.CompletedTask;
        CurrentRun = RunAsync();
        return CurrentRun;
    }

    public void Cancel() => _cts?.Cancel();

    private async Task RunAsync()
    {
        var options = Options(Parts!.Value);
        IsBusy = true;
        Progress = 0;
        _cts = new CancellationTokenSource();
        var progress = new Progress<SplitProgress>(p =>
        {
            Progress = p.Fraction;
            Status = p.Stage switch
            {
                SplitStage.Packing => $"Packing into {FileSplitter.BaseName(options.Source, options.Style)}…  {p.Fraction * 2:P0}",
                SplitStage.Splitting => $"Splitting…  {p.Fraction:P0}",
                _ => "Checking the parts…",
            };
        });
        Status = "Starting…";
        StatusIsError = false;

        try
        {
            var result = await FileSplitter.SplitAsync(options, _engine(), progress, _cts.Token);
            if (result.Success)
            {
                _lastFirstPart = result.FirstPart;
                Status = $"Split into {result.Parts.Count:N0} parts of {Bytes.Format(result.PartSize)}" +
                         (options.Password is not null ? "  ·  password protected" : "") +
                         (result.Verified ? "  ·  checked: they combine back into the original" : "");
                StatusIsError = !result.Verified;
                Finished?.Invoke(this, result.FirstPart!);
            }
            else
            {
                Status = result.Error ?? "Splitting failed";
                StatusIsError = true;
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Canceled  ·  the parts written so far were removed";
            StatusIsError = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
            StatusIsError = true;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            Progress = 0;
            Raise(nameof(CanReveal));
        }
    }
}
