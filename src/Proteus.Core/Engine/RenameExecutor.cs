using System.Runtime.InteropServices;

namespace Proteus.Core.Engine;

public enum OperationKind { Rename, Move, Copy, Properties }

/// <summary>A file's timestamps and attributes at one moment, so they can be put back.</summary>
public sealed record FileStamp(DateTime Created, DateTime Modified, DateTime Accessed, FileAttributes Attributes)
{
    public static FileStamp Read(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return new FileStamp(info.CreationTime, info.LastWriteTime, info.LastAccessTime, info.Attributes);
    }

    public void ApplyTo(string path)
    {
        bool dir = Directory.Exists(path);
        var attrs = dir ? new DirectoryInfo(path).Attributes : File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0 && !dir) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        SetTimes(path, dir, Created, Modified, Accessed);
        if (dir) new DirectoryInfo(path).Attributes = Attributes;
        else File.SetAttributes(path, Attributes);
    }

    internal static void SetTimes(string path, bool dir, DateTime? created, DateTime? modified, DateTime? accessed)
    {
        if (dir)
        {
            if (created is { } c) Directory.SetCreationTime(path, c);
            if (modified is { } m) Directory.SetLastWriteTime(path, m);
            if (accessed is { } a) Directory.SetLastAccessTime(path, a);
        }
        else
        {
            if (created is { } c) File.SetCreationTime(path, c);
            if (modified is { } m) File.SetLastWriteTime(path, m);
            if (accessed is { } a) File.SetLastAccessTime(path, a);
        }
    }
}

/// <summary>
/// One step of a batch. The executor fills in <see cref="Before"/> (dates/attributes it changed),
/// <see cref="CreatedFolders"/> and, for copies, a fingerprint so undo can tell whether the copy
/// has been changed since.
/// </summary>
public sealed record RenameOperation(string Source, string Target)
{
    public OperationKind Kind { get; init; } = OperationKind.Rename;
    public PropertyChanges? Properties { get; init; }
    public FileStamp? Before { get; init; }

    /// <summary>Folders the executor had to create for this item, so undo can remove them again.</summary>
    public List<string>? CreatedFolders { get; init; }

    /// <summary>
    /// What a copy looked like right after it was made, so undo can tell whether it changed since.
    /// A file: its size and modified time. A folder: the total size of its files, the newest modified
    /// time inside it and (in <see cref="CopyCount"/>) how many files and folders it holds.
    /// </summary>
    public long? CopySize { get; init; }
    public DateTime? CopyModified { get; init; }
    public int? CopyCount { get; init; }

    /// <summary>True once undo has put this item back. A partly undone batch keeps the rest to retry.</summary>
    public bool Restored { get; init; }
}

public sealed record OperationResult(RenameOperation Operation, bool Success, string? Error);

public sealed record BatchResult(IReadOnlyList<OperationResult> Results, bool Cancelled)
{
    public int Succeeded => Results.Count(r => r.Success);
    public int Failed => Results.Count(r => !r.Success);
    public IEnumerable<RenameOperation> Completed => Results.Where(r => r.Success).Select(r => r.Operation);
}

/// <summary>
/// Performs renames, moves and copies safely. Any item whose target is currently held by another
/// item in the batch (swaps, cycles, shifting numbers) and any case-only rename goes through a
/// temporary name first, so no file is ever overwritten. Date and attribute changes are applied
/// after the file is in place, and the previous values are recorded for undo.
/// </summary>
public static class RenameExecutor
{
    /// <summary>Temporary name used for item <paramref name="index"/> of batch <paramref name="batchTag"/>.</summary>
    public static string TempPath(string source, string batchTag, int index) =>
        Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, $".proteus-{batchTag}-{index}.tmp");

    public static Task<BatchResult> ExecuteAsync(IReadOnlyList<RenameOperation> ops,
        IProgress<double>? progress = null, CancellationToken ct = default, string? batchTag = null) =>
        Task.Run(() => Execute(ops, progress, ct, batchTag), CancellationToken.None);

    /// <param name="batchTag">
    /// Used in temporary names (".proteus-{tag}-{index}.tmp") so an interrupted batch can be
    /// recovered from its journal entry. A random tag is used when null.
    /// </param>
    public static BatchResult Execute(IReadOnlyList<RenameOperation> ops,
        IProgress<double>? progress = null, CancellationToken ct = default, string? batchTag = null)
    {
        batchTag ??= Guid.NewGuid().ToString("N");
        _created = null;
        var results = new OperationResult?[ops.Count];
        var sources = new HashSet<string>(
            ops.Where(o => o.Kind is OperationKind.Rename or OperationKind.Move).Select(o => o.Source),
            StringComparer.OrdinalIgnoreCase);
        var staged = new List<(int Index, string Temp, FileStamp? Before)>();
        int done = 0;
        int total = ops.Count;
        bool cancelled = false;

        void Report() => progress?.Report(Math.Min(1.0, (double)++done / Math.Max(1, total + staged.Count)));

        // Phase 1: copies, property-only changes, direct moves; or move into a temporary name.
        for (int i = 0; i < ops.Count; i++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            var op = ops[i];
            try
            {
                if (!Path.IsPathFullyQualified(op.Source) || !Path.IsPathFullyQualified(op.Target))
                    throw new ArgumentException("Proteus only works with full paths (like C:\\folder\\file).");

                switch (op.Kind)
                {
                    case OperationKind.Properties:
                        results[i] = Finish(op, op.Target, StampIfNeeded(op, op.Target));
                        break;
                    case OperationKind.Copy:
                    {
                        EnsureParent(op.Target);
                        var warning = Copy(op.Source, op.Target);
                        results[i] = WithWarning(Fingerprint(Finish(op, op.Target, StampIfNeeded(op, op.Target))), warning);
                        break;
                    }
                    default:
                    {
                        // Read the old dates before moving: a move across drives changes them.
                        var before = StampIfNeeded(op, op.Source);
                        bool caseOnly = string.Equals(op.Source, op.Target, StringComparison.OrdinalIgnoreCase);
                        bool targetHeld = !caseOnly && sources.Contains(op.Target);
                        if (caseOnly || targetHeld)
                        {
                            var temp = TempPath(op.Source, batchTag, i);
                            Move(op.Source, temp);
                            staged.Add((i, temp, before));
                        }
                        else
                        {
                            EnsureParent(op.Target);
                            var warning = Move(op.Source, op.Target);
                            results[i] = WithWarning(Finish(op, op.Target, before), warning);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                RemoveEmpty(TakeCreated());
                results[i] = new OperationResult(op, false, ex.Message);
            }
            Report();
        }

        // Phase 2: temporary names to their final names. Always runs, even after cancel,
        // so no file is left with a temporary name.
        foreach (var (index, temp, before) in staged)
        {
            var op = ops[index];
            try
            {
                EnsureParent(op.Target);
                var warning = Move(temp, op.Target);
                results[index] = WithWarning(Finish(op, op.Target, before), warning);
            }
            catch (Exception ex)
            {
                RemoveEmpty(TakeCreated());
                string error = ex.Message;
                try { Move(temp, op.Source); }
                catch (Exception) { error += $" (file left as {Path.GetFileName(temp)})"; }
                results[index] = new OperationResult(op, false, error);
            }
            Report();
        }

        var final = new List<OperationResult>(ops.Count);
        for (int i = 0; i < ops.Count; i++)
            if (results[i] is { } r) final.Add(r);
        progress?.Report(1);
        return new BatchResult(final, cancelled);
    }

    private static FileStamp? StampIfNeeded(RenameOperation op, string path)
    {
        if (op.Properties is not { Any: true }) return null;
        try { return FileStamp.Read(path); }
        catch (Exception) { return null; }
    }

    /// <summary>Applies date/attribute changes to the item at its final path.</summary>
    private static OperationResult Finish(RenameOperation op, string path, FileStamp? before)
    {
        if (TakeCreated() is { } created) op = op with { CreatedFolders = created };
        if (op.Properties is not { Any: true } pc) return new OperationResult(op, true, null);
        try
        {
            bool dir = Directory.Exists(path);
            var attrs = dir ? new DirectoryInfo(path).Attributes : File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0 && !dir) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            FileStamp.SetTimes(path, dir, pc.Created, pc.Modified, pc.Accessed);
            var newAttrs = (attrs | pc.SetAttributes) & ~pc.ClearAttributes;
            if (dir) new DirectoryInfo(path).Attributes = newAttrs;
            else File.SetAttributes(path, newAttrs);
            return new OperationResult(op with { Before = before }, true, null);
        }
        catch (Exception ex)
        {
            // The file itself was renamed/copied; only the dates/attributes failed.
            var recorded = op with { Before = before };
            return op.Kind == OperationKind.Properties
                ? new OperationResult(recorded, false, ex.Message)
                : new OperationResult(recorded, true, "Renamed, but dates/attributes could not be set: " + ex.Message);
        }
    }

    private static OperationResult Fingerprint(OperationResult r)
    {
        if (!r.Success) return r;
        try
        {
            var target = r.Operation.Target;
            if (Directory.Exists(target))
            {
                var (count, size, newest) = FolderFingerprint(target);
                return r with { Operation = r.Operation with { CopySize = size, CopyModified = newest, CopyCount = count } };
            }
            if (!File.Exists(target)) return r;
            var info = new FileInfo(target);
            return r with { Operation = r.Operation with { CopySize = info.Length, CopyModified = info.LastWriteTimeUtc } };
        }
        catch (Exception) { return r; }
    }

    private static OperationResult WithWarning(OperationResult r, string? warning) =>
        warning == null || !r.Success ? r : r with { Error = r.Error == null ? warning : warning + " " + r.Error };

    /// <summary>After a failed item, removes the folders made for it again if they are still empty.</summary>
    private static void RemoveEmpty(List<string>? created)
    {
        if (created == null) return;
        for (int i = created.Count - 1; i >= 0; i--)
        {
            try { if (Directory.Exists(created[i]) && !Directory.EnumerateFileSystemEntries(created[i]).Any()) Directory.Delete(created[i]); }
            catch (Exception) { return; }
        }
    }

    [ThreadStatic] private static List<string>? _created;

    private static void EnsureParent(string target)
    {
        var parent = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(parent) || Directory.Exists(parent)) return;
        if (File.Exists(parent)) throw new IOException($"\u201C{parent}\u201D is a file, not a folder.");
        // Remember every level we create (outermost first).
        var missing = new Stack<string>();
        for (var d = parent; !string.IsNullOrEmpty(d) && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Push(d);
        Directory.CreateDirectory(parent);
        _created ??= [];
        _created.AddRange(missing);
    }

    private static List<string>? TakeCreated()
    {
        var c = _created;
        _created = null;
        return c is { Count: > 0 } ? c : null;
    }

    /// <summary>
    /// Moves a file or folder without overwriting anything. Returns a warning when the move worked
    /// but something needs the user's attention, or null.
    /// </summary>
    internal static string? Move(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            File.Move(from, to, overwrite: false);
            return null;
        }
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && (Directory.Exists(to) || File.Exists(to)))
            throw new IOException($"\u201C{Path.GetFileName(to)}\u201D already exists.");
        if (IsInside(to, from)) throw new IOException("A folder cannot go inside itself.");
        try
        {
            Directory.Move(from, to);
            return null;
        }
        catch (IOException) when (!SameVolume(from, to))
        {
            // Different drive: copy, and only delete the original once the copy is complete.
            if (HasFolderLinks(from))
                throw new IOException("This folder contains folder links (junctions or symbolic links), which Proteus cannot move to another drive. Move it in Explorer instead.");
            bool created = false;
            try { CopyDirectory(from, to, [], ref created); }
            catch (Exception)
            {
                // Only ever remove a folder this call created, never one that was already there.
                if (created) TryDeleteTree(to);
                throw;
            }
            try { DeleteTree(from); }
            catch (Exception ex)
            {
                return $"Moved, but the original folder could not be removed completely ({ex.Message}). What is left of it is still at {from}.";
            }
            return null;
        }
    }

    /// <summary>Copies a file or folder without overwriting anything. Returns a warning, or null.</summary>
    private static string? Copy(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            File.Copy(from, to, overwrite: false);
            return null;
        }
        if (IsInside(to, from)) throw new IOException("A folder cannot go inside itself.");
        var skipped = new List<string>();
        bool created = false;
        try { CopyDirectory(from, to, skipped, ref created); }
        catch (Exception)
        {
            // Only ever remove a folder this call created, never one that was already there.
            if (created) TryDeleteTree(to);
            throw;
        }
        if (skipped.Count == 0) return null;
        var names = string.Join(", ", skipped.Take(3).Select(Path.GetFileName));
        return $"Copied, but {skipped.Count} folder link{(skipped.Count == 1 ? " was" : "s were")} not copied ({names}{(skipped.Count > 3 ? ", …" : "")}).";
    }

    /// <summary>
    /// Copies a folder to a destination that must not exist yet. <paramref name="created"/> turns true
    /// as soon as the destination folder has been made, so a failed copy knows whether it may clean up.
    /// </summary>
    private static void CopyDirectory(string from, string to, List<string> skippedLinks, ref bool created)
    {
        if (Directory.Exists(to) || File.Exists(to)) throw new IOException($"\u201C{Path.GetFileName(to)}\u201D already exists.");
        Directory.CreateDirectory(to);
        created = true;
        CopyContents(from, to, skippedLinks);
    }

    private static void CopyContents(string from, string to, List<string> skippedLinks)
    {
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            // Junctions and symbolic links to folders are not followed (they could loop forever).
            if (IsLink(dir)) { skippedLinks.Add(dir); continue; }
            var sub = Path.Combine(to, Path.GetFileName(dir));
            Directory.CreateDirectory(sub);
            CopyContents(dir, sub, skippedLinks);
        }
    }

    private static bool IsLink(string dir) => (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0;

    private static bool HasFolderLinks(string dir)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir))
            if (IsLink(sub) || HasFolderLinks(sub)) return true;
        return false;
    }

    /// <summary>Deletes a folder tree, clearing read-only flags first (every .git folder has some).</summary>
    internal static void DeleteTree(string dir)
    {
        ClearReadOnly(dir);
        Directory.Delete(dir, recursive: true);
    }

    private static void ClearReadOnly(string dir)
    {
        var info = new DirectoryInfo(dir);
        if ((info.Attributes & FileAttributes.ReadOnly) != 0) info.Attributes &= ~FileAttributes.ReadOnly;
        foreach (var file in info.EnumerateFiles())
            if ((file.Attributes & FileAttributes.ReadOnly) != 0) file.Attributes &= ~FileAttributes.ReadOnly;
        foreach (var sub in info.EnumerateDirectories())
            if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) ClearReadOnly(sub.FullName);
    }

    private static void TryDeleteTree(string partialCopy)
    {
        try { if (Directory.Exists(partialCopy)) DeleteTree(partialCopy); }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>True when <paramref name="path"/> lies strictly inside <paramref name="folder"/>.</summary>
    internal static bool IsInside(string path, string folder)
    {
        var f = folder.TrimEnd('\\', '/');
        return path.Length > f.Length + 1 && path.StartsWith(f, StringComparison.OrdinalIgnoreCase) && path[f.Length] is '\\' or '/';
    }

    /// <summary>
    /// What a folder looks like now: how many files and folders it holds, their total size and the
    /// newest modified time. Used to tell whether a copied folder has been changed since.
    /// </summary>
    internal static (int Count, long Size, DateTime Newest) FolderFingerprint(string dir)
    {
        int count = 0;
        long size = 0;
        var newest = DateTime.MinValue;
        void Walk(DirectoryInfo d)
        {
            foreach (var entry in d.EnumerateFileSystemInfos())
            {
                count++;
                if (entry.LastWriteTimeUtc > newest) newest = entry.LastWriteTimeUtc;
                if (entry is FileInfo fi) size += fi.Length;
                else if (entry is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0) Walk(sub);
            }
        }
        Walk(new DirectoryInfo(dir));
        return (count, size, newest);
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the path is on a drive that normally has a Recycle Bin (a local fixed disk).</summary>
    public static bool HasRecycleBin(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Sends a file or folder to the Recycle Bin (used to undo a copy).</summary>
    public static void Recycle(string path)
    {
        if (!HasRecycleBin(path))
            throw new IOException("This drive has no Recycle Bin, so Proteus left the copy in place. Delete it yourself if you no longer need it.");
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            // FOF_WANTNUKEWARNING: if the item is too big for the Recycle Bin, Windows asks before
            // deleting it for good instead of doing so silently. Declining leaves the copy in place.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING,
        };
        int rc = SHFileOperation(ref op);
        if (op.fAnyOperationsAborted) throw new IOException("Left in place: it was not sent to the Recycle Bin.");
        if (rc != 0) throw new IOException($"Could not move \u201C{Path.GetFileName(path)}\u201D to the Recycle Bin (code {rc}).");
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

    // Layout matches the x64 Windows headers; the app is built for x64 only.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
