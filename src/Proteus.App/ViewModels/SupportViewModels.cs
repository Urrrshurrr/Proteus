using CommunityToolkit.Mvvm.ComponentModel;
using Proteus.Core.History;
using Proteus.Core.Rules;
using Proteus.Core.Tokens;

namespace Proteus.App.ViewModels;

/// <summary>An entry in the "Add rule" menu.</summary>
public sealed record RuleKind(string Title, string Description, string Icon, Func<RenameRule> Create)
{
    public static readonly IReadOnlyList<RuleKind> All =
    [
        new("Replace", "Find text or a pattern and replace it", "⇄", () => new ReplaceRule()),
        new("Insert", "Add text at the start, end or anywhere", "+", () => new InsertRule()),
        new("Remove characters", "Delete characters by position", "−", () => new RemoveRule()),
        new("Change case", "lowercase, UPPERCASE, Title Case…", "Aa", () => new CaseRule()),
        new("Numbering", "Add a running number", "#", () => new NumberingRule()),
        new("New name", "Build the name from tokens like <name> and dates", "T", () => new TemplateRule()),
        new("Clean up", "Tidy separators, brackets, accents and spaces", "≈", () => new CleanupRule()),
        new("Extension", "Change, remove or re-case the extension", ".x", () => new ExtensionRule()),
        new("Pad numbers", "Zero-pad numbers, or remove leading zeros", "0#", () => new PadNumbersRule()),
        new("Rearrange", "Split at a separator and reorder the parts", "\u21C5", () => new RearrangeRule()),
        new("Trim around text", "Remove everything before, after or between markers", "[ ]", () => new CropRule()),
        new("Move", "Move characters or a piece of text elsewhere", "\u21E5", () => new MoveRule()),
        new("Reformat dates", "Find dates like 31.12.2024 and rewrite them", "31", () => new ReformatDateRule()),
        new("Transliterate", "Cyrillic, Greek, accents \u2192 plain Latin", "\u042F\u2192A", () => new TransliterateRule()),
        new("Randomize", "Add random letters or digits", "?!", () => new RandomizeRule()),
        new("JavaScript", "Write a small script for anything the rules can't do", "JS", () => new ScriptRule()),
        new("List of names", "Paste new names, or an old \u2192 new mapping", "\u2630", () => new ListRule()),
    ];
}

public sealed record TraceStepViewModel(int Number, string Title, string Name, bool Applied, bool IsOriginal);

public sealed record TokenInfoView(string Syntax, string Description);

public sealed record TokenGroup(string Name, IReadOnlyList<TokenInfoView> Tokens)
{
    public static readonly IReadOnlyList<TokenGroup> All =
        TokenEngine.Catalog.GroupBy(t => t.Group)
            .Select(g => new TokenGroup(g.Key.ToUpperInvariant(), g.Select(t => new TokenInfoView(t.Syntax, t.Description)).ToList()))
            .ToList();
}

public sealed record Option<T>(T Value, string Label);

public static class Ui
{
    /// <summary>The script editor shows its own error box; other rules use the shared one.</summary>
    public static readonly Avalonia.Data.Converters.FuncValueConverter<string?, bool> IsNotScript = new(icon => icon != "JS");

    /// <summary>CalendarDatePicker uses DateTime?; settings store DateTimeOffset?.</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter DateOffsetToDate = new DateOffsetConverter();

    private sealed class DateOffsetConverter : Avalonia.Data.Converters.IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is DateTimeOffset d ? d.Date : null;

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is DateTime d ? new DateTimeOffset(d.Date) : null;
    }

    public static readonly IReadOnlyList<Option<Proteus.Core.Engine.TimeSource>> TimeSources =
    [
        new(Proteus.Core.Engine.TimeSource.Keep, "Keep as is"),
        new(Proteus.Core.Engine.TimeSource.Fixed, "Set to a date and time"),
        new(Proteus.Core.Engine.TimeSource.Now, "Now"),
        new(Proteus.Core.Engine.TimeSource.PhotoTaken, "Photo date taken (EXIF)"),
        new(Proteus.Core.Engine.TimeSource.MediaRecorded, "Video / audio recorded date"),
        new(Proteus.Core.Engine.TimeSource.DocumentCreated, "Document created date"),
        new(Proteus.Core.Engine.TimeSource.FromName, "A date found in the file name"),
        new(Proteus.Core.Engine.TimeSource.Created, "Copy from Created"),
        new(Proteus.Core.Engine.TimeSource.Modified, "Copy from Modified"),
        new(Proteus.Core.Engine.TimeSource.Accessed, "Copy from Accessed"),
    ];

    public static readonly Avalonia.Data.Converters.FuncValueConverter<bool, double> AppliedOpacity = new(b => b ? 1.0 : 0.45);
}

public sealed partial class HistoryItemViewModel : ObservableObject
{
    public HistoryItemViewModel(BatchRecord record)
    {
        Record = record;
        _isUndone = record.Undone;
    }

    public BatchRecord Record { get; }
    [ObservableProperty] private bool _isUndone;

    /// <summary>Call after the record changed (for example after a partial undo).</summary>
    public void Refresh() => OnPropertyChanged(nameof(Title));

    public string When => Record.Timestamp.Date == DateTime.Today
        ? "Today, " + Record.Timestamp.ToString("t")
        : Record.Timestamp.ToString("g");

    public string Title
    {
        get
        {
            int n = Record.Entries.Count;
            var kinds = Record.Entries.Select(e => e.Kind).Distinct().ToList();
            var verb = kinds.Count != 1 ? "changed" : kinds[0] switch
            {
                Proteus.Core.Engine.OperationKind.Copy => "copied",
                Proteus.Core.Engine.OperationKind.Move => "moved",
                Proteus.Core.Engine.OperationKind.Properties => "updated",
                _ => "renamed",
            };
            var title = $"{n} item{(n == 1 ? "" : "s")} {verb}";
            if (Record.InProgress && !Record.PartlyUndone) title = title.Replace(verb, "planned") + " \u00B7 interrupted";
            if (Record.PartlyUndone && !Record.Undone)
            {
                int left = Record.Entries.Count(e => !e.Restored);
                title += $" \u00B7 partly undone, {left} left";
            }
            return title;
        }
    }
    public string Description => Record.Description;
    public string Example
    {
        get
        {
            var e = Record.Entries.FirstOrDefault();
            return e == null ? string.Empty : $"{Path.GetFileName(e.Source)}  →  {Path.GetFileName(e.Target)}";
        }
    }
}

public enum SortColumn { None, Original, New, Folder, Status }
