using System.Text.Json;
using System.Text.Json.Serialization;
using Proteus.Core.Engine;

namespace Proteus.Core.History;

public sealed class BatchRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Description { get; set; } = string.Empty;
    public List<RenameOperation> Entries { get; set; } = [];
    public bool Undone { get; set; }

    /// <summary>
    /// True while the batch runs. If Proteus stops mid-batch the record stays "in progress":
    /// <see cref="Entries"/> then holds the whole plan, in order, and undo checks each item.
    /// </summary>
    public bool InProgress { get; set; }

    /// <summary>Some items were put back and some could not be; those entries are marked <c>Restored</c>.</summary>
    public bool PartlyUndone { get; set; }
}

/// <summary>Keeps every batch on disk so it can be reverted later, even after a restart or a crash.</summary>
public sealed class UndoJournal
{
    public const int MaxRecords = 500;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string _folder;

    public UndoJournal(string? folder = null) => _folder = folder ?? AppPaths.History;

    public string Folder => _folder;

    /// <summary>How undo removes a copy it made. Recycle Bin by default; tests swap in a plain delete.</summary>
    public static Action<string> RemoveCopy { get; set; } = RenameExecutor.Recycle;

    /// <summary>Writes the plan before anything is touched, so an interrupted batch can still be undone.</summary>
    public BatchRecord Begin(IReadOnlyList<RenameOperation> planned, string description)
    {
        var record = new BatchRecord { Description = description, Entries = planned.ToList(), InProgress = true };
        Save(record);
        return record;
    }

    /// <summary>Replaces the plan with what actually happened.</summary>
    public void Complete(BatchRecord record, IEnumerable<RenameOperation> completed)
    {
        record.Entries = completed.ToList();
        record.InProgress = false;
        if (record.Entries.Count == 0) Delete(record);
        else Save(record);
        Prune();
    }

    /// <summary>Records a finished batch in one step.</summary>
    public BatchRecord Record(IEnumerable<RenameOperation> completed, string description)
    {
        var record = new BatchRecord { Description = description, Entries = completed.ToList() };
        if (record.Entries.Count > 0) Save(record);
        Prune();
        return record;
    }

    public IReadOnlyList<BatchRecord> List(int max = 100)
    {
        if (!Directory.Exists(_folder)) return [];
        var list = new List<BatchRecord>();
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json").OrderByDescending(f => f).Take(max))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<BatchRecord>(File.ReadAllText(file), Json);
                if (rec != null) list.Add(rec);
            }
            catch (Exception) { /* skip damaged entries */ }
        }
        return list.OrderByDescending(r => r.Timestamp).ToList();
    }

    /// <summary>Batches that never finished (Proteus closed or crashed mid-way).</summary>
    public IReadOnlyList<BatchRecord> Interrupted() => List(50).Where(r => r.InProgress && !r.Undone && !r.PartlyUndone).ToList();

    /// <summary>
    /// Reverts a batch: copies go to the Recycle Bin (only if unchanged since), dates and attributes
    /// are restored, renamed and moved items go back, and folders the batch created are removed
    /// when empty. Items that are no longer where Proteus left them are reported, not touched.
    /// When only some items can be put back, those are marked restored and the batch stays open as
    /// "partly undone", so the rest can be tried again later.
    /// </summary>
    public async Task<BatchResult> UndoAsync(BatchRecord record, IProgress<double>? progress = null)
    {
        var results = new List<OperationResult>();
        var moves = new List<RenameOperation>();
        var moveEntry = new List<int>();
        var entries = record.Entries;
        var restored = new bool[entries.Count];
        bool newlyRestored = false;

        await Task.Run(() =>
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (e.Restored) { restored[i] = true; continue; }
                bool targetExists = File.Exists(e.Target) || Directory.Exists(e.Target);
                bool sourceExists = File.Exists(e.Source) || Directory.Exists(e.Source);
                bool samePath = string.Equals(e.Source, e.Target, StringComparison.OrdinalIgnoreCase);

                if (record.InProgress && e.Kind is OperationKind.Rename or OperationKind.Move)
                {
                    // Interrupted batch: the item may be sitting under its temporary name...
                    var temp = RenameExecutor.TempPath(e.Source, record.Id, i);
                    if (File.Exists(temp) || Directory.Exists(temp))
                    {
                        moves.Add(new RenameOperation(temp, e.Source) { Kind = OperationKind.Rename });
                        moveEntry.Add(i);
                        continue;
                    }
                    // ...or it never ran (still at its source, and nothing at the target).
                    if (samePath || (sourceExists && !targetExists)) { restored[i] = true; continue; }
                }

                if (!targetExists)
                {
                    if (record.InProgress && sourceExists) restored[i] = true; // never ran: nothing to undo
                    else results.Add(new OperationResult(e, false, "File is no longer where Proteus put it"));
                    continue;
                }
                try
                {
                    switch (e.Kind)
                    {
                        case OperationKind.Copy:
                            if (e.CopySize == null)
                            {
                                results.Add(new OperationResult(e, false, "Left in place: Proteus could not confirm this copy is unchanged"));
                                break;
                            }
                            if (CopyChanged(e))
                            {
                                results.Add(new OperationResult(e, false, "Left in place: the copy has been changed since it was made"));
                                break;
                            }
                            RemoveCopy(e.Target);
                            results.Add(new OperationResult(e, true, null));
                            restored[i] = newlyRestored = true;
                            break;
                        case OperationKind.Properties:
                            if (e.Before == null) { results.Add(new OperationResult(e, false, "The previous dates were not recorded")); break; }
                            e.Before.ApplyTo(e.Target);
                            results.Add(new OperationResult(e, true, null));
                            restored[i] = newlyRestored = true;
                            break;
                        default:
                            e.Before?.ApplyTo(e.Target);
                            moves.Add(new RenameOperation(e.Target, e.Source) { Kind = e.Kind });
                            moveEntry.Add(i);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new OperationResult(e, false, ex.Message));
                }
            }
        });

        var moved = await RenameExecutor.ExecuteAsync(moves, progress);
        results.AddRange(moved.Results);
        var movedBack = new HashSet<(string, string)>(moved.Results.Where(r => r.Success).Select(r => (r.Operation.Source, r.Operation.Target)));
        for (int k = 0; k < moves.Count; k++)
            if (movedBack.Contains((moves[k].Source, moves[k].Target))) restored[moveEntry[k]] = newlyRestored = true;

        // Remove folders the batch created, if they are empty again (deepest first).
        foreach (var dir in entries.Where(e => e.CreatedFolders != null).SelectMany(e => e.CreatedFolders!)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception) { /* leave it */ }
        }

        if (restored.All(r => r))
        {
            // Everything is back (or there was nothing left to do).
            record.Undone = true;
            record.PartlyUndone = false;
            record.InProgress = false;
            record.Entries = entries.Select(e => e with { Restored = false }).ToList();
            TrySave(record);
        }
        else if (newlyRestored)
        {
            // Keep the record open: what was put back is marked, the rest can be tried again.
            record.PartlyUndone = true;
            record.Entries = entries.Select((e, i) => restored[i] && !e.Restored ? e with { Restored = true } : e).ToList();
            TrySave(record);
        }
        return new BatchResult(results, moved.Cancelled);
    }

    private void TrySave(BatchRecord record)
    {
        try { Save(record); }
        catch (Exception ex) { AppLog.Write("Could not update history after undo", ex); }
    }

    private static bool CopyChanged(RenameOperation e)
    {
        if (e.CopySize == null || e.CopyModified == null) return false;
        if (Directory.Exists(e.Target))
        {
            // Folder copies made before folder fingerprints existed have no count: treat as changed.
            if (e.CopyCount == null) return true;
            var (count, size, newest) = RenameExecutor.FolderFingerprint(e.Target);
            return count != e.CopyCount || size != e.CopySize || newest != e.CopyModified;
        }
        if (!File.Exists(e.Target)) return false;
        var info = new FileInfo(e.Target);
        return info.Length != e.CopySize || info.LastWriteTimeUtc != e.CopyModified;
    }

    public void Delete(BatchRecord record)
    {
        var path = PathFor(record);
        if (File.Exists(path)) File.Delete(path);
    }

    private void Save(BatchRecord record)
    {
        Directory.CreateDirectory(_folder);
        var path = PathFor(record);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
        File.Move(temp, path, overwrite: true); // never leaves a half-written record
    }

    private void Prune()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_folder, "*.json").OrderByDescending(f => f).Skip(MaxRecords).ToList())
                File.Delete(f);
        }
        catch (Exception) { /* best effort */ }
    }

    private string PathFor(BatchRecord r) => Path.Combine(_folder, $"{r.Timestamp:yyyyMMdd-HHmmss}-{r.Id}.json");
}
