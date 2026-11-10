using Proteus.Core.Engine;
using Proteus.Core.History;
using Proteus.Core.Models;
using Proteus.Core.Rules;

namespace Proteus.Tests;

/// <summary>Folder copies and moves must never delete or overwrite anything that was already there.</summary>
public class FolderSafetyTests
{
    /// <summary>Stands in for the Recycle Bin in tests: removes a copied file or folder.</summary>
    public static void DeleteCopy(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }

    private static List<RenameOperation> Ops(PreviewResult res) => res.Items.Where(i => i.WillRename)
        .Select(i => new RenameOperation(i.File.FullPath, i.NewFullPath) { Kind = i.Kind, Properties = i.Properties }).ToList();

    private static string Folder(string parent, string name, params string[] files)
    {
        var dir = Path.Combine(parent, name);
        Directory.CreateDirectory(dir);
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), f);
        return dir;
    }

    [Fact]
    public void a_folder_that_appears_after_the_preview_is_never_deleted()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var album = Folder(src.Path, "Album", "song.mp3");
        var res = RenamePipeline.Preview([new FileEntry(album)], [true], [], new BatchOptions { Mode = BatchMode.Copy, DestinationFolder = dst.Path });
        Assert.Equal(ItemStatus.Ok, res.Items[0].Status);

        // Something creates the destination between the preview and the run.
        var existing = Folder(dst.Path, "Album", "mine.txt");
        var result = RenameExecutor.Execute(Ops(res));

        Assert.Equal(1, result.Failed);
        Assert.Contains("already exists", result.Results[0].Error);
        Assert.True(File.Exists(Path.Combine(existing, "mine.txt")));
        Assert.False(File.Exists(Path.Combine(existing, "song.mp3")));
        Assert.True(File.Exists(Path.Combine(album, "song.mp3")));
    }

    [Fact]
    public async Task undoing_a_folder_move_keeps_a_folder_that_came_back()
    {
        using var d = new TempDir();
        using var hist = new TempDir();
        var a = Folder(d.Path, "A", "one.txt");
        var b = Path.Combine(d.Path, "B");
        var result = RenameExecutor.Execute([new RenameOperation(a, b) { Kind = OperationKind.Move }]);
        Assert.Equal(1, result.Succeeded);

        Folder(d.Path, "A", "new.txt");
        var journal = new UndoJournal(hist.Path);
        var undo = await journal.UndoAsync(journal.Record(result.Completed, "move"));

        Assert.Equal(1, undo.Failed);
        Assert.True(File.Exists(Path.Combine(a, "new.txt")));
        Assert.True(File.Exists(Path.Combine(b, "one.txt")));
    }

    [Fact]
    public void a_folder_cannot_land_where_another_item_goes_inside()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var photo = src.File("x.jpg");
        var folder = Folder(src.Path, "jpg", "inside.txt");
        var opts = new BatchOptions { Mode = BatchMode.Copy, DestinationFolder = dst.Path, SubfolderTemplate = "<ext>" };
        var res = RenamePipeline.Preview([new FileEntry(photo), new FileEntry(folder)], [true, true], [], opts);

        Assert.Equal(ItemStatus.Ok, res.Items[0].Status);
        Assert.Equal(ItemStatus.Conflict, res.Items[1].Status);

        // Even if both were run, the folder copy fails without touching the file's copy.
        var both = new List<RenameOperation>
        {
            new(photo, Path.Combine(dst.Path, "jpg", "x.jpg")) { Kind = OperationKind.Copy },
            new(folder, Path.Combine(dst.Path, "jpg")) { Kind = OperationKind.Copy },
        };
        var result = RenameExecutor.Execute(both);
        Assert.True(result.Results[0].Success);
        Assert.False(result.Results[1].Success);
        Assert.True(File.Exists(Path.Combine(dst.Path, "jpg", "x.jpg")));
        Assert.False(File.Exists(Path.Combine(dst.Path, "jpg", "inside.txt")));
    }

    [Theory]
    [InlineData(BatchMode.Copy)]
    [InlineData(BatchMode.Move)]
    public void a_folder_cannot_go_inside_itself(BatchMode mode)
    {
        using var d = new TempDir();
        var a = Folder(d.Path, "A", "f.txt");
        var res = RenamePipeline.Preview([new FileEntry(a)], [true], [], new BatchOptions { Mode = mode, DestinationFolder = Path.Combine(a, "sub") });
        Assert.Equal(ItemStatus.Invalid, res.Items[0].Status);

        var result = RenameExecutor.Execute([new RenameOperation(a, Path.Combine(a, "sub", "A")) { Kind = mode == BatchMode.Copy ? OperationKind.Copy : OperationKind.Move }]);
        Assert.Equal(1, result.Failed);
        Assert.True(File.Exists(Path.Combine(a, "f.txt")));
        Assert.False(Directory.Exists(Path.Combine(a, "sub", "A")));
    }

    [Fact]
    public async Task a_copied_folder_changed_since_is_left_in_place()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        using var hist = new TempDir();
        var album = Folder(src.Path, "Album", "song.mp3");
        var res = RenamePipeline.Preview([new FileEntry(album)], [true], [], new BatchOptions { Mode = BatchMode.Copy, DestinationFolder = dst.Path });
        var result = RenameExecutor.Execute(Ops(res));
        var op = result.Completed.Single();
        Assert.NotNull(op.CopyCount);

        var copy = Path.Combine(dst.Path, "Album");
        File.WriteAllText(Path.Combine(copy, "added later.txt"), "keep me");

        UndoJournal.RemoveCopy = DeleteCopy;
        var journal = new UndoJournal(hist.Path);
        var undo = await journal.UndoAsync(journal.Record(result.Completed, "copy"));
        Assert.Equal(1, undo.Failed);
        Assert.True(File.Exists(Path.Combine(copy, "added later.txt")));
    }

    [Fact]
    public async Task an_unchanged_copied_folder_is_removed_by_undo()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        using var hist = new TempDir();
        var album = Folder(src.Path, "Album", "song.mp3");
        Folder(album, "Disc 2", "b.mp3");
        var res = RenamePipeline.Preview([new FileEntry(album)], [true], [], new BatchOptions { Mode = BatchMode.Copy, DestinationFolder = dst.Path });
        var result = RenameExecutor.Execute(Ops(res));

        UndoJournal.RemoveCopy = DeleteCopy;
        var journal = new UndoJournal(hist.Path);
        var record = journal.Record(result.Completed, "copy");
        var undo = await journal.UndoAsync(record);
        Assert.Equal(0, undo.Failed);
        Assert.True(record.Undone);
        Assert.False(Directory.Exists(Path.Combine(dst.Path, "Album")));
        Assert.True(File.Exists(Path.Combine(album, "Disc 2", "b.mp3")));
    }

    [Fact]
    public async Task a_folder_copy_recorded_without_a_fingerprint_is_left_in_place()
    {
        using var d = new TempDir();
        using var hist = new TempDir();
        var copy = Folder(d.Path, "Copy", "a.txt");
        UndoJournal.RemoveCopy = DeleteCopy;
        var journal = new UndoJournal(hist.Path);
        var record = journal.Record([new RenameOperation(Path.Combine(d.Path, "Orig"), copy) { Kind = OperationKind.Copy }], "old copy");
        var undo = await journal.UndoAsync(record);
        Assert.Equal(1, undo.Failed);
        Assert.True(Directory.Exists(copy));
    }

    [Fact]
    public async Task a_partly_failed_undo_keeps_the_rest_for_later()
    {
        using var d = new TempDir();
        using var hist = new TempDir();
        var a = d.File("a.txt", "A");
        var b = d.File("b.txt", "B");
        var result = RenameExecutor.Execute([new RenameOperation(a, Path.Combine(d.Path, "a2.txt")), new RenameOperation(b, Path.Combine(d.Path, "b2.txt"))]);
        Assert.Equal(2, result.Succeeded);

        // Something new now sits at a.txt, so that one cannot go back.
        File.WriteAllText(a, "new");
        var journal = new UndoJournal(hist.Path);
        var record = journal.Record(result.Completed, "rename");
        var undo = await journal.UndoAsync(record);
        Assert.Equal(1, undo.Succeeded);
        Assert.Equal(1, undo.Failed);
        Assert.False(record.Undone);
        Assert.True(record.PartlyUndone);
        Assert.Single(record.Entries, e => e.Restored);
        Assert.Equal("B", File.ReadAllText(b));

        // The record on disk says the same, and a second try finishes the job.
        var reloaded = journal.List().Single(r => r.Id == record.Id);
        Assert.True(reloaded.PartlyUndone);
        File.Delete(a);
        undo = await journal.UndoAsync(reloaded);
        Assert.Equal(0, undo.Failed);
        Assert.True(reloaded.Undone);
        Assert.Equal("A", File.ReadAllText(a));
        Assert.Equal("B", File.ReadAllText(b));
    }

    [Fact]
    public void add_a_number_never_cuts_a_name_at_a_slash()
    {
        using var d = new TempDir();
        var files = new[] { new FileEntry(d.File("a.mp3")) };
        var rule = new ListRule { Lines = "AC/DC - Back in Black" };
        var opts = new BatchOptions { Conflicts = ConflictPolicy.AddNumber };
        var res = RenamePipeline.Preview(files, [true], [rule], opts);
        Assert.Equal(ItemStatus.Invalid, res.Items[0].Status);
        Assert.StartsWith("AC/DC", res.Items[0].NewName);
    }

    [Fact]
    public void add_a_number_keeps_dot_files_whole()
    {
        using var one = new TempDir();
        using var two = new TempDir();
        using var dst = new TempDir();
        var files = new[] { new FileEntry(one.File(".gitignore")), new FileEntry(two.File(".gitignore")) };
        var opts = new BatchOptions { Mode = BatchMode.Copy, DestinationFolder = dst.Path, Conflicts = ConflictPolicy.AddNumber };
        var res = RenamePipeline.Preview(files, [true, true], [], opts);
        Assert.Equal([".gitignore", ".gitignore (2)"], res.Items.Select(i => i.NewName));
    }

    [Fact]
    public void folder_trees_with_read_only_files_can_be_deleted()
    {
        using var d = new TempDir();
        var tree = Folder(d.Path, "repo", "a.txt");
        var git = Folder(tree, ".git", "pack.idx");
        File.SetAttributes(Path.Combine(git, "pack.idx"), FileAttributes.ReadOnly);
        RenameExecutor.DeleteTree(tree);
        Assert.False(Directory.Exists(tree));
    }
}
