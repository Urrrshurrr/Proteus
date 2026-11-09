using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Proteus.Core.Models;
using Proteus.Core.Rules;

namespace Proteus.Core.Engine;

public enum BatchMode { Rename, Copy, Move }

public enum ConflictPolicy { Block, AddNumber }

public enum TimeSource { Keep, Now, Fixed, Created, Modified, Accessed, PhotoTaken, MediaRecorded, DocumentCreated, FromName }

public enum AttributeChange { Keep, Set, Clear }

/// <summary>How one of a file's three timestamps should change.</summary>
public sealed partial class TimestampSetting : ObservableObject
{
    [ObservableProperty] private TimeSource _source = TimeSource.Keep;
    [ObservableProperty] private DateTimeOffset? _fixedDate = DateTimeOffset.Now.Date;
    [ObservableProperty] private TimeSpan? _fixedTime = new TimeSpan(12, 0, 0);
    [ObservableProperty] private string _nameFormat = "yyyyMMdd_HHmmss";
    [ObservableProperty] private int _offsetMinutes;

    [JsonIgnore] public bool IsActive => Source != TimeSource.Keep;

    partial void OnSourceChanged(TimeSource value) => OnPropertyChanged(nameof(IsActive));

    /// <summary>The new time for this file, or null to leave it alone.</summary>
    public DateTime? Resolve(FileEntry f, DateTime now)
    {
        DateTime? t = Source switch
        {
            TimeSource.Keep => null,
            TimeSource.Now => now,
            TimeSource.Fixed => FixedDate is { } d ? d.Date + (FixedTime ?? TimeSpan.Zero) : null,
            TimeSource.Created => f.Created,
            TimeSource.Modified => f.Modified,
            TimeSource.Accessed => SafeAccessed(f.FullPath),
            TimeSource.PhotoTaken => f.Metadata.DateTaken,
            TimeSource.MediaRecorded => f.Metadata.Shell("System.Media.DateEncoded") as DateTime?,
            TimeSource.DocumentCreated => f.Metadata.Document("created") as DateTime?,
            TimeSource.FromName => FromName(f.BaseName),
            _ => null,
        };
        return t?.AddMinutes(OffsetMinutes);
    }

    private DateTime? FromName(string name)
    {
        if (string.IsNullOrWhiteSpace(NameFormat)) return null;
        foreach (var fmt in NameFormat.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var m = Regex.Match(name, @"(?<!\d)" + ReformatDateRule.FormatToRegex(fmt) + @"(?!\d)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
                if (m.Success && DateTime.TryParseExact(m.Value, fmt, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d))
                    return d;
            }
            catch (Exception) { }
        }
        return null;
    }

    private static DateTime? SafeAccessed(string path)
    {
        try { return File.GetLastAccessTime(path); }
        catch (Exception) { return null; }
    }
}

/// <summary>Everything about a batch that is not a rename rule: where results go and what else changes.</summary>
public sealed partial class BatchOptions : ObservableObject
{
    public BatchOptions()
    {
        Hook(_created);
        Hook(_modified);
        Hook(_accessed);
    }

    [ObservableProperty] private BatchMode _mode = BatchMode.Rename;
    [ObservableProperty] private string _destinationFolder = string.Empty;
    [ObservableProperty] private string _subfolderTemplate = string.Empty;
    [ObservableProperty] private ConflictPolicy _conflicts = ConflictPolicy.Block;

    private TimestampSetting _created = new();
    private TimestampSetting _modified = new();
    private TimestampSetting _accessed = new();

    public TimestampSetting Created { get => _created; set => Replace(ref _created, value, nameof(Created)); }
    public TimestampSetting Modified { get => _modified; set => Replace(ref _modified, value, nameof(Modified)); }
    public TimestampSetting Accessed { get => _accessed; set => Replace(ref _accessed, value, nameof(Accessed)); }

    [ObservableProperty] private AttributeChange _readOnly;
    [ObservableProperty] private AttributeChange _hidden;
    [ObservableProperty] private AttributeChange _archive;
    [ObservableProperty] private AttributeChange _system;

    [JsonIgnore] public bool ChangesTimes => Created.IsActive || Modified.IsActive || Accessed.IsActive;
    [JsonIgnore] public bool ChangesAttributes => ReadOnly != AttributeChange.Keep || Hidden != AttributeChange.Keep
                                                  || Archive != AttributeChange.Keep || System != AttributeChange.Keep;
    [JsonIgnore] public bool ChangesProperties => ChangesTimes || ChangesAttributes;
    [JsonIgnore] public bool MovesFiles => Mode != BatchMode.Rename;

    [JsonIgnore]
    public string ModeSummary => Mode switch
    {
        BatchMode.Copy => "Copy" + DestinationSuffix,
        BatchMode.Move => "Move" + DestinationSuffix,
        _ => "Rename in place",
    };

    [JsonIgnore]
    public string PropertiesSummary
    {
        get
        {
            var parts = new List<string>();
            if (ChangesTimes) parts.Add("dates");
            if (ChangesAttributes) parts.Add("attributes");
            return parts.Count == 0 ? "Dates & attributes" : "Change " + string.Join(" & ", parts);
        }
    }

    private string DestinationSuffix =>
        DestinationFolder.Length > 0 ? " to " + ShortPath(DestinationFolder) : SubfolderTemplate.Length > 0 ? " into subfolders" : " (choose a folder)";

    private static string ShortPath(string p)
    {
        var name = Path.GetFileName(p.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? p : name;
    }

    public (FileAttributes Set, FileAttributes Clear) AttributeMasks()
    {
        FileAttributes set = 0, clear = 0;
        void Add(AttributeChange c, FileAttributes a)
        {
            if (c == AttributeChange.Set) set |= a;
            else if (c == AttributeChange.Clear) clear |= a;
        }
        Add(ReadOnly, FileAttributes.ReadOnly);
        Add(Hidden, FileAttributes.Hidden);
        Add(Archive, FileAttributes.Archive);
        Add(System, FileAttributes.System);
        return (set, clear);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(ModeSummary) or nameof(PropertiesSummary) or nameof(ChangesProperties)
            or nameof(ChangesTimes) or nameof(ChangesAttributes) or nameof(MovesFiles)) return;
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ModeSummary)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(PropertiesSummary)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ChangesProperties)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ChangesTimes)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ChangesAttributes)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(MovesFiles)));
    }

    private void Replace(ref TimestampSetting field, TimestampSetting value, string name)
    {
        if (ReferenceEquals(field, value)) return;
        field.PropertyChanged -= OnChildChanged;
        field = value ?? new TimestampSetting();
        Hook(field);
        OnPropertyChanged(name);
    }

    private void Hook(TimestampSetting t) => t.PropertyChanged += OnChildChanged;

    private void OnChildChanged(object? sender, PropertyChangedEventArgs e) =>
        OnPropertyChanged(sender == _created ? nameof(Created) : sender == _modified ? nameof(Modified) : nameof(Accessed));
}
