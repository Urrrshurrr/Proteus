using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Proteus.App.Services;
using Proteus.Core;
using Proteus.Core.Engine;
using Proteus.Core.History;
using Proteus.Core.Models;
using Proteus.Core.Presets;
using Proteus.Core.Rules;

namespace Proteus.App.ViewModels;

public interface IUiServices
{
    Task<IReadOnlyList<string>> PickFilesAsync();
    Task<string?> PickFolderAsync();
    Task<string?> PickCsvAsync();
    Task<string?> SaveCsvAsync(string suggestedName);
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly List<FileItemViewModel> _all = [];
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<RenameRule> _hookedRules = [];
    private readonly UndoJournal _journal;
    private readonly PresetStore _presets;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _traceCts;
    private CancellationTokenSource? _noticeCts;
    private Task _previewTask = Task.CompletedTask;
    private BatchRecord? _lastBatch;
    private PreviewResult? _lastPreview;

    /// <summary>The window's view model, so global error handlers can show a notice.</summary>
    public static MainViewModel? Current { get; private set; }

    public MainViewModel() : this(null, null) { }

    public MainViewModel(UndoJournal? journal, PresetStore? presets)
    {
        _journal = journal ?? new UndoJournal();
        _presets = presets ?? new PresetStore();
        Current = this;
        Rules.CollectionChanged += OnRulesChanged;
        var settings = AppSettings.Load();
        _isDark = settings.Dark;
        _includeSubfolders = settings.IncludeSubfolders;
        _highlightChanges = settings.HighlightChanges;
        foreach (var r in settings.Rules) Rules.Add(r);
        _options = settings.Options ?? new BatchOptions();
        // Always start by renaming in place; copying or moving must be chosen deliberately each time.
        _options.Mode = BatchMode.Rename;
        _options.PropertyChanged += OnOptionsChanged;
        if (Rules.Count == 0)
        {
            Rules.Add(new CleanupRule());
            Rules.Add(new CaseRule());
        }
        SelectedRule = Rules.FirstOrDefault();
        ApplyTheme();
        RefreshPresets();
        _ = RefreshHistoryAsync(checkInterrupted: true);
    }

    public IUiServices? Ui { get; set; }

    // ---------- Collections ----------
    public ObservableCollection<RenameRule> Rules { get; } = [];
    public ObservableCollection<TraceStepViewModel> Trace { get; } = [];
    public ObservableCollection<HistoryItemViewModel> History { get; } = [];
    public ObservableCollection<string> Presets { get; } = [];
    public IReadOnlyList<RuleKind> RuleKinds => RuleKind.All;
    public IReadOnlyList<TokenGroup> TokenGroups => TokenGroup.All;

    [ObservableProperty] private IReadOnlyList<FileItemViewModel> _visibleFiles = [];

    /// <summary>Copy/move mode, destination, conflict handling, dates and attributes.</summary>
    [ObservableProperty] private BatchOptions _options;

    partial void OnOptionsChanged(BatchOptions? oldValue, BatchOptions newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnOptionsChanged;
        newValue.PropertyChanged += OnOptionsChanged;
        OnOptionsChanged(newValue, new PropertyChangedEventArgs(null));
    }

    private void OnOptionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BatchOptions.ModeSummary) or nameof(BatchOptions.PropertiesSummary)
            or nameof(BatchOptions.ChangesProperties) or nameof(BatchOptions.ChangesTimes)
            or nameof(BatchOptions.ChangesAttributes) or nameof(BatchOptions.MovesFiles)) return;
        OnPropertyChanged(nameof(RenameButtonText));
        SchedulePreview();
    }

    [RelayCommand]
    private async Task PickDestinationAsync()
    {
        if (Ui == null) return;
        var folder = await Ui.PickFolderAsync();
        if (folder != null) Options.DestinationFolder = folder;
    }

    // ---------- State ----------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRule), nameof(SelectedRuleSupportsTarget))]
    private RenameRule? _selectedRule;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    private FileItemViewModel? _selectedFile;

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _showOnlyChanged;
    [ObservableProperty] private bool _highlightChanges = true;
    [ObservableProperty] private bool _includeSubfolders = true;
    [ObservableProperty] private bool _addFoldersAsItems;
    [ObservableProperty] private bool _isDark = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRename), nameof(RenameButtonText))]
    private bool _isBusy;

    /// <summary>True from the moment anything changes until the new preview is ready.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRename), nameof(RenameButtonText))]
    private bool _isPreviewPending;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _noticeIsError;
    [ObservableProperty] private bool _noticeCanUndo;
    [ObservableProperty] private bool _isHistoryOpen;
    [ObservableProperty] private string _newPresetName = string.Empty;
    [ObservableProperty] private SortColumn _sortColumn = SortColumn.None;
    [ObservableProperty] private bool _sortDescending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFiles), nameof(IsEmpty), nameof(StatsText))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRename), nameof(RenameButtonText), nameof(StatsText))]
    private int _changeCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems), nameof(StatsText))]
    private int _problemCount;

    public bool HasFiles => TotalCount > 0;
    public bool IsEmpty => TotalCount == 0;
    public bool HasProblems => ProblemCount > 0;
    public bool HasSelectedRule => SelectedRule != null;
    public bool HasSelectedFile => SelectedFile != null;
    public bool SelectedRuleSupportsTarget => SelectedRule?.SupportsTarget == true;
    public bool HasRules => Rules.Count > 0;
    public bool HasPresets => Presets.Count > 0;
    public bool HasHistory => History.Count > 0;
    public bool CanRename => !IsBusy && !IsPreviewPending && ChangeCount > 0;

    public string RenameButtonText
    {
        get
        {
            var mode = Options?.Mode ?? BatchMode.Rename;
            bool updateOnly = mode == BatchMode.Rename && _all.All(f => f.Status != ItemStatus.Ok);
            if (IsBusy) return mode switch { BatchMode.Copy => "Copying…", BatchMode.Move => "Moving…", _ => updateOnly ? "Updating…" : "Renaming…" };
            if (IsPreviewPending && ChangeCount > 0) return "Updating preview…";
            if (ChangeCount == 0) return "Nothing to do";
            var verb = mode switch { BatchMode.Copy => "Copy", BatchMode.Move => "Move", _ => updateOnly ? "Update" : "Rename" };
            return $"{verb} {ChangeCount:N0} item{(ChangeCount == 1 ? "" : "s")}";
        }
    }

    public string StatsText => TotalCount == 0 ? "No files yet"
        : $"{TotalCount:N0} item{(TotalCount == 1 ? "" : "s")}  ·  {ChangeCount:N0} will change" + (ProblemCount > 0 ? $"  ·  {ProblemCount:N0} need attention" : "");

    // ---------- Adding and removing files ----------
    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (Ui == null) return;
        await AddPathsAsync(await Ui.PickFilesAsync(), IncludeSubfolders, AddFoldersAsItems);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (Ui == null) return;
        var folder = await Ui.PickFolderAsync();
        if (folder != null) await AddPathsAsync([folder], IncludeSubfolders, AddFoldersAsItems);
    }

    public async void AddPaths(IEnumerable<string> paths)
    {
        try { await AddPathsAsync(paths, IncludeSubfolders, AddFoldersAsItems); }
        catch (Exception ex) { ReportError("Could not add those files", ex); }
    }

    public async Task AddPathsAsync(IEnumerable<string> paths, bool recurse, bool foldersAsItems)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;
        var known = new HashSet<string>(_known, StringComparer.OrdinalIgnoreCase);
        // Finding files and reading their details happens off the UI thread.
        var (entries, found) = await Task.Run(() =>
        {
            var paths = Expand(list, recurse, foldersAsItems);
            var made = new List<FileEntry>();
            foreach (var p in paths)
            {
                if (!known.Add(p)) continue;
                try { made.Add(new FileEntry(p)); }
                catch (Exception) { }
            }
            return (made, paths.Count);
        });
        int added = 0;
        foreach (var entry in entries)
        {
            if (!_known.Add(entry.FullPath)) continue;
            var vm = new FileItemViewModel(entry);
            vm.PropertyChanged += OnFileItemChanged;
            _all.Add(vm);
            added++;
        }
        if (added > 0)
        {
            ApplySort();
            RefreshVisible();
            SchedulePreview();
        }
        else if (found > 0)
        {
            ShowNotice("Those files are already in the list.");
        }
    }

    private static List<string> Expand(List<string> paths, bool recurse, bool foldersAsItems)
    {
        var result = new List<string>();
        foreach (var p in paths)
        {
            if (File.Exists(p)) result.Add(Path.GetFullPath(p));
            else if (Directory.Exists(p))
            {
                if (foldersAsItems)
                {
                    result.Add(Path.GetFullPath(p));
                    continue;
                }
                try
                {
                    var opts = new EnumerationOptions
                    {
                        RecurseSubdirectories = recurse,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.System,
                    };
                    var files = Directory.EnumerateFiles(p, "*", opts).ToList();
                    files.Sort(NaturalComparer.Instance);
                    result.AddRange(files);
                }
                catch (Exception) { }
            }
        }
        return result;
    }

    public void RemoveItems(IEnumerable<FileItemViewModel> items)
    {
        var set = items.ToHashSet();
        if (set.Count == 0) return;
        foreach (var item in set)
        {
            item.PropertyChanged -= OnFileItemChanged;
            _known.Remove(item.Entry.FullPath);
        }
        _all.RemoveAll(set.Contains);
        RefreshVisible();
        SchedulePreview();
    }

    [RelayCommand]
    private void ClearFiles()
    {
        foreach (var item in _all) item.PropertyChanged -= OnFileItemChanged;
        _all.Clear();
        _known.Clear();
        SelectedFile = null;
        RefreshVisible();
        SchedulePreview();
    }

    [RelayCommand]
    private void IncludeAll(string? mode)
    {
        foreach (var f in _all)
            f.IsIncluded = mode switch { "none" => false, "invert" => !f.IsIncluded, _ => true };
    }

    [RelayCommand]
    private void SortBy(SortColumn column)
    {
        if (SortColumn == column) SortDescending = !SortDescending;
        else { SortColumn = column; SortDescending = false; }
        ApplySort();
        RefreshVisible();
        SchedulePreview();
    }

    private void ApplySort()
    {
        if (SortColumn == SortColumn.None) return;
        Comparison<FileItemViewModel> cmp = SortColumn switch
        {
            SortColumn.New => (a, b) => NaturalComparer.Instance.Compare(a.NewName, b.NewName),
            SortColumn.Folder => (a, b) =>
            {
                int c = NaturalComparer.Instance.Compare(a.Folder, b.Folder);
                return c != 0 ? c : NaturalComparer.Instance.Compare(a.OriginalName, b.OriginalName);
            },
            SortColumn.Status => (a, b) => a.Status.CompareTo(b.Status),
            _ => (a, b) => NaturalComparer.Instance.Compare(a.OriginalName, b.OriginalName),
        };
        var sorted = _all.Order(Comparer<FileItemViewModel>.Create(cmp)).ToList(); // stable
        if (SortDescending) sorted.Reverse();
        _all.Clear();
        _all.AddRange(sorted);
    }

    partial void OnFilterTextChanged(string value) => RefreshVisible();
    partial void OnShowOnlyChangedChanged(bool value) => RefreshVisible();
    partial void OnSelectedFileChanged(FileItemViewModel? value) => UpdateTrace();
    partial void OnIsDarkChanged(bool value) => ApplyTheme();

    private void RefreshVisible()
    {
        IEnumerable<FileItemViewModel> q = _all;
        var f = FilterText.Trim();
        if (f.Length > 0)
            q = q.Where(x => x.OriginalName.Contains(f, StringComparison.CurrentCultureIgnoreCase)
                          || x.NewName.Contains(f, StringComparison.CurrentCultureIgnoreCase));
        if (ShowOnlyChanged) q = q.Where(x => x.HasChange);
        var selected = SelectedFile;
        VisibleFiles = q.ToList();
        TotalCount = _all.Count;
        if (selected != null && VisibleFiles.Contains(selected)) SelectedFile = selected;
    }

    private void OnFileItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileItemViewModel.IsIncluded)) SchedulePreview();
    }

    // ---------- Rules ----------
    [RelayCommand]
    private void AddRule(RuleKind kind)
    {
        var rule = kind.Create();
        int at = SelectedRule != null ? Rules.IndexOf(SelectedRule) + 1 : Rules.Count;
        Rules.Insert(at, rule);
        SelectedRule = rule;
    }

    [RelayCommand]
    private void RemoveRule(RenameRule? rule)
    {
        rule ??= SelectedRule;
        if (rule == null) return;
        int i = Rules.IndexOf(rule);
        Rules.Remove(rule);
        if (SelectedRule == null || SelectedRule == rule)
            SelectedRule = Rules.Count == 0 ? null : Rules[Math.Clamp(i, 0, Rules.Count - 1)];
    }

    [RelayCommand]
    private void DuplicateRule(RenameRule? rule)
    {
        rule ??= SelectedRule;
        if (rule == null) return;
        var copy = rule.Clone();
        Rules.Insert(Rules.IndexOf(rule) + 1, copy);
        SelectedRule = copy;
    }

    [RelayCommand]
    private void MoveRuleUp(RenameRule? rule) => MoveRule(rule ?? SelectedRule, -1);

    [RelayCommand]
    private void MoveRuleDown(RenameRule? rule) => MoveRule(rule ?? SelectedRule, +1);

    private void MoveRule(RenameRule? rule, int delta)
    {
        if (rule == null) return;
        int i = Rules.IndexOf(rule), j = i + delta;
        if (i < 0 || j < 0 || j >= Rules.Count) return;
        Rules.Move(i, j);
        SelectedRule = rule;
    }

    [RelayCommand]
    private void ClearRules()
    {
        Rules.Clear();
        SelectedRule = null;
    }

    private void OnRulesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Keep exactly the rules in the collection hooked (Clear() reports no old items).
        foreach (var r in _hookedRules.Where(r => !Rules.Contains(r)).ToList())
        {
            r.PropertyChanged -= OnRuleChanged;
            _hookedRules.Remove(r);
        }
        foreach (var r in Rules)
            if (_hookedRules.Add(r)) r.PropertyChanged += OnRuleChanged;
        OnPropertyChanged(nameof(HasRules));
        SchedulePreview();
    }

    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RenameRule.Summary) or nameof(RenameRule.HasScope) or nameof(ReplaceRule.RegexError)
            or nameof(RenameRule.RuntimeError) or nameof(ListRule.LineCount)) return;
        if (sender == SelectedRule && e.PropertyName == nameof(RenameRule.Target)) OnPropertyChanged(nameof(SelectedRuleSupportsTarget));
        SchedulePreview();
    }

    // ---------- Preview ----------
    public void SchedulePreview()
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        IsPreviewPending = true;
        var items = _all.ToArray();
        var entries = items.Select(i => i.Entry).ToArray();
        var included = items.Select(i => i.IsIncluded).ToArray();
        var originals = Rules.ToArray();
        var clones = originals.Select(r => r.Clone()).ToArray();
        var options = CloneOptions(Options);
        _previewTask = RunPreviewAsync(items, entries, included, originals, clones, options, cts);
    }

    /// <summary>Completes when the most recently scheduled preview has been applied.</summary>
    public async Task WaitForPreviewAsync()
    {
        for (int i = 0; i < 100 && IsPreviewPending; i++) await _previewTask;
    }

    private static BatchOptions CloneOptions(BatchOptions o) =>
        System.Text.Json.JsonSerializer.Deserialize<BatchOptions>(
            System.Text.Json.JsonSerializer.Serialize(o, RuleJson.Options), RuleJson.Options) ?? new BatchOptions();

    private async Task RunPreviewAsync(FileItemViewModel[] items, FileEntry[] entries, bool[] included,
        RenameRule[] originals, RenameRule[] rules, BatchOptions options, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await Task.Delay(entries.Length > 2000 ? 150 : 60, ct);
            var result = await Task.Run(() => RenamePipeline.Preview(entries, included, rules, options, ct), ct);
            if (ct.IsCancellationRequested) return;
            for (int i = 0; i < items.Length; i++)
            {
                var r = result.Items[i];
                items[i].NewName = r.NewName;
                items[i].Status = r.Status;
                items[i].Message = r.Message;
                items[i].Kind = r.Kind;
                items[i].TargetFolder = r.TargetDirectory;
                items[i].PropertyChanges = r.Properties?.Describe();
            }
            // Problems found while previewing (script errors, bad patterns) show on the real rules.
            for (int i = 0; i < originals.Length && i < rules.Length; i++) originals[i].RuntimeError = rules[i].RuntimeError;

            _lastPreview = result;
            ChangeCount = result.Changed;
            ProblemCount = result.Problems;
            TotalCount = _all.Count;
            if (ShowOnlyChanged || FilterText.Length > 0) RefreshVisible();
            UpdateTrace();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            ReportError("The preview could not be updated", ex);
            ChangeCount = 0; // never rename from a preview that failed
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts) && !ct.IsCancellationRequested) IsPreviewPending = false;
            OnPropertyChanged(nameof(RenameButtonText));
        }
    }

    private void UpdateTrace()
    {
        _traceCts?.Cancel();
        var file = SelectedFile;
        if (file == null) { Trace.Clear(); return; }
        int idx = _all.IndexOf(file);
        if (idx < 0) { Trace.Clear(); return; }
        var cts = _traceCts = new CancellationTokenSource();
        var entries = _all.Select(f => f.Entry).ToList();
        var included = _all.Select(f => f.IsIncluded).ToList();
        var rules = Rules.Select(r => r.Clone()).ToList();
        var mode = Options.Mode;
        _ = BuildTraceAsync(file, entries, included, idx, rules, mode, cts.Token);
    }

    private async Task BuildTraceAsync(FileItemViewModel file, List<FileEntry> entries, List<bool> included, int idx,
        List<RenameRule> rules, BatchMode mode, CancellationToken ct)
    {
        try
        {
            // Off the UI thread: tokens may read metadata or hash a large file.
            var steps = await Task.Run(() => RenamePipeline.Trace(entries, included, idx, rules), ct);
            if (ct.IsCancellationRequested || SelectedFile != file) return;
            Trace.Clear();
            for (int i = 0; i < steps.Count; i++)
                Trace.Add(new TraceStepViewModel(i, steps[i].RuleTitle, steps[i].NameAfter, steps[i].Applied, i == 0));
            if (file.ChangesFolder)
                Trace.Add(new TraceStepViewModel(Trace.Count, mode == BatchMode.Copy ? "Copy to" : "Move to", file.TargetFolder!, true, false));
            if (file.PropertyChanges is { Length: > 0 } pc)
                Trace.Add(new TraceStepViewModel(Trace.Count, "Dates & attributes", pc, true, false));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("Step-by-step view failed", ex); }
    }

    // ---------- Rename / undo ----------
    [RelayCommand]
    private async Task RenameAsync()
    {
        if (IsBusy) return;
        // Never act on an out-of-date preview: wait for the latest one first.
        if (IsPreviewPending) await WaitForPreviewAsync();
        if (!CanRename || _lastPreview == null) return;

        var current = _all.Where(f => f.IsIncluded).Select(f => f.Entry).ToHashSet();
        var planned = _lastPreview.Items.Where(i => i.WillRename && current.Contains(i.File)).ToList();
        if (planned.Count == 0) return;
        var ops = planned.Select(p => new RenameOperation(p.File.FullPath, p.NewFullPath) { Kind = p.Kind, Properties = p.Properties }).ToList();
        var bySource = _all.Where(f => current.Contains(f.Entry))
            .GroupBy(f => f.Entry.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        IsBusy = true;
        Progress = 0;
        BatchRecord? record = null;
        try
        {
            // Write the plan first, so even a crash mid-batch can be undone from History.
            try { record = _journal.Begin(ops, DescribeRules()); }
            catch (Exception ex)
            {
                ReportError("History could not be written, so nothing was changed", ex);
                return;
            }

            var result = await RenameExecutor.ExecuteAsync(ops, new Progress<double>(p => Progress = p), default, record.Id);
            foreach (var r in result.Results.Where(r => r.Success))
            {
                if (!bySource.TryGetValue(r.Operation.Source, out var item)) continue;
                if (r.Operation.Kind is OperationKind.Rename or OperationKind.Move)
                {
                    _known.Remove(r.Operation.Source);
                    item.Entry.MovedTo(r.Operation.Target);
                    _known.Add(r.Operation.Target);
                }
                else item.Entry.Refresh();
                item.NotifyMoved();
            }

            try { _journal.Complete(record, result.Completed); }
            catch (Exception ex) { ReportError("The files were changed, but History could not be updated", ex); }
            _lastBatch = record.Entries.Count > 0 ? record : null;
            await RefreshHistoryAsync();

            var done = Options.Mode switch { BatchMode.Copy => "Copied", BatchMode.Move => "Moved", _ => "Renamed" };
            if (ops.All(o => o.Kind == OperationKind.Properties)) done = "Updated";
            var warning = result.Results.FirstOrDefault(r => r.Success && r.Error != null);
            if (result.Failed == 0 && warning == null)
                ShowNotice($"{done} {result.Succeeded:N0} item{(result.Succeeded == 1 ? "" : "s")}.", canUndo: result.Succeeded > 0);
            else if (result.Failed == 0)
                ShowNotice($"{done} {result.Succeeded:N0}. {Path.GetFileName(warning!.Operation.Target)}: {warning.Error}",
                    isError: true, canUndo: true, sticky: true);
            else
            {
                var first = result.Results.First(r => !r.Success);
                ShowNotice($"{done} {result.Succeeded:N0}, {result.Failed:N0} failed. {Path.GetFileName(first.Operation.Source)}: {first.Error}",
                    isError: true, canUndo: result.Succeeded > 0, sticky: true);
            }
        }
        catch (Exception ex)
        {
            ReportError("Something went wrong during the batch. Anything already changed can be undone from History", ex);
        }
        finally
        {
            IsBusy = false;
            RefreshVisible();
            SchedulePreview();
        }
    }

    [RelayCommand]
    private async Task UndoLastAsync()
    {
        if (_lastBatch == null) return;
        var vm = History.FirstOrDefault(h => h.Record.Id == _lastBatch.Id) ?? new HistoryItemViewModel(_lastBatch);
        await UndoBatchAsync(vm);
    }

    [RelayCommand]
    private async Task UndoBatchAsync(HistoryItemViewModel? item)
    {
        if (item == null || item.IsUndone || IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _journal.UndoAsync(item.Record, new Progress<double>(p => Progress = p));
            item.IsUndone = item.Record.Undone;
            var byPath = _all.GroupBy(f => f.Entry.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var r in result.Results.Where(r => r.Success))
            {
                if (r.Operation.Kind is OperationKind.Rename or OperationKind.Move && byPath.TryGetValue(r.Operation.Source, out var f))
                {
                    _known.Remove(r.Operation.Source);
                    f.Entry.MovedTo(r.Operation.Target);
                    _known.Add(r.Operation.Target);
                    f.NotifyMoved();
                }
            }
            foreach (var f in _all) f.Entry.Refresh();
            if (_lastBatch?.Id == item.Record.Id) _lastBatch = null;
            if (result.Failed == 0) ShowNotice($"Undone: {result.Succeeded:N0} item{(result.Succeeded == 1 ? "" : "s")} restored.");
            else ShowNotice($"Restored {result.Succeeded:N0}, {result.Failed:N0} not restored ({result.Results.First(r => !r.Success).Error}).", isError: true, sticky: true);
        }
        catch (Exception ex)
        {
            ReportError("Undo did not finish", ex);
        }
        finally
        {
            IsBusy = false;
            RefreshVisible();
            SchedulePreview();
        }
    }

    [RelayCommand]
    private async Task ToggleHistoryAsync()
    {
        if (!IsHistoryOpen) await RefreshHistoryAsync();
        IsHistoryOpen = !IsHistoryOpen;
    }

    [RelayCommand]
    private void OpenHistoryFolder()
    {
        try
        {
            Directory.CreateDirectory(_journal.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_journal.Folder) { UseShellExecute = true });
        }
        catch (Exception ex) { ReportError("Could not open the history folder", ex); }
    }

    private async Task RefreshHistoryAsync(bool checkInterrupted = false)
    {
        IReadOnlyList<BatchRecord> records;
        try { records = await Task.Run(() => _journal.List(50)); }
        catch (Exception ex) { AppLog.Write("Reading history failed", ex); return; }
        History.Clear();
        foreach (var r in records) History.Add(new HistoryItemViewModel(r));
        OnPropertyChanged(nameof(HasHistory));
        if (checkInterrupted && records.Any(r => r.InProgress && !r.Undone))
            ShowNotice("A previous batch was interrupted before it finished. Open History to undo it.", isError: true, sticky: true);
    }

    private string DescribeRules() =>
        string.Join(" → ", Rules.Where(r => r.IsEnabled).Select(r => r.Title));

    // ---------- Presets ----------
    [RelayCommand]
    private void SavePreset()
    {
        var name = NewPresetName.Trim();
        if (name.Length == 0) { ShowNotice("Type a name for the preset first.", isError: true); return; }
        try
        {
            _presets.Save(name, Rules, Options);
            NewPresetName = string.Empty;
            RefreshPresets();
            ShowNotice($"Saved preset “{name}”.");
        }
        catch (Exception ex) { ReportError("Could not save the preset", ex); }
    }

    [RelayCommand]
    private void LoadPreset(string? name)
    {
        if (name == null) return;
        Preset? preset;
        try { preset = _presets.Load(name); }
        catch (Exception ex) { ReportError($"The preset “{name}” could not be read", ex); return; }
        if (preset == null) return;
        Rules.Clear();
        foreach (var r in preset.Rules) Rules.Add(r);
        if (preset.Options != null) Options = preset.Options;
        SelectedRule = Rules.FirstOrDefault();
        ShowNotice($"Loaded preset “{name}”.");
    }

    [RelayCommand]
    private void DeletePreset(string? name)
    {
        if (name == null) return;
        try { _presets.Delete(name); }
        catch (Exception ex) { ReportError("Could not delete the preset", ex); }
        RefreshPresets();
    }

    private void RefreshPresets()
    {
        Presets.Clear();
        try { foreach (var n in _presets.Names()) Presets.Add(n); }
        catch (Exception ex) { AppLog.Write("Listing presets failed", ex); }
        OnPropertyChanged(nameof(HasPresets));
    }

    // ---------- Misc ----------
    [RelayCommand]
    private void ToggleTheme() => IsDark = !IsDark;

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    private void ApplyTheme()
    {
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    /// <summary>Logs a problem and tells the user in plain words.</summary>
    public void ReportError(string what, Exception? ex = null)
    {
        AppLog.Write(what, ex);
        ShowNotice(ex == null ? what + "." : $"{what}: {ex.Message}", isError: true, sticky: true);
    }

    /// <summary>Called when the window is asked to close; false while a batch is running.</summary>
    public bool CanClose()
    {
        if (!IsBusy) return true;
        ShowNotice("Proteus is still working on the batch. Please wait for it to finish before closing.", isError: true);
        return false;
    }

    private async void ShowNotice(string text, bool isError = false, bool canUndo = false, bool sticky = false)
    {
        try
        {
            _noticeCts?.Cancel();
            var cts = _noticeCts = new CancellationTokenSource();
            Notice = text;
            NoticeIsError = isError;
            NoticeCanUndo = canUndo;
            if (sticky) return;
            await Task.Delay(canUndo ? 9000 : 4000, cts.Token);
            if (Application.Current != null) Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) Notice = null; });
            else if (!cts.IsCancellationRequested) Notice = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("Showing a notice failed", ex); }
    }

    public void SaveSettings() => new AppSettings
    {
        Dark = IsDark,
        IncludeSubfolders = IncludeSubfolders,
        HighlightChanges = HighlightChanges,
        Rules = Rules.ToList(),
        Options = Options,
    }.Save();

    // ---------- CSV ----------
    [RelayCommand]
    private async Task ImportCsvAsync()
    {
        if (Ui == null) return;
        var path = await Ui.PickCsvAsync();
        if (path == null) return;
        List<(string Current, string New)> rows;
        try { rows = Csv.ReadRenameList(await File.ReadAllTextAsync(path)); }
        catch (Exception ex) { ReportError("Could not read that file", ex); return; }
        if (rows.Count == 0) { ShowNotice("No rows found. Each row needs the current file and its new name.", isError: true); return; }

        var baseDir = Path.GetDirectoryName(path) ?? string.Empty;
        var toAdd = new List<string>();
        var lines = new System.Text.StringBuilder();
        int folderParts = 0;
        foreach (var (current, newName) in rows)
        {
            var full = Path.IsPathRooted(current) ? current : Path.Combine(baseDir, current);
            if (File.Exists(full) || Directory.Exists(full))
            {
                full = Path.GetFullPath(full);
                if (!_known.Contains(full)) toAdd.Add(full);
                lines.Append(full);
            }
            else lines.Append(current);
            var nameOnly = Path.GetFileName(newName);
            if (nameOnly != newName) folderParts++;
            lines.Append('\t').Append(nameOnly).Append('\n');
        }
        int before = _all.Count;
        if (toAdd.Count > 0) await AddPathsAsync(toAdd, recurse: false, foldersAsItems: true);
        var rule = new ListRule { Mode = ListMode.Mapping, Lines = lines.ToString() };
        Rules.Add(rule);
        SelectedRule = rule;
        var note = folderParts > 0 ? $" {folderParts:N0} new name{(folderParts == 1 ? "" : "s")} included a folder; only the file name part is used (use Move to change folders)." : "";
        ShowNotice($"Imported {rows.Count:N0} name{(rows.Count == 1 ? "" : "s")} as a mapping rule; added {_all.Count - before:N0} file{(_all.Count - before == 1 ? "" : "s")} to the list.{note}");
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (Ui == null || _all.Count == 0) return;
        if (IsPreviewPending) await WaitForPreviewAsync();
        if (_lastPreview == null) return;
        var path = await Ui.SaveCsvAsync($"Proteus preview {DateTime.Now:yyyy-MM-dd HHmm}.csv");
        if (path == null) return;
        var inList = _all.Select(f => f.Entry).ToHashSet();
        var rows = new List<string[]> { new[] { "Current path", "New path", "Action", "Status", "Dates & attributes", "Message" } };
        foreach (var i in _lastPreview.Items.Where(i => inList.Contains(i.File)))
        {
            rows.Add([
                i.File.FullPath,
                i.WillRename || i.Status is ItemStatus.Conflict or ItemStatus.Invalid ? i.NewFullPath : i.File.FullPath,
                i.Status == ItemStatus.Update ? "Update" : i.Kind.ToString(),
                i.Status.ToString(),
                i.Properties?.Describe() ?? string.Empty,
                i.Message ?? string.Empty,
            ]);
        }
        try
        {
            await File.WriteAllTextAsync(path, Csv.Write(rows), new System.Text.UTF8Encoding(true));
            ShowNotice($"Exported {rows.Count - 1:N0} rows to {Path.GetFileName(path)}.");
        }
        catch (Exception ex) { ReportError("Could not write the file", ex); }
    }
}

/// <summary>Explorer-style ordering: "file2" before "file10".</summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        try { return StrCmpLogicalW(x, y); }
        catch (Exception) { return string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase); }
    }
}
