using Proteus.Core.Models;

namespace Proteus.Core.Rules;

/// <summary>Everything a rule may need to know about the file it is renaming.</summary>
public sealed class RuleContext
{
    public RuleContext(FileEntry file, int index, int folderIndex, int count)
    {
        File = file;
        Index = index;
        FolderIndex = folderIndex;
        Count = count;
    }

    public FileEntry File { get; }

    /// <summary>0-based position among the files being renamed.</summary>
    public int Index { get; }

    /// <summary>0-based position among the files being renamed in the same folder.</summary>
    public int FolderIndex { get; }

    public int Count { get; }

    /// <summary>The moment the preview started; <c>&lt;now&gt;</c> uses it so every file gets the same time.</summary>
    public DateTime Now { get; init; } = DateTime.Now;

    /// <summary>Every file's context in list order (null for files not taking part), when known.</summary>
    public IReadOnlyList<RuleContext?>? All { get; init; }
}

/// <summary>The name being built up as rules run: base name and extension (with dot).</summary>
public record struct NameParts(string Base, string Extension)
{
    public string Full => Base + Extension;

    public static NameParts FromFull(string full, bool isFolder)
    {
        if (isFolder) return new NameParts(full, string.Empty);
        int dot = full.LastIndexOf('.');
        return dot <= 0 ? new NameParts(full, string.Empty) : new NameParts(full[..dot], full[dot..]);
    }
}
