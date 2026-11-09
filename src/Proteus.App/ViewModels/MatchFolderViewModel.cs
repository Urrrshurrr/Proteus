using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Proteus.Core.Engine;
using Proteus.Core.Models;
using Proteus.Core.Rules;

namespace Proteus.App.ViewModels;

/// <summary>A name the user can pick for a file; <see cref="Name"/> is null for "leave as is".</summary>
public sealed record ReferenceOption(string? Name, string Label)
{
    public static readonly ReferenceOption None = new(null, "— Leave this file as it is —");
    public override string ToString() => Label;
}

/// <summary>
/// "Match names from a folder": reads the names in a reference folder, pairs each file in the list
/// with its closest name (season / episode first, then title), and lets the user check and fix the pairs.
/// </summary>
public sealed partial class MatchFolderViewModel : ObservableObject
{
    public static readonly string VideoExtensions = "*.mkv;*.mp4;*.avi;*.m4v;*.mov;*.wmv;*.ts;*.m2ts;*.webm;*.mpg;*.mpeg;*.flv";

    private static readonly HashSet<string> VideoSet = new(
        VideoExtensions.Split(';').Select(p => p.TrimStart('*')), StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<FileEntry> _files;
    private readonly IUiServices? _ui;
    private CancellationTokenSource? _scanCts;

    public MatchFolderViewModel(IReadOnlyList<FileEntry> files, IUiServices? ui, bool hasOtherRules)
    {
        _files = files;
        _ui = ui;
        HasOtherRules = hasOtherRules;
        _filter = DefaultFilter(files);
    }

    /// <summary>Video extensions when the list holds videos, otherwise the list's own extensions.</summary>
    public static string DefaultFilter(IEnumerable<FileEntry> files)
    {
        var exts = files.Where(f => !f.IsFolder && f.Extension.Length > 0)
            .Select(f => f.Extension.ToLowerInvariant()).Distinct().ToList();
        if (exts.Count == 0) return "*";
        if (exts.Any(VideoSet.Contains))
        {
            var extra = exts.Where(e => !VideoSet.Contains(e)).Select(e => "*" + e);
            return string.Join(";", new[] { VideoExtensions }.Concat(extra));
        }
        return string.Join(";", exts.Select(e => "*" + e));
    }

    public ObservableCollection<MatchRowViewModel> Rows { get; } = [];
    public ObservableCollection<ReferenceOption> Options { get; } = [];

    [ObservableProperty] private string _referenceFolder = string.Empty;
    [ObservableProperty] private bool _includeSubfolders = true;
    [ObservableProperty] private string _filter;
    [ObservableProperty] private bool _turnOffOtherRules = true;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _summary = "Choose the folder whose names you want to copy.";

    public bool HasOtherRules { get; }
    public bool HasRows => Rows.Count > 0;
    public int FileCount => _files.Count;

    public int ApplyCount => Rows.Count(r => r.IsIncluded && r.Selected?.Name != null && !r.IsUnchanged);
    public bool CanApply => !IsScanning && ApplyCount > 0;
    public string ApplyText => ApplyCount == 0 ? "Nothing to apply" : $"Use {ApplyCount:N0} name{(ApplyCount == 1 ? "" : "s")}";

    /// <summary>Set by the window: closes it with the given result.</summary>
    public Action<bool>? Close { get; set; }

    partial void OnReferenceFolderChanged(string value) => _ = ScanAsync();
    partial void OnIncludeSubfoldersChanged(bool value) => _ = ScanAsync();
    partial void OnFilterChanged(string value) => _ = ScanAsync();
    partial void OnIsScanningChanged(bool value) => RaiseApply();

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (_ui == null) return;
        var folder = await _ui.PickFolderAsync();
        if (folder != null) ReferenceFolder = folder;
    }

    /// <summary>Reads the reference folder and pairs every file. Safe to call repeatedly; the latest call wins.</summary>
    public async Task ScanAsync()
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var folder = ReferenceFolder.Trim().Trim('"');
        Error = null;
        if (folder.Length == 0)
        {
            ClearRows();
            Summary = "Choose the folder whose names you want to copy.";
            return;
        }
        if (!Directory.Exists(folder))
        {
            ClearRows();
            Error = "That folder can't be found. Check the path, or that the drive is connected.";
            Summary = string.Empty;
            return;
        }

        IsScanning = true;
        Summary = "Reading names…";
        var filter = Filter;
        var recurse = IncludeSubfolders;
        var sources = _files.Select(f => f.BaseName).ToList();
        var ownPaths = _files.Select(f => f.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (names, matches) = await Task.Run(() =>
            {
                var refs = ReadReferenceNames(folder, filter, recurse, ownPaths, cts.Token);
                return (refs, NameMatcher.Match(sources, refs, NameMatcher.DefaultMinScore, cts.Token));
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            Fill(names, matches);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            ClearRows();
            Error = "Couldn't read that folder: " + ex.Message;
            Summary = string.Empty;
        }
        finally
        {
            if (_scanCts == cts) IsScanning = false;
        }
    }

    /// <summary>The distinct names (without extension) of the files in a folder that pass the filter.</summary>
    public static List<string> ReadReferenceNames(string folder, string filter, bool recurse,
        ISet<string>? exclude = null, CancellationToken ct = default)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
        };
        var pattern = string.IsNullOrWhiteSpace(filter) ? "*" : filter;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", opts))
        {
            ct.ThrowIfCancellationRequested();
            if (exclude != null && exclude.Contains(path)) continue;
            var fileName = Path.GetFileName(path);
            if (!Wildcard.IsMatchAny(fileName, pattern)) continue;
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            if (IsExtra(folder, path, baseName)) continue;
            if (baseName.Length > 0 && seen.Add(baseName)) names.Add(baseName);
        }
        names.Sort(NaturalComparer.Instance);
        return names;
    }

    // Media servers (Jellyfin, Plex, Emby, Kodi) keep trailers and bonus material next to the episodes.
    private static readonly string[] ExtraSuffixes =
        ["-trailer", "-sample", "-featurette", "-behindthescenes", "-deleted", "-deletedscene", "-interview", "-scene", "-short", "-clip", "-extra", "-other", "-theme"];

    private static readonly HashSet<string> ExtraFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "trailers", "featurettes", "behind the scenes", "deleted scenes", "interviews", "scenes", "shorts",
        "samples", "sample", "clips", "other", "backdrops", "theme-music", "theme music", ".actors",
    };

    /// <summary>Trailers, samples and bonus clips are never episode names.</summary>
    public static bool IsExtra(string root, string path, string baseName)
    {
        if (ExtraSuffixes.Any(s => baseName.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return true;
        if (baseName.Equals("trailer", StringComparison.OrdinalIgnoreCase) || baseName.Equals("sample", StringComparison.OrdinalIgnoreCase)) return true;
        var rel = Path.GetRelativePath(root, Path.GetDirectoryName(path) ?? root);
        if (rel == ".") return false;
        return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ExtraFolders.Contains);
    }

    private void ClearRows()
    {
        foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        Options.Clear();
        OnPropertyChanged(nameof(HasRows));
        RaiseApply();
    }

    private void Fill(List<string> names, IReadOnlyList<NameMatch> matches)
    {
        ClearRows();
        Options.Add(ReferenceOption.None);
        var options = names.Select(n => new ReferenceOption(n, n)).ToList();
        foreach (var o in options) Options.Add(o);
        var keys = _files.Select(f => NameMatcher.FindEpisode(f.BaseName)).ToList();
        foreach (var m in matches)
        {
            var row = new MatchRowViewModel(_files[m.SourceIndex], Options, m.ReferenceIndex >= 0 ? options[m.ReferenceIndex] : ReferenceOption.None, m.Kind, m.Score);
            // Two copies of the same episode: only one can take the name.
            if (m.ReferenceIndex < 0 && keys[m.SourceIndex] is { } k)
                row.SameEpisodeAsOther = matches.Any(o => o.SourceIndex != m.SourceIndex && o.ReferenceIndex >= 0 && keys[o.SourceIndex] is { } ok && ok.SameAs(k));
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        UpdateDuplicates();
        OnPropertyChanged(nameof(HasRows));
        RaiseApply();
        UpdateSummary(names.Count);
    }

    private void UpdateSummary(int referenceCount)
    {
        if (referenceCount == 0)
        {
            Summary = "No matching files in that folder. Check the file types filter, or turn on subfolders.";
            return;
        }
        int episode = Rows.Count(r => r.Kind == MatchKind.Episode);
        int similar = Rows.Count(r => r.Kind == MatchKind.Similar);
        int same = Rows.Count(r => r.Kind == MatchKind.None && r.SameEpisodeAsOther);
        int none = Rows.Count(r => r.Kind == MatchKind.None) - same;
        var parts = new List<string>();
        if (episode > 0) parts.Add($"{episode:N0} by episode number");
        if (similar > 0) parts.Add($"{similar:N0} by similar name");
        if (none > 0) parts.Add($"{none:N0} without a match");
        if (same > 0) parts.Add($"{same:N0} left out as a second copy of an episode");
        Summary = $"Found {referenceCount:N0} name{(referenceCount == 1 ? "" : "s")}. Matched " + string.Join(", ", parts) + ". Check each pair, and pick a different name where needed.";
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MatchRowViewModel.Selected)) UpdateDuplicates();
        if (e.PropertyName is nameof(MatchRowViewModel.Selected) or nameof(MatchRowViewModel.IsIncluded)) RaiseApply();
    }

    private void UpdateDuplicates()
    {
        var counts = Rows.Where(r => r.Selected?.Name != null)
            .GroupBy(r => r.Selected!.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var r in Rows)
            r.IsDuplicate = r.Selected?.Name is { } n && counts.TryGetValue(n, out var c) && c > 1;
    }

    private void RaiseApply()
    {
        OnPropertyChanged(nameof(ApplyCount));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ApplyText));
    }

    /// <summary>The chosen pairs: each file's full path and its new name without extension.</summary>
    public List<(string Path, string NewBaseName)> ChosenPairs() =>
        Rows.Where(r => r.IsIncluded && r.Selected?.Name != null && !r.IsUnchanged)
            .Select(r => (r.Entry.FullPath, r.Selected!.Name!))
            .ToList();

    [RelayCommand]
    private void Apply()
    {
        if (!CanApply) return;
        Close?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        _scanCts?.Cancel();
        Close?.Invoke(false);
    }
}

public sealed partial class MatchRowViewModel : ObservableObject
{
    public MatchRowViewModel(FileEntry entry, IReadOnlyList<ReferenceOption> options, ReferenceOption selected, MatchKind kind, double score)
    {
        Entry = entry;
        Options = options;
        _selected = selected;
        Kind = kind;
        Score = score;
        _isIncluded = selected.Name != null;
    }

    public FileEntry Entry { get; }
    public IReadOnlyList<ReferenceOption> Options { get; }
    public MatchKind Kind { get; }
    public double Score { get; }
    public string CurrentName => Entry.FileName;
    public string Folder => Entry.Directory;

    /// <summary>Unmatched because another file in the list is the same episode and took the name.</summary>
    public bool SameEpisodeAsOther { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewName), nameof(IsUnchanged), nameof(Confidence), nameof(ConfidenceTip), nameof(IsSure), nameof(IsUnsure), nameof(IsPicked))]
    private ReferenceOption? _selected;

    [ObservableProperty] private bool _isIncluded;
    [ObservableProperty] private bool _isDuplicate;

    private bool _userPicked;

    partial void OnSelectedChanged(ReferenceOption? oldValue, ReferenceOption? newValue)
    {
        if (oldValue != null) _userPicked = true;
        // Picking a name turns the row on; choosing "leave as is" turns it off.
        IsIncluded = newValue?.Name != null;
    }

    /// <summary>The name the file will get: the reference name plus the file's own extension.</summary>
    public string NewName => Selected?.Name is { } n ? n + Entry.Extension : CurrentName;

    public bool IsUnchanged => Selected?.Name is { } n && string.Equals(n + Entry.Extension, CurrentName, StringComparison.Ordinal);

    public bool IsPicked => _userPicked && Selected?.Name != null;
    public bool IsSure => !IsPicked && Selected?.Name != null && Kind == MatchKind.Episode;
    public bool IsUnsure => !IsPicked && Selected?.Name != null && Kind == MatchKind.Similar;

    public string Confidence
    {
        get
        {
            if (Selected?.Name == null) return SameEpisodeAsOther ? "Same episode" : "No match";
            if (IsUnchanged) return "Same name";
            if (_userPicked) return "Your pick";
            return Kind == MatchKind.Episode ? "Episode" : Score >= 0.6 ? "Similar" : "Weak";
        }
    }

    public string ConfidenceTip => Confidence switch
    {
        "Episode" => "Both names have the same season and episode number" + (FindEpisode() is { } k ? $" ({k})." : "."),
        "Similar" => "Matched on how alike the names are. Worth a quick look.",
        "Weak" => "Only a loose resemblance. Please check this one.",
        "Your pick" => "You chose this name.",
        "Same name" => "The file already has this name, so it is left alone.",
        "Same episode" => "Another file in your list is the same episode and took its name. If this copy is the one you want, pick the name here instead.",
        _ => "Nothing in the folder was close enough. Pick a name from the list, or leave the file as it is.",
    };

    private EpisodeKey? FindEpisode() => NameMatcher.FindEpisode(Entry.BaseName);
}
