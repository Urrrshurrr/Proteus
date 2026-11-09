using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Proteus.Core.Engine;
using Proteus.Core.Models;

namespace Proteus.App.ViewModels;

/// <summary>
/// "Match a naming style": learns a template from sample names, then shows every file in the list
/// rebuilt in that style, for checking and fixing before it becomes a rule.
/// </summary>
public sealed partial class MatchStyleViewModel : ObservableObject
{
    private readonly IUiServices? _ui;
    private readonly IReadOnlyList<FileEntry> _files;
    private bool _learning;

    public MatchStyleViewModel(IReadOnlyList<FileEntry> files, IUiServices? ui, bool hasOtherRules)
    {
        _files = files;
        _ui = ui;
        HasOtherRules = hasOtherRules;
        foreach (var f in files)
        {
            var row = new StyleRowViewModel(f);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        _separator = Separators[0];
        _case = Cases[0];
        ShowPlaceholder = files.Select(f => NameStyle.Parse(f.BaseName).Show).Where(s => s.Length > 0)
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => "From each file, e.g. " + g.Key)
            .FirstOrDefault() ?? "From each file's name";
        Render();
    }

    public static IReadOnlyList<Option<string>> Separators { get; } =
    [
        new(" ", "Spaces"),
        new(".", "Dots"),
        new("_", "Underscores"),
        new("-", "Hyphens"),
    ];

    public static IReadOnlyList<Option<WordCase>> Cases { get; } =
    [
        new(WordCase.AsIs, "As in each file"),
        new(WordCase.Title, "Title Case"),
        new(WordCase.Lower, "lowercase"),
        new(WordCase.Upper, "UPPERCASE"),
    ];

    public static string FieldHelp { get; } = string.Join("   ", NameStyle.Fields.Select(f => f.Token));

    public ObservableCollection<StyleRowViewModel> Rows { get; } = [];

    [ObservableProperty] private string _samples = string.Empty;
    [ObservableProperty] private string _template = string.Empty;
    [ObservableProperty] private Option<string> _separator;
    [ObservableProperty] private Option<WordCase> _case;
    [ObservableProperty] private bool _smallWordsLower = true;
    [ObservableProperty] private string _showOverride = string.Empty;
    [ObservableProperty] private string _yearOverride = string.Empty;
    [ObservableProperty] private bool _turnOffOtherRules = true;
    [ObservableProperty] private string _learnSummary = "Add a few names in the style you want: pick sample files, a folder, or type them in.";
    [ObservableProperty] private bool _learnFailed;

    public bool HasOtherRules { get; }
    public string ShowPlaceholder { get; }
    public bool HasTemplate => Template.Trim().Length > 0;

    public int ApplyCount => ChosenPairs().Count;
    public bool CanApply => ApplyCount > 0;
    public string ApplyText => ApplyCount == 0 ? "Nothing to apply" : $"Use {ApplyCount:N0} name{(ApplyCount == 1 ? "" : "s")}";

    public string RowSummary
    {
        get
        {
            if (!HasTemplate) return string.Empty;
            int ready = Rows.Count(r => r.IsReady && !r.IsUnchanged);
            int partial = Rows.Count(r => r.IsPartial);
            int blocked = Rows.Count(r => r.IsIncluded && !r.IsEdited && r.IsBlocked);
            int same = Rows.Count(r => r.IsUnchanged && !r.IsBlocked);
            var parts = new List<string> { $"{ready:N0} ready" };
            if (partial > 0) parts.Add($"{partial:N0} missing a part (amber: type it into the name, or untick the row)");
            if (blocked > 0) parts.Add($"{blocked:N0} left alone because no episode number or name was found");
            if (same > 0) parts.Add($"{same:N0} already in this style");
            return string.Join(", ", parts) + ".";
        }
    }

    /// <summary>Set by the window: closes it with the given result.</summary>
    public Action<bool>? Close { get; set; }

    partial void OnSamplesChanged(string value) => Learn();
    partial void OnTemplateChanged(string value) { OnPropertyChanged(nameof(HasTemplate)); Render(); }
    partial void OnSeparatorChanged(Option<string> value) => Render();
    partial void OnCaseChanged(Option<WordCase> value) => Render();
    partial void OnSmallWordsLowerChanged(bool value) => Render();
    partial void OnShowOverrideChanged(string value) => Render();
    partial void OnYearOverrideChanged(string value) => Render();

    [RelayCommand]
    private async Task AddSampleFilesAsync()
    {
        if (_ui == null) return;
        var files = await _ui.PickFilesAsync();
        AppendSamples(files.Select(Path.GetFileNameWithoutExtension).OfType<string>());
    }

    [RelayCommand]
    private async Task AddSampleFolderAsync()
    {
        if (_ui == null) return;
        var folder = await _ui.PickFolderAsync();
        if (folder == null) return;
        var filter = MatchFolderViewModel.DefaultFilter(_files);
        var names = await Task.Run(() => MatchFolderViewModel.ReadReferenceNames(folder, filter, recurse: true));
        // A handful is plenty to learn from; a whole library would only slow the box down.
        AppendSamples(names.Take(40));
    }

    public void AppendSamples(IEnumerable<string> names)
    {
        var list = names.Where(n => n.Trim().Length > 0).ToList();
        if (list.Count == 0) return;
        var text = Samples.TrimEnd();
        Samples = (text.Length > 0 ? text + Environment.NewLine : string.Empty) + string.Join(Environment.NewLine, list);
    }

    [RelayCommand]
    private void ClearSamples() => Samples = string.Empty;

    private void Learn()
    {
        var lines = Samples.Replace("\r\n", "\n").Split('\n').Select(StripExtension).Where(l => l.Length > 0).ToList();
        var style = NameStyle.Learn(lines);
        if (style == null)
        {
            LearnFailed = false;
            LearnSummary = "Add a few names in the style you want: pick sample files, a folder, or type them in.";
            return;
        }
        var fields = NameStyle.FieldsIn(style.Template);
        LearnFailed = fields.Count == 0;
        LearnSummary = LearnFailed
            ? "No show name, episode number or title could be recognised in these samples. Try adding a few more, or write the pattern yourself."
            : style.Agreeing == style.Samples
                ? $"All {style.Samples:N0} sample{(style.Samples == 1 ? "" : "s")} follow this pattern."
                : $"{style.Agreeing:N0} of {style.Samples:N0} samples follow this pattern; the others differ a little.";

        _learning = true;
        try
        {
            Template = style.Template;
            Separator = Separators.FirstOrDefault(s => s.Value == style.WordSeparator) ?? Separators[0];
            Case = Cases.First(c => c.Value == style.Case);
            SmallWordsLower = style.SmallWordsLower;
        }
        finally { _learning = false; }
        Render();
    }

    /// <summary>Sample lines may be pasted file names: drop a trailing ".mkv"-style extension.</summary>
    private static string StripExtension(string line)
    {
        var l = line.Trim().Trim('"');
        var ext = Path.GetExtension(l);
        return ext.Length is > 1 and <= 5 && ext.Skip(1).All(char.IsLetterOrDigit) && !ext.Skip(1).All(char.IsDigit)
            ? l[..^ext.Length]
            : l;
    }

    private void Render()
    {
        if (_learning) return;
        var template = Template.Trim();
        foreach (var r in Rows)
        {
            if (template.Length == 0) { r.SetStyled(null); continue; }
            r.SetStyled(NameStyle.Apply(template, r.Parsed, Separator?.Value ?? " ", Case?.Value ?? WordCase.AsIs, SmallWordsLower,
                ShowOverride, YearOverride));
        }
        UpdateDuplicates();
        RaiseApply();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StyleRowViewModel.NewBaseName)) UpdateDuplicates();
        if (e.PropertyName is nameof(StyleRowViewModel.NewBaseName) or nameof(StyleRowViewModel.IsIncluded)) RaiseApply();
    }

    private void UpdateDuplicates()
    {
        var counts = Rows.Where(r => r.IsIncluded && r.NewBaseName.Trim().Length > 0)
            .GroupBy(r => Path.Combine(r.Entry.Directory, r.NewBaseName.Trim() + r.Entry.Extension), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var r in Rows)
            r.IsDuplicate = r.IsIncluded && counts.TryGetValue(Path.Combine(r.Entry.Directory, r.NewBaseName.Trim() + r.Entry.Extension), out var c) && c > 1;
    }

    private void RaiseApply()
    {
        OnPropertyChanged(nameof(ApplyCount));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ApplyText));
        OnPropertyChanged(nameof(RowSummary));
    }

    /// <summary>The chosen pairs: each file's full path and its new name without extension.</summary>
    public List<(string Path, string NewBaseName)> ChosenPairs() =>
        Rows.Where(r => r.IsIncluded && r.NewBaseName.Trim().Length > 0 && !r.IsUnchanged)
            .Select(r => (r.Entry.FullPath, r.NewBaseName.Trim()))
            .ToList();

    [RelayCommand]
    private void Apply()
    {
        if (!CanApply) return;
        Close?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => Close?.Invoke(false);
}

public sealed partial class StyleRowViewModel : ObservableObject
{
    private bool _settingStyled;
    private StyledName? _styled;

    public StyleRowViewModel(FileEntry entry)
    {
        Entry = entry;
        Parsed = NameStyle.Parse(entry.BaseName);
        _newBaseName = entry.BaseName;
    }

    public FileEntry Entry { get; }
    public ParsedName Parsed { get; }
    public string CurrentName => Entry.FileName;
    public string Extension => Entry.Extension;
    public string Folder => Entry.Directory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnchanged), nameof(Status), nameof(StatusTip), nameof(IsReady), nameof(IsPartial))]
    private string _newBaseName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(StatusTip), nameof(IsReady), nameof(IsPartial))]
    private bool _isIncluded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(StatusTip))]
    private bool _isDuplicate;

    /// <summary>True once the user has typed in the name box; style changes then leave the row alone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(StatusTip), nameof(IsReady), nameof(IsPartial))]
    private bool _isEdited;

    public bool IsComplete => _styled?.IsComplete ?? false;

    /// <summary>Without its episode number or name a styled name would be misleading, so the file is left alone.</summary>
    public bool IsBlocked => _styled != null && _styled.Missing.Any(m => m is "episode number" or "name");
    public bool IsUnchanged => string.Equals(NewBaseName.Trim(), Entry.BaseName, StringComparison.Ordinal);
    public bool IsReady => IsIncluded && !IsDuplicate && (IsEdited || IsComplete);
    public bool IsPartial => IsIncluded && !IsDuplicate && !IsEdited && _styled != null && !IsComplete && !IsUnchanged;

    public string Status
    {
        get
        {
            if (!IsIncluded) return "Skipped";
            if (IsDuplicate) return "Used twice";
            if (!IsEdited && IsBlocked) return "No " + _styled!.Missing.First(m => m is "episode number" or "name");
            if (IsUnchanged) return "Same name";
            if (IsEdited) return "Edited";
            if (_styled == null) return "—";
            return IsComplete ? "Ready" : "No " + _styled.Missing[0];
        }
    }

    public string StatusTip => Status switch
    {
        "Used twice" => "Another file in the same folder would get this name too. Change one of them.",
        "Same name" => "This file already has this name, so it is left alone.",
        "Edited" => "You changed this name by hand. Style changes no longer touch it.",
        "Ready" => "Everything the style needs was found in the file's name.",
        "Skipped" => "This file is left as it is.",
        "No episode number" => "No season or episode number was found in this file's name, so it is left as it is. Type a name to rename it anyway.",
        "No name" => "No show or film name was found in this file's name. Type it under “Show name”, or type the whole name here.",
        _ => _styled is { Missing.Count: > 0 }
            ? $"The file's name has no {string.Join(" or ", _styled.Missing)}, so that part was left out. Type it in, or untick the row."
            : string.Empty,
    };

    partial void OnNewBaseNameChanged(string value)
    {
        if (!_settingStyled) IsEdited = true;
    }

    /// <summary>Shows the name the style produces, unless the user has typed their own.</summary>
    public void SetStyled(StyledName? styled)
    {
        _styled = styled;
        if (!IsEdited)
        {
            _settingStyled = true;
            try { NewBaseName = !IsBlocked && styled?.Name is { Length: > 0 } n ? n : Entry.BaseName; }
            finally { _settingStyled = false; }
        }
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusTip));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsPartial));
    }

    partial void OnIsEditedChanged(bool value)
    {
        if (!value) SetStyled(_styled);
    }

    /// <summary>Throws away a hand-typed name and goes back to the styled one.</summary>
    [RelayCommand]
    private void Reset() => IsEdited = false;
}
