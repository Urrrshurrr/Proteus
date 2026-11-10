using Proteus.App.ViewModels;
using Proteus.Core.Engine;
using Proteus.Core.History;
using Proteus.Core.Models;
using Proteus.Core.Presets;
using Proteus.Core.Rules;

namespace Proteus.Tests;

/// <summary>Regression tests for safety-critical behaviour.</summary>
public class RegressionTests
{
    private static List<RenameOperation> Ops(PreviewResult res) => res.Items.Where(i => i.WillRename)
        .Select(i => new RenameOperation(i.File.FullPath, i.NewFullPath) { Kind = i.Kind, Properties = i.Properties }).ToList();

    [Fact]
    public void relative_destination_goes_inside_each_files_folder()
    {
        using var src = new TempDir();
        var files = new[] { new FileEntry(src.File("a.txt")) };
        var res = RenamePipeline.Preview(files, [true], [], new BatchOptions { Mode = BatchMode.Move, DestinationFolder = "Sorted" });
        var item = Assert.Single(res.Items);
        Assert.Equal(Path.Combine(src.Path, "Sorted"), item.TargetDirectory);
        Assert.True(Path.IsPathFullyQualified(item.NewFullPath));
        Assert.Equal(ItemStatus.Ok, item.Status);
    }

    [Fact]
    public void bad_destinations_are_flagged()
    {
        using var src = new TempDir();
        var blocker = src.File("not-a-folder");
        var files = new[] { new FileEntry(src.File("a.txt")) };
        var res = RenamePipeline.Preview(files, [true], [], new BatchOptions { Mode = BatchMode.Copy, DestinationFolder = "bad|name" });
        Assert.Equal(ItemStatus.Invalid, res.Items[0].Status);
        res = RenamePipeline.Preview(files, [true], [], new BatchOptions { Mode = BatchMode.Copy, DestinationFolder = blocker });
        Assert.Equal(ItemStatus.Invalid, res.Items[0].Status);
    }

    [Fact]
    public void executor_refuses_relative_paths()
    {
        var r = RenameExecutor.Execute([new RenameOperation("a.txt", "b.txt")]);
        Assert.Equal(1, r.Failed);
    }

    [Fact]
    public async Task interrupted_swap_is_recovered_from_temporary_names()
    {
        using var d = new TempDir();
        using var hist = new TempDir();
        var a = d.File("a.txt", "A");
        var b = d.File("b.txt", "B");
        var journal = new UndoJournal(hist.Path);
        var ops = new List<RenameOperation> { new(a, b), new(b, a) };
        var record = journal.Begin(ops, "swap");

        // Simulate a crash after phase 1: both files sit under their temporary names.
        File.Move(a, RenameExecutor.TempPath(a, record.Id, 0));
        File.Move(b, RenameExecutor.TempPath(b, record.Id, 1));

        var reloaded = Assert.Single(new UndoJournal(hist.Path).Interrupted());
        var undo = await journal.UndoAsync(reloaded);
        Assert.Equal(2, undo.Succeeded);
        Assert.Equal("A", File.ReadAllText(a));
        Assert.Equal("B", File.ReadAllText(b));
        Assert.Equal(["a.txt", "b.txt"], d.Names());
    }

    [Fact]
    public async Task interrupted_batch_undoes_only_what_ran()
    {
        using var d = new TempDir();
        using var hist = new TempDir();
        var a = d.File("a.txt");
        var c = d.File("c.txt");
        var journal = new UndoJournal(hist.Path);
        var ops = new List<RenameOperation>
        {
            new(a, Path.Combine(d.Path, "a2.txt")),
            new(c, Path.Combine(d.Path, "c2.txt")),
        };
        var record = journal.Begin(ops, "partial");
        File.Move(a, Path.Combine(d.Path, "a2.txt")); // first op ran, second never did

        var undo = await journal.UndoAsync(record);
        Assert.Equal(1, undo.Succeeded);
        Assert.Equal(0, undo.Failed);
        Assert.Equal(["a.txt", "c.txt"], d.Names());
    }

    [Fact]
    public void completed_batch_replaces_the_plan()
    {
        using var d = new TempDir();
        using var hist = new TempDir();
        var a = d.File("a.txt");
        var journal = new UndoJournal(hist.Path);
        var ops = new List<RenameOperation> { new(a, Path.Combine(d.Path, "b.txt")) };
        var record = journal.Begin(ops, "x");
        var result = RenameExecutor.Execute(ops, batchTag: record.Id);
        journal.Complete(record, result.Completed);
        var saved = Assert.Single(journal.List());
        Assert.False(saved.InProgress);
        Assert.Empty(journal.Interrupted());
    }

    [Fact]
    public async Task rename_waits_for_the_latest_preview()
    {
        using var d = new TempDir();
        using var hist = new TempDir();
        using var presets = new TempDir();
        d.File("a.txt");
        var vm = new MainViewModel(new UndoJournal(hist.Path), new PresetStore(presets.Path));
        vm.Rules.Clear();
        await vm.AddPathsAsync([d.Path], recurse: false, foldersAsItems: false);
        vm.Rules.Add(new InsertRule { Text = "x-" });
        await vm.WaitForPreviewAsync();
        Assert.Equal("x-a.txt", vm.VisibleFiles[0].NewName);

        // Change the rule and press Rename straight away, before the new preview has run.
        ((InsertRule)vm.Rules[0]).Text = "y-";
        Assert.True(vm.IsPreviewPending);
        await vm.RenameCommand.ExecuteAsync(null);

        Assert.Equal(["y-a.txt"], d.Names());
    }

    private sealed class ExplodingRule : RenameRule
    {
        public override string Title => "Exploding";
        public override string Icon => "!";
        public override string Summary => "throws";
        protected override string Transform(string input, RuleContext ctx) =>
            input.StartsWith('b') ? throw new InvalidOperationException("boom") : input + "!";
    }

    [Fact]
    public void a_throwing_rule_marks_only_that_file()
    {
        using var d = new TempDir();
        var files = new[] { new FileEntry(d.File("a.txt")), new FileEntry(d.File("b.txt")) };
        var rule = new ExplodingRule();
        var res = RenamePipeline.Preview(files, [true, true], [rule]);
        Assert.Equal(ItemStatus.Ok, res.Items[0].Status);
        Assert.Equal(ItemStatus.Invalid, res.Items[1].Status);
        Assert.Contains("boom", res.Items[1].Message);
        Assert.Equal("boom", rule.RuntimeError);
    }

    [Fact]
    public void rearrange_on_an_empty_name_does_not_throw()
    {
        using var d = new TempDir();
        var e = new FileEntry(d.File("abc.txt"));
        var rules = new RenameRule[] { new RemoveRule { Count = 10 }, new RearrangeRule { Delimiter = "", Pattern = "$L-$1" } };
        var (name, problem) = RenamePipeline.RunSafe(e, rules, new RuleContext(e, 0, 0, 1));
        Assert.Null(problem);
        Assert.Equal(".txt", name); // nothing to rearrange, so the (empty) name is left alone
    }

    [Fact]
    public void let_and_const_work_for_every_file_and_nothing_leaks()
    {
        using var d = new TempDir();
        var files = new[] { new FileEntry(d.File("a.txt")), new FileEntry(d.File("b.txt")), new FileEntry(d.File("c.txt")) };
        var rule = new ScriptRule { Script = "const up = name.toUpperCase();\nlet n = number;\nvar seen = (typeof seen === 'undefined' ? 0 : seen) + 1;\nname = up + n + '-' + seen;" };
        var res = RenamePipeline.Preview(files, [true, true, true], [rule]);
        Assert.Equal(["A1-1.txt", "B2-1.txt", "C3-1.txt"], res.Items.Select(i => i.NewName));
        Assert.Null(rule.RuntimeError);
    }

    [Fact]
    public void script_error_line_numbers_match_the_editor()
    {
        using var d = new TempDir();
        var e = new FileEntry(d.File("a.txt"));
        var rule = new ScriptRule { Script = "name = name;\nname = missing.thing;" };
        RenamePipeline.Run(e, [rule], new RuleContext(e, 0, 0, 1));
        Assert.Contains("line 2", rule.RuntimeError);
    }

    [Fact]
    public async Task a_copy_changed_since_is_left_in_place()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        using var hist = new TempDir();
        var files = new[] { new FileEntry(src.File("a.txt", "A")) };
        var res = RenamePipeline.Preview(files, [true], [], new BatchOptions { Mode = BatchMode.Copy, DestinationFolder = dst.Path });
        var result = RenameExecutor.Execute(Ops(res));
        var copy = Path.Combine(dst.Path, "a.txt");
        Assert.NotNull(result.Completed.Single().CopySize);
        File.AppendAllText(copy, " edited");
        File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddMinutes(5));

        UndoJournal.RemoveCopy = FolderSafetyTests.DeleteCopy;
        var journal = new UndoJournal(hist.Path);
        var undo = await journal.UndoAsync(journal.Record(result.Completed, "copy"));
        Assert.Equal(1, undo.Failed);
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void network_paths_have_no_recycle_bin()
    {
        Assert.False(RenameExecutor.HasRecycleBin(@"\\server\share\file.txt"));
        Assert.Throws<IOException>(() => RenameExecutor.Recycle(@"\\server\share\file.txt"));
    }

    [Fact]
    public async Task a_batch_is_not_marked_undone_when_nothing_could_be_restored()
    {
        using var d = new TempDir();
        using var hist = new TempDir();
        var journal = new UndoJournal(hist.Path);
        var record = journal.Record([new RenameOperation(Path.Combine(d.Path, "x.txt"), Path.Combine(d.Path, "gone.txt"))], "x");
        var undo = await journal.UndoAsync(record);
        Assert.Equal(1, undo.Failed);
        Assert.False(record.Undone);
    }

    [Fact]
    public void folder_and_its_contents_in_one_batch_is_a_conflict()
    {
        using var d = new TempDir();
        var folder = Path.Combine(d.Path, "sub");
        Directory.CreateDirectory(folder);
        var inner = Path.Combine(folder, "a.txt");
        File.WriteAllText(inner, "x");
        var files = new[] { new FileEntry(folder), new FileEntry(inner) };
        var res = RenamePipeline.Preview(files, [true, true], [new InsertRule { Text = "z" }]);
        Assert.Equal(ItemStatus.Ok, res.Items[0].Status);
        Assert.Equal(ItemStatus.Conflict, res.Items[1].Status);
    }

    [Fact]
    public void setting_a_date_to_its_current_value_is_not_a_change()
    {
        using var d = new TempDir();
        var path = d.File("a.txt");
        var when = new DateTime(2020, 1, 1, 12, 0, 0);
        File.SetLastWriteTime(path, when);
        var opts = new BatchOptions();
        opts.Modified.Source = TimeSource.Fixed;
        opts.Modified.FixedDate = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        opts.Modified.FixedTime = new TimeSpan(12, 0, 0);
        var res = RenamePipeline.Preview([new FileEntry(path)], [true], [], opts);
        Assert.Equal(ItemStatus.Unchanged, res.Items[0].Status);
    }

    [Fact]
    public void reserved_subfolder_names_are_made_safe()
    {
        using var d = new TempDir();
        var files = new[] { new FileEntry(d.File("a.txt")) };
        var res = RenamePipeline.Preview(files, [true], [], new BatchOptions { Mode = BatchMode.Move, SubfolderTemplate = "CON" });
        Assert.Equal(Path.Combine(d.Path, "CON_"), res.Items[0].TargetDirectory);
    }

    [Fact]
    public void csv_quotes_values_with_edge_spaces()
    {
        Assert.Equal("\" a\",b \r\n".Replace("b \r\n", "\"b \"\r\n"), Csv.Write([[" a", "b "]]));
    }

    [Fact]
    public void blank_scope_is_no_scope_and_bad_regex_is_reported()
    {
        Assert.False(new InsertRule { ScopePattern = " ; " }.HasScope);
        using var d = new TempDir();
        var e = new FileEntry(d.File("a.txt"));
        var rule = new InsertRule { Text = "x", ScopePattern = "(", ScopeIsRegex = true };
        RenamePipeline.Run(e, [rule], new RuleContext(e, 0, 0, 1));
        Assert.NotNull(rule.RuntimeError);
    }
}
