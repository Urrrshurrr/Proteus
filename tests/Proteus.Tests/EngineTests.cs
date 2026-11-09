using Proteus.Core.Engine;
using Proteus.Core.History;
using Proteus.Core.Models;
using Proteus.Core.Presets;
using Proteus.Core.Rules;
using Proteus.Core.Tokens;

namespace Proteus.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "proteus-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name, string content = "x")
    {
        var p = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(p, content);
        return p;
    }
    public string[] Names() => Directory.GetFileSystemEntries(Path).Select(System.IO.Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class RuleTests
{
    private static string Run(string fileName, params RenameRule[] rules)
    {
        using var dir = new TempDir();
        var entry = new FileEntry(dir.File(fileName));
        return RenamePipeline.Run(entry, rules, new RuleContext(entry, 0, 0, 1));
    }

    [Fact] public void Replace_text_all() => Assert.Equal("a-b-c.txt", Run("a_b_c.txt", new ReplaceRule { Find = "_", ReplaceWith = "-" }));
    [Fact] public void Replace_first() => Assert.Equal("a-b_c.txt", Run("a_b_c.txt", new ReplaceRule { Find = "_", ReplaceWith = "-", Occurrence = Occurrence.First }));
    [Fact] public void Replace_last() => Assert.Equal("a_b-c.txt", Run("a_b_c.txt", new ReplaceRule { Find = "_", ReplaceWith = "-", Occurrence = Occurrence.Last }));
    [Fact] public void Replace_is_case_insensitive_by_default() => Assert.Equal("xbc.txt", Run("Abc.txt", new ReplaceRule { Find = "a", ReplaceWith = "x" }));
    [Fact] public void Replace_text_dollar_is_literal() => Assert.Equal("$1.txt", Run("a.txt", new ReplaceRule { Find = "a", ReplaceWith = "$1" }));
    [Fact] public void Replace_regex_groups() => Assert.Equal("2024 Photo.jpg", Run("Photo 2024.jpg", new ReplaceRule { Find = @"(\w+) (\d+)", ReplaceWith = "$2 $1", UseRegex = true }));
    [Fact] public void Replace_does_not_touch_extension() => Assert.Equal("X.txt", Run("txt.txt", new ReplaceRule { Find = "txt", ReplaceWith = "X" }));
    [Fact] public void Replace_bad_regex_leaves_name() => Assert.Equal("a.txt", Run("a.txt", new ReplaceRule { Find = "(", UseRegex = true, ReplaceWith = "x" }));

    [Fact] public void Insert_prefix_suffix()
    {
        Assert.Equal("pre-a.txt", Run("a.txt", new InsertRule { Text = "pre-" }));
        Assert.Equal("a-suf.txt", Run("a.txt", new InsertRule { Text = "-suf", Position = InsertPosition.Suffix }));
    }

    [Fact] public void Insert_at_position_and_after_text()
    {
        Assert.Equal("abXcd.txt", Run("abcd.txt", new InsertRule { Text = "X", Position = InsertPosition.AtPosition, Index = 3 }));
        Assert.Equal("abcXd.txt", Run("abcd.txt", new InsertRule { Text = "X", Position = InsertPosition.AtPosition, Index = 2, FromEnd = true }));
        Assert.Equal("abc!d.txt", Run("abcd.txt", new InsertRule { Text = "!", Position = InsertPosition.AfterText, Anchor = "c" }));
    }

    [Fact] public void Remove_from_start_and_end()
    {
        Assert.Equal("cdef.txt", Run("abcdef.txt", new RemoveRule { Count = 2 }));
        Assert.Equal("abcd.txt", Run("abcdef.txt", new RemoveRule { Count = 2, FromEnd = true }));
        Assert.Equal("adef.txt", Run("abcdef.txt", new RemoveRule { Start = 2, Count = 2 }));
        Assert.Equal("ab.txt", Run("ab.txt", new RemoveRule { Start = 10, Count = 2 }));
    }

    [Fact] public void Case_modes()
    {
        Assert.Equal("Hello World-Again.txt", Run("hELLO wORLD-again.txt", new CaseRule { Mode = CaseMode.Title }));
        Assert.Equal("Hello world.txt", Run("hELLO wORLD.txt", new CaseRule { Mode = CaseMode.Sentence }));
        Assert.Equal("ABC.txt", Run("abc.txt", new CaseRule { Mode = CaseMode.Upper }));
        Assert.Equal("abc.TXT", Run("abc.txt", new CaseRule { Mode = CaseMode.Upper, Target = RuleTarget.Extension }));
    }

    [Fact] public void Cleanup_defaults()
    {
        Assert.Equal("My Song.mp3", Run("My__Song_%20.mp3", new CleanupRule()));
        Assert.Equal("Movie.mkv", Run("Movie (2010) [1080p].mkv", new CleanupRule { RemoveRoundBrackets = true, RemoveSquareBrackets = true }));
        Assert.Equal("Cafe.txt", Run("Café.txt", new CleanupRule { StripDiacritics = true }));
    }

    [Fact] public void Extension_modes()
    {
        Assert.Equal("a.jpg", Run("a.JPG", new ExtensionRule()));
        Assert.Equal("a.png", Run("a.jpg", new ExtensionRule { Mode = ExtensionMode.Change, NewExtension = ".png" }));
        Assert.Equal("a", Run("a.jpg", new ExtensionRule { Mode = ExtensionMode.Remove }));
        Assert.Equal("a.jpg.bak", Run("a.jpg", new ExtensionRule { Mode = ExtensionMode.Append, NewExtension = "bak" }));
    }

    [Fact] public void Pad_numbers() => Assert.Equal("Track 007 of 012.mp3", Run("Track 7 of 12.mp3", new PadNumbersRule { Digits = 3 }));

    [Fact] public void Template_with_tokens()
    {
        Assert.Equal("x-a-1.txt", Run("a.txt", new TemplateRule { Template = "x-<name>-<n>" }));
        Assert.Equal("[a].txt", Run("a.txt", new TemplateRule { Template = "[<current>]" }));
    }

    [Fact] public void Disabled_rule_does_nothing() => Assert.Equal("a.txt", Run("a.txt", new InsertRule { Text = "x", IsEnabled = false }));

    [Fact] public void Scope_limits_rule()
    {
        Assert.Equal("x-a.jpg", Run("a.jpg", new InsertRule { Text = "x-", ScopePattern = "*.jpg" }));
        Assert.Equal("a.png", Run("a.png", new InsertRule { Text = "x-", ScopePattern = "*.jpg" }));
        Assert.Equal("x-a.png", Run("a.png", new InsertRule { Text = "x-", ScopePattern = "*.jpg", ScopeInvert = true }));
        Assert.Equal("x-a.png", Run("a.png", new InsertRule { Text = "x-", ScopePattern = "*.jpg; *.png" }));
    }

    [Fact] public void Unknown_tokens_kept_and_escapes()
    {
        using var dir = new TempDir();
        var e = new FileEntry(dir.File("a.txt"));
        var ctx = new RuleContext(e, 4, 0, 10);
        Assert.Equal("<nope> 005 of 10", TokenEngine.Expand("<nope> <n:3> of <count>", ctx));
        Assert.Equal("<n>", TokenEngine.Expand("\\<n\\>", ctx));
    }
}

public class NumberingAndPreviewTests
{
    [Fact]
    public void Numbering_and_per_folder_reset()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        var files = new[]
        {
            new FileEntry(dir.File("a.txt")), new FileEntry(dir.File("b.txt")),
            new FileEntry(dir.File(Path.Combine("sub", "c.txt"))),
        };
        var inc = new[] { true, true, true };
        var res = RenamePipeline.Preview(files, inc, [new NumberingRule { Digits = 2, Separator = "_" }]);
        Assert.Equal(["01_a.txt", "02_b.txt", "03_c.txt"], res.Items.Select(i => i.NewName));

        res = RenamePipeline.Preview(files, inc, [new NumberingRule { Digits = 1, Separator = "_", ResetPerFolder = true }]);
        Assert.Equal(["1_a.txt", "2_b.txt", "1_c.txt"], res.Items.Select(i => i.NewName));
    }

    [Fact]
    public void Excluded_files_do_not_count()
    {
        using var dir = new TempDir();
        var files = new[] { new FileEntry(dir.File("a.txt")), new FileEntry(dir.File("b.txt")), new FileEntry(dir.File("c.txt")) };
        var res = RenamePipeline.Preview(files, [true, false, true], [new NumberingRule { Digits = 1, Separator = " " }]);
        Assert.Equal(["1 a.txt", "b.txt", "2 c.txt"], res.Items.Select(i => i.NewName));
        Assert.Equal(ItemStatus.Excluded, res.Items[1].Status);
    }

    [Fact]
    public void Detects_duplicates_existing_and_invalid()
    {
        using var dir = new TempDir();
        dir.File("taken.txt");
        var files = new[] { new FileEntry(dir.File("a.txt")), new FileEntry(dir.File("b.txt")) };
        var res = RenamePipeline.Preview(files, [true, true], [new TemplateRule { Template = "same" }]);
        Assert.All(res.Items, i => Assert.Equal(ItemStatus.Conflict, i.Status));

        res = RenamePipeline.Preview([files[0]], [true], [new TemplateRule { Template = "taken" }]);
        Assert.Equal(ItemStatus.Conflict, res.Items[0].Status);

        res = RenamePipeline.Preview([files[0]], [true], [new TemplateRule { Template = "a:b" }]);
        Assert.Equal(ItemStatus.Invalid, res.Items[0].Status);

        res = RenamePipeline.Preview([files[0]], [true], [new TemplateRule { Template = "CON" }]);
        Assert.Equal(ItemStatus.Invalid, res.Items[0].Status);
    }

    [Fact]
    public void Swap_is_allowed_by_validator()
    {
        using var dir = new TempDir();
        var files = new[] { new FileEntry(dir.File("1.txt")), new FileEntry(dir.File("2.txt")) };
        // 1 -> 2 and 2 -> 1
        var names = new[] { "2.txt", "1.txt" };
        var items = NameValidator.Validate(files, names);
        Assert.All(items, i => Assert.Equal(ItemStatus.Ok, i.Status));
    }

    [Fact]
    public void Blocked_chain_is_flagged()
    {
        using var dir = new TempDir();
        var files = new[] { new FileEntry(dir.File("a.txt")), new FileEntry(dir.File("b.txt")) };
        // a -> b (b is being renamed), but b -> invalid so b stays: a must be blocked.
        var items = NameValidator.Validate(files, ["b.txt", "bad?.txt"]);
        Assert.Equal(ItemStatus.Conflict, items[0].Status);
        Assert.Equal(ItemStatus.Invalid, items[1].Status);
    }

    [Fact]
    public void Trace_shows_each_step()
    {
        using var dir = new TempDir();
        var files = new[] { new FileEntry(dir.File("a_b.txt")) };
        var steps = RenamePipeline.Trace(files, [true], 0,
            [new ReplaceRule { Find = "_", ReplaceWith = " " }, new CaseRule { Mode = CaseMode.Upper }, new InsertRule { Text = "z", IsEnabled = false }]);
        Assert.Equal(["a_b.txt", "a b.txt", "A B.txt", "A B.txt"], steps.Select(s => s.NameAfter));
        Assert.False(steps[3].Applied);
    }
}

public class ExecutorTests
{
    private static List<RenameOperation> Ops(TempDir d, params (string From, string To)[] pairs) =>
        pairs.Select(p => new RenameOperation(Path.Combine(d.Path, p.From), Path.Combine(d.Path, p.To))).ToList();

    [Fact]
    public void Simple_rename()
    {
        using var d = new TempDir();
        d.File("a.txt", "A");
        var r = RenameExecutor.Execute(Ops(d, ("a.txt", "b.txt")));
        Assert.Equal(1, r.Succeeded);
        Assert.Equal(["b.txt"], d.Names());
    }

    [Fact]
    public void Swap_two_files()
    {
        using var d = new TempDir();
        d.File("a.txt", "A"); d.File("b.txt", "B");
        var r = RenameExecutor.Execute(Ops(d, ("a.txt", "b.txt"), ("b.txt", "a.txt")));
        Assert.Equal(2, r.Succeeded);
        Assert.Equal("B", File.ReadAllText(Path.Combine(d.Path, "a.txt")));
        Assert.Equal("A", File.ReadAllText(Path.Combine(d.Path, "b.txt")));
    }

    [Fact]
    public void Three_cycle_and_shift()
    {
        using var d = new TempDir();
        d.File("1.txt", "1"); d.File("2.txt", "2"); d.File("3.txt", "3");
        var r = RenameExecutor.Execute(Ops(d, ("1.txt", "2.txt"), ("2.txt", "3.txt"), ("3.txt", "4.txt")));
        Assert.Equal(3, r.Succeeded);
        Assert.Equal(["2.txt", "3.txt", "4.txt"], d.Names());
        Assert.Equal("1", File.ReadAllText(Path.Combine(d.Path, "2.txt")));
        Assert.Equal("3", File.ReadAllText(Path.Combine(d.Path, "4.txt")));
    }

    [Fact]
    public void Case_only_rename()
    {
        using var d = new TempDir();
        d.File("photo.JPG");
        var r = RenameExecutor.Execute(Ops(d, ("photo.JPG", "Photo.jpg")));
        Assert.Equal(1, r.Succeeded);
        Assert.Equal(["Photo.jpg"], d.Names());
    }

    [Fact]
    public void Never_overwrites_and_reports_failure()
    {
        using var d = new TempDir();
        d.File("a.txt", "A"); d.File("b.txt", "B"); d.File("c.txt", "C");
        var r = RenameExecutor.Execute(Ops(d, ("a.txt", "c.txt"), ("b.txt", "d.txt")));
        Assert.Equal(1, r.Succeeded);
        Assert.Equal(1, r.Failed);
        Assert.Equal("C", File.ReadAllText(Path.Combine(d.Path, "c.txt")));
        Assert.Equal(["a.txt", "c.txt", "d.txt"], d.Names());
    }

    [Fact]
    public void Folder_rename()
    {
        using var d = new TempDir();
        Directory.CreateDirectory(Path.Combine(d.Path, "old"));
        var r = RenameExecutor.Execute(Ops(d, ("old", "new")));
        Assert.Equal(1, r.Succeeded);
        Assert.Equal(["new"], d.Names());
    }

    [Fact]
    public async Task Undo_round_trip_survives_new_journal_instance()
    {
        using var d = new TempDir();
        using var hist = new TempDir();
        d.File("a.txt", "A"); d.File("b.txt", "B");
        var r = RenameExecutor.Execute(Ops(d, ("a.txt", "b.txt"), ("b.txt", "c.txt")));
        Assert.Equal(2, r.Succeeded);
        new UndoJournal(hist.Path).Record(r.Completed, "test");

        var journal = new UndoJournal(hist.Path); // as if the app restarted
        var batch = Assert.Single(journal.List());
        var undo = await journal.UndoAsync(batch);
        Assert.Equal(2, undo.Succeeded);
        Assert.Equal("A", File.ReadAllText(Path.Combine(d.Path, "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(d.Path, "b.txt")));
        Assert.True(journal.List()[0].Undone);
    }
}

public class PresetTests
{
    [Fact]
    public void Presets_round_trip_all_rule_types()
    {
        using var dir = new TempDir();
        var store = new PresetStore(dir.Path);
        RenameRule[] rules =
        [
            new ReplaceRule { Find = "a", ReplaceWith = "b", UseRegex = true, Occurrence = Occurrence.Last, ScopePattern = "*.jpg" },
            new InsertRule { Text = "x", Position = InsertPosition.Suffix },
            new RemoveRule { Count = 3 }, new CaseRule { Mode = CaseMode.Upper, Target = RuleTarget.Extension },
            new NumberingRule { Digits = 4 }, new TemplateRule { Template = "<n>" }, new CleanupRule { StripDigits = true },
            new ExtensionRule { Mode = ExtensionMode.Change, NewExtension = "png" }, new PadNumbersRule { Digits = 5, IsEnabled = false },
        ];
        store.Save("My: preset", rules);
        Assert.Contains("My_ preset", store.Names());
        var loaded = store.Load("My: preset")!;
        Assert.Equal(rules.Select(r => r.GetType()), loaded.Rules.Select(r => r.GetType()));
        var rep = (ReplaceRule)loaded.Rules[0];
        Assert.Equal(Occurrence.Last, rep.Occurrence);
        Assert.Equal("*.jpg", rep.ScopePattern);
        Assert.False(loaded.Rules[8].IsEnabled);
        Assert.Equal(RuleTarget.Extension, loaded.Rules[3].Target);
    }

    [Fact]
    public void Clone_is_independent()
    {
        var r = new ReplaceRule { Find = "a" };
        var c = (ReplaceRule)r.Clone();
        c.Find = "b";
        Assert.Equal("a", r.Find);
    }
}
