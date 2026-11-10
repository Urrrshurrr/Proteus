using Proteus.Core.Metadata;

namespace Proteus.Core.Models;

/// <summary>A file or folder that has been added to the rename list.</summary>
public sealed class FileEntry
{
    private FileMetadata? _metadata;

    public FileEntry(string fullPath)
    {
        FullPath = Path.GetFullPath(fullPath);
        IsFolder = System.IO.Directory.Exists(FullPath);
        Refresh();
    }

    public string FullPath { get; private set; }
    public bool IsFolder { get; }
    public string Directory => Path.GetDirectoryName(FullPath) ?? string.Empty;
    public string FileName => Path.GetFileName(FullPath);

    /// <summary>
    /// Name without extension. Folders have no extension, and neither do dot-files such as
    /// ".gitignore": the whole name is the name.
    /// </summary>
    public string BaseName => IsFolder || IsDotFile ? FileName : Path.GetFileNameWithoutExtension(FullPath);

    /// <summary>Extension including the leading dot, or empty.</summary>
    public string Extension => IsFolder || IsDotFile ? string.Empty : Path.GetExtension(FullPath);

    private bool IsDotFile => FileName.LastIndexOf('.') == 0;

    public long Size { get; private set; }
    public DateTime Created { get; private set; }
    public DateTime Modified { get; private set; }

    public FileMetadata Metadata => _metadata ??= new FileMetadata(this);

    public void Refresh()
    {
        try
        {
            FileSystemInfo info = IsFolder ? new DirectoryInfo(FullPath) : new FileInfo(FullPath);
            Created = info.CreationTime;
            Modified = info.LastWriteTime;
            Size = info is FileInfo fi && fi.Exists ? fi.Length : 0;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Called after a successful rename so the entry tracks the file.</summary>
    public void MovedTo(string newFullPath)
    {
        FullPath = newFullPath;
        _metadata = null;
        Refresh();
    }

    public override string ToString() => FullPath;
}
