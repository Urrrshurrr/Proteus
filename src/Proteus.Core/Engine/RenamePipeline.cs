using System.Text;
using Proteus.Core.Models;
using Proteus.Core.Rules;
using Proteus.Core.Tokens;

namespace Proteus.Core.Engine;

public enum ItemStatus
{
    Unchanged,
    Ok,
    Conflict,
    Invalid,
    Excluded,
    /// <summary>Name and place stay; only dates or attributes change.</summary>
    Update,
}

/// <summary>Planned timestamps and attributes for one item (null = leave alone).</summary>
public sealed record PropertyChanges(DateTime? Created, DateTime? Modified, DateTime? Accessed,
    FileAttributes SetAttributes, FileAttributes ClearAttributes)
{
    public bool Any => Created != null || Modified != null || Accessed != null || SetAttributes != 0 || ClearAttributes != 0;

    public string Describe()
    {
        var parts = new List<string>();
        if (Created is { } c) parts.Add($"created {c:yyyy-MM-dd HH:mm}");
        if (Modified is { } m) parts.Add($"modified {m:yyyy-MM-dd HH:mm}");
        if (Accessed is { } a) parts.Add($"accessed {a:yyyy-MM-dd HH:mm}");
        if (SetAttributes != 0) parts.Add("+" + SetAttributes.ToString().Replace(", ", " +"));
        if (ClearAttributes != 0) parts.Add("−" + ClearAttributes.ToString().Replace(", ", " −"));
        return string.Join(", ", parts);
    }
}

public sealed record PreviewItem(FileEntry File, string NewName, ItemStatus Status, string? Message)
{
    /// <summary>Folder the item ends up in (its own folder unless copying or moving).</summary>
    public string TargetDirectory { get; init; } = File.Directory;
    public OperationKind Kind { get; init; } = OperationKind.Rename;
    public PropertyChanges? Properties { get; init; }

    public string NewFullPath => Path.Combine(TargetDirectory, NewName);
    public bool WillRename => Status is ItemStatus.Ok or ItemStatus.Update;
    public bool ChangesFolder => !string.Equals(TargetDirectory.TrimEnd('\\'), File.Directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}

public sealed record PreviewResult(IReadOnlyList<PreviewItem> Items)
{
    public int Changed => Items.Count(i => i.WillRename);
    public int Problems => Items.Count(i => i.Status is ItemStatus.Conflict or ItemStatus.Invalid);
}

public sealed record TraceStep(string RuleTitle, string NameAfter, bool Applied);

/// <summary>Runs the rule stack over the file list, works out destinations, and validates the result.</summary>
public static class RenamePipeline
{
    /// <param name="files">Files in display order.</param>
    /// <param name="included">Whether each file takes part (same length as <paramref name="files"/>).</param>
    public static PreviewResult Preview(IReadOnlyList<FileEntry> files, IReadOnlyList<bool> included,
        IReadOnlyList<RenameRule> rules, CancellationToken ct = default) =>
        Preview(files, included, rules, null, ct);

    public static PreviewResult Preview(IReadOnlyList<FileEntry> files, IReadOnlyList<bool> included,
        IReadOnlyList<RenameRule> rules, BatchOptions? options, CancellationToken ct = default)
    {
        var contexts = BuildContexts(files, included);
        var names = new string?[files.Count];
        var dirs = new string[files.Count];
        var props = new PropertyChanges?[files.Count];
        var problems = new string?[files.Count];
        var now = DateTime.Now;

        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            dirs[i] = files[i].Directory;
            if (contexts[i] is not { } ctx) continue;
            try
            {
                (names[i], problems[i]) = RunSafe(files[i], rules, ctx);
                if (options == null) continue;
                if (options.MovesFiles)
                {
                    var (dir, dirProblem) = TargetDirectorySafe(files[i], options, ctx);
                    dirs[i] = dir;
                    problems[i] ??= dirProblem;
                }
                if (options.ChangesProperties) props[i] = PlanProperties(files[i], options, now);
            }
            catch (Exception ex)
            {
                // Never let one file stop the whole preview.
                names[i] ??= files[i].FileName;
                problems[i] = "Could not work out the new name: " + ex.Message;
            }
        }
        var mode = options?.Mode ?? BatchMode.Rename;
        var policy = options?.Conflicts ?? ConflictPolicy.Block;
        return new PreviewResult(NameValidator.Validate(files, names, dirs, props, mode, policy, problems));
    }

    /// <summary>Planned date/attribute changes, leaving out anything that would not actually change.</summary>
    private static PropertyChanges? PlanProperties(FileEntry f, BatchOptions options, DateTime now)
    {
        static DateTime? IfDifferent(DateTime? wanted, DateTime current) =>
            wanted is { } w && Math.Abs((w - current).TotalSeconds) >= 1 ? w : null;

        var created = IfDifferent(options.Created.Resolve(f, now), f.Created);
        var modified = IfDifferent(options.Modified.Resolve(f, now), f.Modified);
        DateTime? accessed = null;
        if (options.Accessed.Resolve(f, now) is { } a)
        {
            try { accessed = IfDifferent(a, File.GetLastAccessTime(f.FullPath)); }
            catch (Exception) { accessed = a; }
        }

        var (set, clear) = options.AttributeMasks();
        if (set != 0 || clear != 0)
        {
            try
            {
                var current = File.GetAttributes(f.FullPath);
                set &= ~current;   // already on
                clear &= current;  // already off
            }
            catch (Exception) { }
        }
        var pc = new PropertyChanges(created, modified, accessed, set, clear);
        return pc.Any ? pc : null;
    }

    /// <summary>Destination folder for copy/move: the chosen folder (or the file's own) plus expanded subfolders.</summary>
    public static string TargetDirectory(FileEntry file, BatchOptions options, RuleContext ctx) =>
        TargetDirectorySafe(file, options, ctx).Directory;

    /// <summary>
    /// Works out the destination, always as a full path. A relative destination ("Sorted") is taken
    /// to mean a folder inside each file's own folder. Returns a problem when the destination can't be used.
    /// </summary>
    public static (string Directory, string? Problem) TargetDirectorySafe(FileEntry file, BatchOptions options, RuleContext ctx)
    {
        var (baseDir, problem) = ResolveDestination(options.DestinationFolder, file.Directory);
        if (problem != null || string.IsNullOrWhiteSpace(options.SubfolderTemplate)) return (baseDir, problem);
        // Backslashes are folder separators here, not token escapes.
        var expanded = TokenEngine.Expand(options.SubfolderTemplate.Replace('\\', '/'), ctx);
        var segments = expanded.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(SafeSegment).Where(s => s.Length > 0);
        return (Path.Combine([baseDir, .. segments]), null);
    }

    /// <summary>Turns what the user typed into a full folder path, or explains why it can't be used.</summary>
    public static (string Directory, string? Problem) ResolveDestination(string? destination, string fileDirectory)
    {
        var dest = (destination ?? string.Empty).Trim().Trim('"').Trim();
        if (dest.Length == 0) return (fileDirectory, null);
        int colon = dest.IndexOf(':');
        if (dest.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || (colon >= 0 && colon != 1) || dest.Any(c => c < 32))
            return (fileDirectory, "The destination folder contains characters Windows does not allow");
        try
        {
            var full = Path.GetFullPath(Path.IsPathFullyQualified(dest) ? dest : Path.Combine(fileDirectory, dest));
            if (File.Exists(full)) return (fileDirectory, "The destination is a file, not a folder");
            return (full, null);
        }
        catch (Exception ex)
        {
            return (fileDirectory, "The destination folder is not a valid path: " + ex.Message);
        }
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static string SafeSegment(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c < 32 || "<>:\"|?*".Contains(c) ? '_' : c);
        var seg = sb.ToString().Trim().TrimEnd('.').Trim();
        if (seg.Length > 120) seg = seg[..120].TrimEnd('.', ' ');
        if (ReservedNames.Contains(seg.Split('.')[0])) seg += "_";
        return seg;
    }

    /// <summary>The name of one file after each rule, for the step-through view.</summary>
    public static IReadOnlyList<TraceStep> Trace(IReadOnlyList<FileEntry> files, IReadOnlyList<bool> included,
        int fileIndex, IReadOnlyList<RenameRule> rules)
    {
        var contexts = BuildContexts(files, included);
        var ctx = contexts[fileIndex] ?? new RuleContext(files[fileIndex], 0, 0, 1);
        var file = files[fileIndex];
        var steps = new List<TraceStep>(rules.Count + 1) { new("Original", file.FileName, true) };
        var name = new NameParts(file.BaseName, file.Extension);
        foreach (var rule in rules)
        {
            var before = name;
            try
            {
                name = rule.Apply(name, ctx);
                steps.Add(new TraceStep(rule.Title, name.Full, rule.IsEnabled && rule.InScope(ctx) && before != name));
            }
            catch (Exception ex)
            {
                steps.Add(new TraceStep(rule.Title + " (failed)", ex.Message, false));
            }
        }
        return steps;
    }

    public static string Run(FileEntry file, IReadOnlyList<RenameRule> rules, RuleContext ctx) => RunSafe(file, rules, ctx).Name;

    /// <summary>Runs the rules; a rule that throws stops the chain and is reported instead of crashing.</summary>
    public static (string Name, string? Problem) RunSafe(FileEntry file, IReadOnlyList<RenameRule> rules, RuleContext ctx)
    {
        var name = new NameParts(file.BaseName, file.Extension);
        foreach (var rule in rules)
        {
            try
            {
                name = rule.Apply(name, ctx);
            }
            catch (Exception ex)
            {
                rule.RuntimeError = $"{ex.Message}";
                return (name.Full, $"The \u201C{rule.Title}\u201D rule failed: {ex.Message}");
            }
        }
        return (name.Full, null);
    }

    public static RuleContext?[] BuildContexts(IReadOnlyList<FileEntry> files, IReadOnlyList<bool> included)
    {
        var result = new RuleContext?[files.Count];
        int count = included.Count(x => x);
        var perFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        for (int i = 0; i < files.Count; i++)
        {
            if (!included[i]) continue;
            var dir = files[i].Directory;
            perFolder.TryGetValue(dir, out var fi);
            perFolder[dir] = fi + 1;
            result[i] = new RuleContext(files[i], index++, fi, count);
        }
        return result;
    }
}
