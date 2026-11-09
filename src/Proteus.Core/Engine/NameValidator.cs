using Proteus.Core.Models;

namespace Proteus.Core.Engine;

/// <summary>Checks proposed names against Windows rules and against each other.</summary>
public static class NameValidator
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const string InvalidChars = "<>:\"/\\|?*";

    /// <summary>Returns a problem description, or null if the name is acceptable.</summary>
    public static string? CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name is empty";
        foreach (var c in name)
        {
            if (c < 32) return "Contains a control character";
            if (InvalidChars.Contains(c)) return $"Contains “{c}”, which Windows does not allow";
        }
        if (name.EndsWith('.') || name.EndsWith(' ')) return "Ends with a dot or space";
        if (name.Length > 255) return "Longer than 255 characters";
        var stem = name.Split('.')[0].TrimEnd();
        if (Reserved.Contains(stem)) return $"“{stem}” is a reserved name in Windows";
        return null;
    }

    /// <summary>Rename in place, no property changes.</summary>
    public static IReadOnlyList<PreviewItem> Validate(IReadOnlyList<FileEntry> files, IReadOnlyList<string?> newNames) =>
        Validate(files, newNames, files.Select(f => f.Directory).ToList(), new PropertyChanges?[files.Count],
            BatchMode.Rename, ConflictPolicy.Block);

    public static IReadOnlyList<PreviewItem> Validate(IReadOnlyList<FileEntry> files, IReadOnlyList<string?> newNames,
        IReadOnlyList<string> targetDirs, IReadOnlyList<PropertyChanges?> props, BatchMode mode, ConflictPolicy policy,
        IReadOnlyList<string?>? problems = null)
    {
        var kind = mode switch { BatchMode.Copy => OperationKind.Copy, BatchMode.Move => OperationKind.Move, _ => OperationKind.Rename };
        var items = new PreviewItem[files.Count];
        bool vacates = mode != BatchMode.Copy; // copies leave the original in place

        string Target(int i) => Path.Combine(targetDirs[i], newNames[i]!);
        bool SamePlace(int i) => string.Equals(Target(i), files[i].FullPath, StringComparison.Ordinal);

        // Paths currently held by items that will move away are free to take (rename / move only).
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (vacates)
            for (int i = 0; i < files.Count; i++)
                if (newNames[i] != null && !SamePlace(i)) sources.Add(files[i].FullPath);

        bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

        // With "add a number", resolve clashes up front by giving later items "name (2).ext" and so on.
        var finalNames = newNames.ToArray();
        if (policy == ConflictPolicy.AddNumber)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Count; i++)
            {
                if (finalNames[i] == null) continue;
                if (vacates && SamePlace(i)) { taken.Add(files[i].FullPath); continue; }
                var name = finalNames[i]!;
                var dir = targetDirs[i];
                var path = Path.Combine(dir, name);
                bool caseOnly = vacates && string.Equals(path, files[i].FullPath, StringComparison.OrdinalIgnoreCase);
                int n = 2;
                while (!caseOnly && (taken.Contains(path) || (Exists(path) && !sources.Contains(path))) && n < 10000)
                {
                    var stem = files[i].IsFolder ? name : Path.GetFileNameWithoutExtension(name);
                    var ext = files[i].IsFolder ? string.Empty : Path.GetExtension(name);
                    path = Path.Combine(dir, $"{stem} ({n++}){ext}");
                }
                finalNames[i] = Path.GetFileName(path);
                taken.Add(path);
            }
        }

        var targetsCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Count; i++)
        {
            if (finalNames[i] == null) continue;
            var t = Path.Combine(targetDirs[i], finalNames[i]!);
            targetsCount[t] = targetsCount.GetValueOrDefault(t) + 1;
        }

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var nn = finalNames[i];
            var dir = targetDirs[i];
            if (nn == null)
            {
                items[i] = new PreviewItem(file, file.FileName, ItemStatus.Excluded, null);
                continue;
            }
            var target = Path.Combine(dir, nn);
            bool samePlace = string.Equals(target, file.FullPath, StringComparison.Ordinal);
            var pc = props[i];

            if (problems?[i] is { } early)
            {
                items[i] = new PreviewItem(file, nn, ItemStatus.Invalid, early) { TargetDirectory = dir, Kind = kind, Properties = pc };
                continue;
            }

            if (samePlace && mode != BatchMode.Copy)
            {
                items[i] = pc?.Any == true
                    ? new PreviewItem(file, nn, ItemStatus.Update, null) { Kind = OperationKind.Properties, Properties = pc }
                    : new PreviewItem(file, nn, ItemStatus.Unchanged, null);
                continue;
            }

            var item = new PreviewItem(file, nn, ItemStatus.Ok, null) { TargetDirectory = dir, Kind = kind, Properties = pc };

            var problem = problems?[i] ?? CheckName(nn);
            if (problem != null) { items[i] = item with { Status = ItemStatus.Invalid, Message = problem }; continue; }

            if (mode == BatchMode.Copy && string.Equals(target, file.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                items[i] = item with { Status = ItemStatus.Conflict, Message = "A copy needs a different name or folder" };
                continue;
            }

            if (targetsCount[target] > 1)
            {
                items[i] = item with { Status = ItemStatus.Conflict, Message = "Another file would get the same name" };
                continue;
            }

            bool caseOnly = vacates && string.Equals(target, file.FullPath, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && !sources.Contains(target) && Exists(target))
            {
                items[i] = item with { Status = ItemStatus.Conflict, Message = "A file with this name already exists" };
                continue;
            }

            if (file.IsFolder && mode != BatchMode.Rename &&
                (target + "\\").StartsWith(file.FullPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                items[i] = item with { Status = ItemStatus.Invalid, Message = "A folder cannot go inside itself" };
                continue;
            }

            items[i] = item;
        }

        // A folder and something inside it can't both move in one batch: the inner path changes
        // the moment the folder is renamed, so the result would depend on list order.
        if (vacates)
        {
            var movingFolders = items.Where(it => it.Status == ItemStatus.Ok && it.File.IsFolder)
                .Select(it => it.File.FullPath.TrimEnd('\\') + "\\").ToList();
            if (movingFolders.Count > 0)
                for (int i = 0; i < items.Length; i++)
                {
                    var it = items[i];
                    if (it.Status is not (ItemStatus.Ok or ItemStatus.Update)) continue;
                    if (movingFolders.Any(f => it.File.FullPath.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                        items[i] = it with { Status = ItemStatus.Conflict, Message = "Its folder is also being renamed in this batch. Do the folder and its contents in separate batches." };
                }
        }

        // An item may only take a path freed by another item if that other item really moves.
        // Repeat until stable, since blocking one item can block the item waiting on it.
        bool changed = vacates;
        while (changed)
        {
            changed = false;
            var staying = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
                if (it.Status is not (ItemStatus.Ok)) staying.Add(it.File.FullPath);
            for (int i = 0; i < items.Length; i++)
            {
                var it = items[i];
                if (it.Status != ItemStatus.Ok) continue;
                var target = it.NewFullPath;
                if (string.Equals(target, it.File.FullPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (staying.Contains(target))
                {
                    items[i] = it with { Status = ItemStatus.Conflict, Message = "Would overwrite a file that is not being renamed" };
                    changed = true;
                }
            }
        }
        return items;
    }
}
