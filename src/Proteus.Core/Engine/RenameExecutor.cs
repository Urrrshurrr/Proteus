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

    /// <summary>Size and modified time of a copy right after it was made (files only).</summary>
    public long? CopySize { get; init; }
    public DateTime? CopyModified { get; init; }
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
                        EnsureParent(op.Target);
                        Copy(op.Source, op.Target);
                        results[i] = Fingerprint(Finish(op, op.Target, StampIfNeeded(op, op.Target)));
                        break;
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
                            Move(op.Source, op.Target);
                            results[i] = Finish(op, op.Target, before);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                TakeCreated();
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
                Move(temp, op.Target);
                results[index] = Finish(op, op.Target, before);
            }
            catch (Exception ex)
            {
                TakeCreated();
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
        if (!r.Success || !File.Exists(r.Operation.Target)) return r;
        try
        {
            var info = new FileInfo(r.Operation.Target);
            return r with { Operation = r.Operation with { CopySize = info.Length, CopyModified = info.LastWriteTimeUtc } };
        }
        catch (Exception) { return r; }
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

    internal static void Move(string from, string to)
    {
        if (Directory.Exists(from))
        {
            try
            {
                Directory.Move(from, to);
            }
            catch (IOException) when (!SameVolume(from, to))
            {
                // Different drive: copy, and only delete the original once the copy is complete.
                try { CopyDirectory(from, to); }
                catch (Exception)
                {
                    TryDelete(to);
                    throw;
                }
                Directory.Delete(from, recursive: true);
            }
        }
        else File.Move(from, to, overwrite: false);
    }

    private static void Copy(string from, string to)
    {
        if (Directory.Exists(from))
        {
            try { CopyDirectory(from, to); }
            catch (Exception)
            {
                TryDelete(to);
                throw;
            }
        }
        else File.Copy(from, to, overwrite: false);
    }

    private static void TryDelete(string partialCopy)
    {
        try { if (Directory.Exists(partialCopy)) Directory.Delete(partialCopy, recursive: true); }
        catch (Exception) { /* best effort */ }
    }

    private static void CopyDirectory(string from, string to)
    {
        if (Directory.Exists(to) || File.Exists(to)) throw new IOException($"\u201C{Path.GetFileName(to)}\u201D already exists.");
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            // Junctions and symbolic links to folders are not followed (they could loop forever).
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the path is on a drive that normally has a Recycle Bin (a local fixed disk).</summary>
    public static bool HasRecycleBin(string path)
    {
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
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };
        int rc = SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted) throw new IOException($"Could not move \u201C{Path.GetFileName(path)}\u201D to the Recycle Bin (code {rc}).");
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

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
