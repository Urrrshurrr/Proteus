using Proteus.Core.Engine;
using Proteus.Core.History;
using Proteus.Core.Models;
using Proteus.Core.Presets;
using Proteus.Core.Rules;

namespace Proteus.Tests;

public class ScriptRuleTests
{
    private static string Run(string fileName, ScriptRule rule, int index = 0)
    {
        using var dir = new TempDir();
        var entry = new FileEntry(dir.File(fileName));
        return RenamePipeline.Run(entry, [rule], new RuleContext(entry, index, 0, 3));
    }

    [Fact]
    public void Script_changes_name_and_extension()
    {
        var r = new ScriptRule { Script = "name = name.toUpperCase().replace(/_/g, ' '); ext = 'TXT';" };
        Assert.Equal("MY FILE.TXT", Run("my_file.md", r));
        Assert.Null(r.Error);
    }

    [Fact]
    public void Script_sees_index_tokens_and_helpers()
    {
        var r = new ScriptRule { Script = "name = pad(number, 3) + '-' + original.name + '-' + token('ext') + '-' + count;" };
        Assert.Equal("002-a-txt-3.txt", Run("a.txt", r, index: 1));
    }

    [Fact]
    public void Script_errors_leave_the_name_and_are_reported()
    {
        var syntax = new ScriptRule { Script = "name = ;" };
        Assert.Equal("a.txt", Run("a.txt", syntax));
        Assert.NotNull(syntax.Error);

        var runtime = new ScriptRule { Script = "name = undefinedThing.foo;" };
        Assert.Equal("a.txt", Run("a.txt", runtime));
        Assert.Contains("error", runtime.Error!, StringComparison.OrdinalIgnoreCase);

        var loop = new ScriptRule { Script = "while (true) {}" };
        Assert.Equal("a.txt", Run("a.txt", loop));
        Assert.NotNull(loop.Error);
    }

    [Fact]
    public void Script_has_no_dotnet_access()
    {
        var r = new ScriptRule { Script = "name = typeof System === 'undefined' && typeof importNamespace === 'undefined' ? 'safe' : 'unsafe';" };
        Assert.Equal("safe.txt", Run("a.txt", r));
    }
}

public class BatchModeTests
{
    private static List<RenameOperation> Ops(PreviewResult res) => res.Items.Where(i => i.WillRename)
        .Select(i => new RenameOperation(i.File.FullPath, i.NewFullPath) { Kind = i.Kind, Properties = i.Properties }).ToList();

    [Fact]
    public void Copy_to_folder_keeps_originals_and_undo_removes_copies()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        using var hist = new TempDir();
        var files = new[] { new FileEntry(src.File("a.txt", "A")), new FileEntry(src.File("b.txt", "B")) };
        var opts = new BatchOptions { Mode = BatchMode.Copy, DestinationFolder = dst.Path };
        var res = RenamePipeline.Preview(files, [true, true], [new InsertRule { Text = "copy-" }], opts);
        Assert.All(res.Items, i => Assert.Equal(ItemStatus.Ok, i.Status));

        var result = RenameExecutor.Execute(Ops(res));
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(["a.txt", "b.txt"], src.Names());
        Assert.Equal(["copy-a.txt", "copy-b.txt"], dst.Names());

        UndoJournal.RemoveCopy = FolderSafetyTests.DeleteCopy;
        var journal = new UndoJournal(hist.Path);
        var rec = journal.Record(result.Completed, "copy");
        var undo = journal.UndoAsync(rec).GetAwaiter().GetResult();
        Assert.Equal(2, undo.Succeeded);
        Assert.Empty(dst.Names());
        Assert.Equal(["a.txt", "b.txt"], src.Names());
    }

    [Fact]
    public void Copy_in_place_with_same_name_is_a_conflict()
    {
        using var src = new TempDir();
        var files = new[] { new FileEntry(src.File("a.txt")) };
        var res = RenamePipeline.Preview(files, [true], [], new BatchOptions { Mode = BatchMode.Copy });
        Assert.Equal(ItemStatus.Conflict, res.Items[0].Status);
    }

    [Fact]
    public void Move_into_token_subfolders_and_undo()
    {
        using var src = new TempDir();
        using var hist = new TempDir();
        var a = src.File("a.jpg"); var b = src.File("b.png");
        File.SetLastWriteTime(a, new DateTime(2023, 5, 1));
        File.SetLastWriteTime(b, new DateTime(2024, 7, 9));
        var files = new[] { new FileEntry(a), new FileEntry(b) };
        var opts = new BatchOptions { Mode = BatchMode.Move, SubfolderTemplate = @"<modified:yyyy>\<ext>" };
        var res = RenamePipeline.Preview(files, [true, true], [], opts);
        Assert.Equal(Path.Combine(src.Path, "2023", "jpg"), res.Items[0].TargetDirectory);
        Assert.Equal(ItemStatus.Ok, res.Items[0].Status);

        var result = RenameExecutor.Execute(Ops(res));
        Assert.Equal(2, result.Succeeded);
        Assert.True(File.Exists(Path.Combine(src.Path, "2023", "jpg", "a.jpg")));
        Assert.True(File.Exists(Path.Combine(src.Path, "2024", "png", "b.png")));

        var journal = new UndoJournal(hist.Path);
        var undo = journal.UndoAsync(journal.Record(result.Completed, "move")).GetAwaiter().GetResult();
        Assert.Equal(2, undo.Succeeded);
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.Empty(Directory.GetDirectories(src.Path)); // created subfolders removed again
    }

    [Fact]
    public void Add_number_resolves_clashes()
    {
        using var src = new TempDir();
        src.File("photo.jpg");
        var files = new[] { new FileEntry(src.File("x.jpg")), new FileEntry(src.File("y.jpg")) };
        var opts = new BatchOptions { Conflicts = ConflictPolicy.AddNumber };
        var res = RenamePipeline.Preview(files, [true, true], [new TemplateRule { Template = "photo" }], opts);
        Assert.Equal(["photo (2).jpg", "photo (3).jpg"], res.Items.Select(i => i.NewName));
        Assert.All(res.Items, i => Assert.Equal(ItemStatus.Ok, i.Status));
    }

    [Fact]
    public void Dates_and_attributes_change_and_undo_restores_them()
    {
        using var src = new TempDir();
        using var hist = new TempDir();
        var path = src.File("IMG_20200102_030405.jpg");
        var originalModified = new DateTime(2025, 1, 1, 10, 0, 0);
        File.SetLastWriteTime(path, originalModified);
        var files = new[] { new FileEntry(path) };
        var opts = new BatchOptions { ReadOnly = AttributeChange.Set };
        opts.Modified.Source = TimeSource.FromName;
        opts.Modified.OffsetMinutes = 60;
        opts.Created.Source = TimeSource.Fixed;
        opts.Created.FixedDate = new DateTimeOffset(2019, 6, 7, 0, 0, 0, TimeSpan.Zero);
        opts.Created.FixedTime = new TimeSpan(8, 30, 0);

        var res = RenamePipeline.Preview(files, [true], [], opts);
        var item = Assert.Single(res.Items);
        Assert.Equal(ItemStatus.Update, item.Status);
        Assert.Equal(new DateTime(2020, 1, 2, 4, 4, 5), item.Properties!.Modified);

        var result = RenameExecutor.Execute(Ops(res));
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(new DateTime(2020, 1, 2, 4, 4, 5), File.GetLastWriteTime(path));
        Assert.Equal(new DateTime(2019, 6, 7, 8, 30, 0), File.GetCreationTime(path));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));

        var journal = new UndoJournal(hist.Path);
        var undo = journal.UndoAsync(journal.Record(result.Completed, "props")).GetAwaiter().GetResult();
        Assert.Equal(1, undo.Succeeded);
        Assert.Equal(originalModified, File.GetLastWriteTime(path));
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void Rename_plus_dates_in_one_batch()
    {
        using var src = new TempDir();
        var path = src.File("a.txt");
        var files = new[] { new FileEntry(path) };
        var opts = new BatchOptions();
        opts.Modified.Source = TimeSource.Fixed;
        opts.Modified.FixedDate = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var res = RenamePipeline.Preview(files, [true], [new InsertRule { Text = "z" }], opts);
        var result = RenameExecutor.Execute(Ops(res));
        Assert.Equal(1, result.Succeeded);
        var renamed = Path.Combine(src.Path, "za.txt");
        Assert.Equal(new DateTime(2000, 1, 1, 12, 0, 0), File.GetLastWriteTime(renamed));
    }

    [Fact]
    public void Options_round_trip_in_presets()
    {
        using var dir = new TempDir();
        var store = new PresetStore(dir.Path);
        var opts = new BatchOptions { Mode = BatchMode.Move, SubfolderTemplate = "<ext>", Hidden = AttributeChange.Clear };
        opts.Accessed.Source = TimeSource.Now;
        store.Save("p", [new ScriptRule { Script = "name='x'" }], opts);
        var p = store.Load("p")!;
        Assert.IsType<ScriptRule>(p.Rules[0]);
        Assert.Equal(BatchMode.Move, p.Options!.Mode);
        Assert.Equal(AttributeChange.Clear, p.Options.Hidden);
        Assert.Equal(TimeSource.Now, p.Options.Accessed.Source);
    }
}

public class CsvTests
{
    [Fact]
    public void Parses_quotes_separators_and_headers()
    {
        var list = Csv.ReadRenameList("Old,New\r\n\"C:\\x\\a, b.txt\",\"New \"\"one\"\".txt\"\nc.txt,d.txt\n");
        Assert.Equal(2, list.Count);
        Assert.Equal(@"C:\x\a, b.txt", list[0].Current);
        Assert.Equal("New \"one\".txt", list[0].New);
        var tab = Csv.ReadRenameList("a.txt\tb.txt");
        Assert.Equal(("a.txt", "b.txt"), tab[0]);
        var semi = Csv.ReadRenameList("a.txt;b.txt");
        Assert.Equal(("a.txt", "b.txt"), semi[0]);
    }

    [Fact]
    public void Writes_escaped_rows()
    {
        Assert.Equal("a,\"b,c\",\"d\"\"e\"\r\n", Csv.Write([["a", "b,c", "d\"e"]]));
    }

    [Fact]
    public void Full_path_mapping_in_list_rule()
    {
        using var dir = new TempDir();
        var f = new FileEntry(dir.File("a.txt"));
        var rule = new ListRule { Mode = ListMode.Mapping, Lines = f.FullPath + "\tRenamed" };
        Assert.Equal("Renamed.txt", RenamePipeline.Run(f, [rule], new RuleContext(f, 0, 0, 1)));
    }
}
