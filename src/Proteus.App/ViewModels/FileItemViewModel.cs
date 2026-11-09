using CommunityToolkit.Mvvm.ComponentModel;
using Proteus.Core.Engine;
using Proteus.Core.Models;

namespace Proteus.App.ViewModels;

public sealed partial class FileItemViewModel : ObservableObject
{
    public FileItemViewModel(FileEntry entry)
    {
        Entry = entry;
        _newName = entry.FileName;
    }

    public FileEntry Entry { get; }

    [ObservableProperty] private bool _isIncluded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChange))]
    private string _newName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel), nameof(IsOk), nameof(IsProblem), nameof(IsUnchanged), nameof(IsInvalid), nameof(HasChange))]
    private ItemStatus _status = ItemStatus.Unchanged;

    [ObservableProperty] private string? _message;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private OperationKind _kind = OperationKind.Rename;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderDisplay), nameof(ChangesFolder))]
    private string? _targetFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private string? _propertyChanges;

    public bool ChangesFolder => TargetFolder != null &&
        !string.Equals(TargetFolder.TrimEnd('\\'), Entry.Directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Folder column: the current folder, or where the item is going.</summary>
    public string FolderDisplay
    {
        get
        {
            if (!ChangesFolder) return FolderShort;
            var t = TargetFolder!;
            var rel = t.StartsWith(Entry.Directory, StringComparison.OrdinalIgnoreCase)
                ? t[Entry.Directory.Length..].TrimStart('\\')
                : Path.GetFileName(t.TrimEnd('\\'));
            return "\u2192 " + (rel.Length == 0 ? t : rel);
        }
    }

    public string OriginalName => Entry.FileName;
    public string Folder => Entry.Directory;
    public string FolderShort
    {
        get
        {
            var name = Path.GetFileName(Entry.Directory);
            return string.IsNullOrEmpty(name) ? Entry.Directory : name;
        }
    }
    public string TypeGlyph => Entry.IsFolder ? "" : "";
    public string Tooltip => Message ?? (PropertyChanges is { Length: > 0 } p ? Entry.FullPath + "\n" + p : Entry.FullPath);

    public bool HasChange => Status is ItemStatus.Ok or ItemStatus.Conflict or ItemStatus.Invalid or ItemStatus.Update;
    public bool IsOk => Status is ItemStatus.Ok or ItemStatus.Update;
    public bool IsProblem => Status == ItemStatus.Conflict;
    public bool IsInvalid => Status == ItemStatus.Invalid;
    public bool IsUnchanged => Status is ItemStatus.Unchanged or ItemStatus.Excluded;

    public string StatusLabel => Status switch
    {
        ItemStatus.Ok => Kind switch { OperationKind.Copy => "Copy", OperationKind.Move => "Move", _ => "Rename" },
        ItemStatus.Update => "Update",
        ItemStatus.Conflict => "Conflict",
        ItemStatus.Invalid => "Invalid",
        ItemStatus.Excluded => "Skipped",
        _ => "No change",
    };

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(Tooltip));

    public void NotifyMoved()
    {
        OnPropertyChanged(nameof(OriginalName));
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(FolderShort));
        OnPropertyChanged(nameof(FolderDisplay));
        OnPropertyChanged(nameof(ChangesFolder));
        OnPropertyChanged(nameof(Tooltip));
    }
}
