using System.IO;
using PartCombiner.Core;

namespace PartCombiner;

public enum ItemState { Ready, Warning, Blocked, NeedsEngine, Working, Done, Failed, Canceled }

public sealed record PartRow(string Name, string Size);

public sealed record IssueRow(string Message, bool IsError);

/// <summary>One detected set of parts, as shown in the list.</summary>
public sealed class PartSetViewModel : ObservableObject
{
    private ItemState _state;
    private string _statusText = "";
    private string _statusGlyph = "";
    private double _progress;
    private bool _isExpanded;
    private string? _outputPath;
    private bool _willExtract;

    public PartSetViewModel(PartSet set, bool extractArchives, bool engineAvailable)
    {
        Set = set;
        PartRows = set.Parts.Select(p => new PartRow(p.Name, Bytes.Format(p.Size))).ToList();
        Refresh(extractArchives, engineAvailable);
    }

    public PartSet Set { get; }

    public string Key => Set.Key;

    public string Title => Set.DisplayName;

    public string FolderPath => Set.Directory;

    public string Badge => Set.Kind switch
    {
        SetKind.RarVolumes => "RAR",
        SetKind.ZipSpanned => "ZIP",
        SetKind.SplitArchive => FormatInfo.ShortName(Set.Format) is { Length: > 0 } s ? s : "ARC",
        _ => FileBadge(Set.JoinedFileName),
    };

    public string Details
    {
        get
        {
            var count = Set.Parts.Count == 1 ? "1 part" : $"{Set.Parts.Count} parts";
            return $"{count}  ·  {Bytes.Format(Set.TotalSize)}  ·  {Set.SchemeLabel}";
        }
    }

    public IReadOnlyList<PartRow> PartRows { get; }

    public IReadOnlyList<IssueRow> IssueRows =>
        Set.Issues.OrderByDescending(i => i.Severity).Select(i => new IssueRow(i.Message, i.Severity == IssueSeverity.Error)).ToList();

    /// <summary>All problems, for the status line tooltip.</summary>
    public string IssueSummary => Set.Issues.Count == 0 ? StatusText : string.Join("\n", Set.Issues.Select(i => "• " + i.Message));

    public bool HasIssues => Set.Issues.Count > 0;

    public ItemState State
    {
        get => _state;
        private set
        {
            if (SetField(ref _state, value))
                Raise(nameof(IsWorking), nameof(IsDone), nameof(CanRemove), nameof(CanProcess), nameof(ShowProgress));
        }
    }

    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }

    public string StatusGlyph { get => _statusGlyph; private set => SetField(ref _statusGlyph, value); }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetField(ref _progress, value))
                Raise(nameof(ProgressText));
        }
    }

    public string ProgressText => $"{Math.Floor(_progress * 100):0}%";

    public bool IsExpanded { get => _isExpanded; set => SetField(ref _isExpanded, value); }

    public string? OutputPath { get => _outputPath; private set => SetField(ref _outputPath, value); }

    public bool WillExtract { get => _willExtract; private set => SetField(ref _willExtract, value); }

    public bool IsWorking => _state == ItemState.Working;

    public bool IsDone => _state == ItemState.Done && _outputPath is not null;

    public bool ShowProgress => _state == ItemState.Working;

    public bool CanRemove => _state != ItemState.Working;

    public bool CanProcess => _state is ItemState.Ready or ItemState.Warning or ItemState.Failed or ItemState.Canceled;

    /// <summary>Re-evaluate the idle status after the output mode or 7-Zip availability changed.</summary>
    public void Refresh(bool extractArchives, bool engineAvailable)
    {
        WillExtract = CombineService.WillExtract(Set, extractArchives);
        if (_state is ItemState.Working or ItemState.Done or ItemState.Failed)
            return;

        var error = Set.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error);
        var warning = Set.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Warning);

        if (error is not null)
        {
            // The card opens to list the problems in full; the status line just says it's blocked.
            int errors = Set.Issues.Count(i => i.Severity == IssueSeverity.Error);
            SetStatus(ItemState.Blocked, errors == 1 ? "Won't be combined" : $"Won't be combined  ·  {errors} problems", Glyphs.Error);
        }
        else if (WillExtract && !engineAvailable)
            SetStatus(ItemState.NeedsEngine, "7-Zip is needed to extract this", Glyphs.Warning);
        else if (warning is not null)
            SetStatus(ItemState.Warning, warning.Message, Glyphs.Warning);
        else
            SetStatus(ItemState.Ready, WillExtract ? "Ready to extract" : "Ready to join", "");
    }

    public void Begin()
    {
        OutputPath = null;
        Progress = 0;
        SetStatus(ItemState.Working, "Starting…", "");
    }

    public void Report(CombineProgress p)
    {
        if (_state != ItemState.Working) return;
        Progress = p.Fraction;
        string stage = p.Stage switch
        {
            CombineStage.Joining => "Joining parts",
            CombineStage.Extracting => "Extracting",
            CombineStage.Unpacking => "Unpacking tar",
            _ => "Finishing",
        };
        var item = string.IsNullOrEmpty(p.CurrentItem) ? "" : "  ·  " + Path.GetFileName(p.CurrentItem.TrimEnd('\\', '/'));
        StatusText = stage + item;
    }

    public void Complete(CombineResult result)
    {
        OutputPath = result.OutputPath;
        if (result.Success)
        {
            Progress = 1;
            var name = result.OutputPath is null ? "" : Path.GetFileName(result.OutputPath.TrimEnd('\\'));
            var text = $"Done  →  {name}";
            if (result.ChecksumVerified) text += "  ·  checksum OK";
            if (!string.IsNullOrEmpty(result.Warning)) text += "  ·  " + result.Warning;
            SetStatus(ItemState.Done, text, Glyphs.Done);
        }
        else
        {
            Progress = 0;
            SetStatus(ItemState.Failed, result.Error ?? "Failed", Glyphs.Error);
        }
        Raise(nameof(IsDone));
    }

    public void Fail(string message)
    {
        Progress = 0;
        SetStatus(ItemState.Failed, message, Glyphs.Error);
    }

    public void MarkCanceled()
    {
        Progress = 0;
        SetStatus(ItemState.Canceled, "Canceled", Glyphs.Canceled);
    }

    private void SetStatus(ItemState state, string text, string glyph)
    {
        State = state;
        StatusText = text;
        StatusGlyph = glyph;
        Raise(nameof(IsDone));
    }

    private static string FileBadge(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        return ext.Length is > 0 and <= 4 ? ext : "FILE";
    }
}

internal static class Glyphs
{
    public const string Error = "\uE783";
    public const string Warning = "\uE7BA";
    public const string Done = "\uE930";
    public const string Canceled = "\uE711";
}
