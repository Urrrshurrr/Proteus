using System.Diagnostics;
using System.Globalization;
using Proteus.Core.Engine;
using Proteus.Core.Models;
using Proteus.Core.Rules;
using Proteus.Core.Tokens;

namespace Proteus.Tests;

/// <summary>Rules, tokens and matching: cases that used to give wrong names without saying so.</summary>
public class RuleAndTokenFixTests
{
    private static string Run(string fileName, params RenameRule[] rules)
    {
        using var dir = new TempDir();
        var entry = new FileEntry(dir.File(fileName));
        return RenamePipeline.Run(entry, rules, new RuleContext(entry, 0, 0, 1));
    }

    private static (PreviewResult Result, TempDir Dir) Preview(string[] names, params RenameRule[] rules)
    {
        var d = new TempDir();
        var files = names.Select(n => new FileEntry(d.File(n))).ToArray();
        return (RenamePipeline.Preview(files, files.Select(_ => true).ToArray(), rules), d);
    }

    // ---------- JavaScript ----------

    [Fact]
    public void a_script_that_fails_on_some_files_marks_each_of_them_invalid()
    {
        var rule = new ScriptRule { Script = "name = name.match(/(\\w+) (\\w+)/)[2];" };
        var (res, d) = Preview(["a b.txt", "nospace.txt", "c d.txt"], rule);
        using (d)
        {
            Assert.Equal(ItemStatus.Ok, res.Items[0].Status);
            Assert.Equal(ItemStatus.Invalid, res.Items[1].Status);
            Assert.Contains("Script error", res.Items[1].Message);
            Assert.Equal(ItemStatus.Ok, res.Items[2].Status);
            // The error stays visible even though the last file worked.
            Assert.NotNull(rule.RuntimeError);
        }
    }

    [Fact]
    public void the_rule_error_counts_failed_files()
    {
        var rule = new ScriptRule { Script = "name = missing.thing;" };
        var (res, d) = Preview(["a.txt", "b.txt", "c.txt"], rule);
        using (d)
        {
            Assert.All(res.Items, i => Assert.Equal(ItemStatus.Invalid, i.Status));
            Assert.Contains("3 files failed", rule.RuntimeError);
        }
    }

    [Fact]
    public void a_syntax_error_marks_files_invalid()
    {
        var rule = new ScriptRule { Script = "name = ;" };
        var (res, d) = Preview(["a.txt"], rule);
        using (d)
        {
            Assert.Equal(ItemStatus.Invalid, res.Items[0].Status);
            Assert.StartsWith("Script error", rule.RuntimeError);
        }
    }

    [Fact]
    public void undeclared_variables_and_prototype_changes_do_not_carry_over()
    {
        var rule = new ScriptRule
        {
            Script = "seen = (typeof seen === 'undefined' ? 0 : seen) + 1;\n" +
                     "if (String.prototype.shout) name = name + '!';\n" +
                     "String.prototype.shout = function () { return 1; };\n" +
                     "name = name + seen;",
        };
        var (res, d) = Preview(["a.txt", "b.txt", "c.txt"], rule);
        using (d) Assert.Equal(["a1.txt", "b1.txt", "c1.txt"], res.Items.Select(i => i.NewName));
    }

    [Fact]
    public void a_fresh_engine_per_file_stays_fast()
    {
        var rule = new ScriptRule { Script = "name = name.toUpperCase() + '_' + pad(number, 4);" };
        using var d = new TempDir();
        var files = Enumerable.Range(0, 2000).Select(i => new FileEntry(d.File($"f{i}.txt"))).ToArray();
        var sw = Stopwatch.StartNew();
        var res = RenamePipeline.Preview(files, files.Select(_ => true).ToArray(), [rule]);
        sw.Stop();
        Assert.Equal("F0_0001.txt", res.Items[0].NewName);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"2,000 files took {sw.Elapsed}");
    }

    // ---------- Replace ----------

    [Fact]
    public void a_slow_pattern_stops_with_a_message()
    {
        var rule = new ReplaceRule { Find = "(a+)+$", ReplaceWith = "x", UseRegex = true };
        var names = Enumerable.Range(0, 20).Select(i => new string('a', 40) + $"!{i}.txt").ToArray();
        var sw = Stopwatch.StartNew();
        var (res, d) = Preview(names, rule);
        sw.Stop();
        using (d)
        {
            Assert.Contains("too long", rule.RuntimeError);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        }
    }

    [Fact]
    public void dollars_in_token_values_stay_literal_in_a_regex_replace()
    {
        using var d = new TempDir();
        var folder = Path.Combine(d.Path, "Cost $1 folder");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "report.txt");
        File.WriteAllText(path, "x");
        var e = new FileEntry(path);
        var rule = new ReplaceRule { Find = "(rep)ort", ReplaceWith = "$1 <parent>", UseRegex = true };
        Assert.Equal("rep Cost $1 folder.txt", RenamePipeline.Run(e, [rule], new RuleContext(e, 0, 0, 1)));
    }

    // ---------- Scope and numbering ----------

    [Fact]
    public void a_scope_of_separators_only_means_no_filter()
    {
        Assert.Equal("xa.txt", Run("a.txt", new InsertRule { Text = "x", ScopePattern = ";" }));
    }

    [Fact]
    public void numbering_with_a_filter_has_no_gaps()
    {
        var rule = new NumberingRule { ScopePattern = "*.jpg", Separator = " " };
        var (res, d) = Preview(["a.jpg", "b.txt", "c.jpg", "d.jpg"], rule);
        using (d) Assert.Equal(["01 a.jpg", "b.txt", "02 c.jpg", "03 d.jpg"], res.Items.Select(i => i.NewName));
    }

    // ---------- Characters ----------

    [Fact]
    public void cutting_never_splits_an_emoji()
    {
        Assert.Equal("ab.txt", Run("ab\U0001F600.txt", new CleanupRule { MaxLength = 3 }));
        Assert.Equal("a.txt", Run("a\U0001F600.txt", new RemoveRule { Start = 2, Count = 1 }));
    }

    [Fact]
    public void nested_brackets_are_removed_completely()
    {
        Assert.Equal("Album.mp3", Run("Album (2010 (remaster)).mp3", new CleanupRule { RemoveRoundBrackets = true }));
    }

    [Fact]
    public void reserved_device_names_are_invalid()
    {
        foreach (var n in new[] { "COM¹", "LPT³.txt", "CONIN$", "conout$.log", "COM0" })
            Assert.NotNull(NameValidator.CheckName(n));
        Assert.Null(NameValidator.CheckName("COMPUTER.txt"));
    }

    // ---------- Regional settings ----------

    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public void dates_use_the_gregorian_calendar_whatever_the_region(string culture)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            using var d = new TempDir();
            var path = d.File("a.txt");
            File.SetLastWriteTime(path, new DateTime(2026, 11, 10, 9, 0, 0));
            var e = new FileEntry(path);
            Assert.Equal("2026-11-10.txt", RenamePipeline.Run(e, [new TemplateRule { Template = "<modified:yyyy-MM-dd>" }], new RuleContext(e, 0, 0, 1)));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void change_case_ignores_the_windows_language_unless_asked()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal("file index.txt", Run("FILE INDEX.txt", new CaseRule { Mode = CaseMode.Lower }));
            Assert.Equal("fıle ındex.txt", Run("FILE INDEX.txt", new CaseRule { Mode = CaseMode.Lower, UseLanguageRules = true }));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void now_is_the_same_for_every_file_in_a_preview()
    {
        var (res, d) = Preview(Enumerable.Range(0, 50).Select(i => $"f{i}.txt").ToArray(), new InsertRule { Text = "<now:HHmmssfffffff> ", Position = InsertPosition.Prefix });
        using (d) Assert.Single(res.Items.Select(i => i.NewName.Split(' ')[0]).Distinct());
    }

    // ---------- Transliterate ----------

    [Fact]
    public void transliterate_handles_serbian_macedonian_and_belarusian_letters()
    {
        Assert.Equal("Jovan.txt", Run("Јован.txt", new TransliterateRule { Alphabet = TranslitAlphabet.Cyrillic }));
        Assert.Equal("kjuka.txt", Run("ќука.txt", new TransliterateRule { Alphabet = TranslitAlphabet.Cyrillic }));
        Assert.Equal("Minsku.txt", Run("Мінскў.txt", new TransliterateRule { Alphabet = TranslitAlphabet.Cyrillic }));
    }

    [Fact]
    public void everything_to_ascii_keeps_what_it_cannot_convert()
    {
        var rule = new TransliterateRule();
        Assert.Equal("写真.jpg", Run("写真.jpg", rule));
        Assert.Equal("Cafe 写真 Moskva.jpg", Run("Café 写真 Москва.jpg", new TransliterateRule()));
        Assert.Equal("שלום.txt", Run("שלום.txt", new TransliterateRule()));
    }

    // ---------- Move and Rearrange ----------

    [Fact]
    public void move_takes_one_separator_with_the_piece()
    {
        Assert.Equal("Trip 2024.jpg", Run("2024 - Trip.jpg", new MoveRule { Count = 4, Destination = MoveDestination.End }));
        Assert.Equal("2024-12-31 Invoice_Acme.pdf", Run("Invoice_2024-12-31_Acme.pdf",
            new MoveRule { Source = MoveSource.Text, Text = @"\d{4}-\d{2}-\d{2}", TextIsRegex = true, Destination = MoveDestination.Start }));
        Assert.Equal("a  b c.txt", Run("c a  b.txt", new MoveRule { Count = 1, Destination = MoveDestination.End }));
    }

    [Fact]
    public void rearrange_leaves_names_without_the_delimiter_alone()
    {
        Assert.Equal("Holiday.jpg", Run("Holiday.jpg", new RearrangeRule()));
        Assert.Equal("Leftb.txt", Run("a - b.txt", new RearrangeRule { Pattern = "Left$2" }));
        Assert.Equal("Left b.txt", Run("a - b.txt", new RearrangeRule { Pattern = "Left $L" }));
    }

    // ---------- Matching ----------

    [Fact]
    public void the_same_episode_of_a_different_show_is_only_a_weak_guess()
    {
        var m = NameMatcher.Match(["Breaking.Bad.S01E01.720p"], ["The Wire - 01x01 - The Target", "Better Call Saul - 01x02 - Mijo"]);
        Assert.NotEqual(MatchKind.Episode, m[0].Kind);
        Assert.True(m[0].Score < 0.6);
        var good = NameMatcher.Match(["Breaking.Bad.S01E01.720p"], ["Breaking Bad - 01x01 - Pilot"]);
        Assert.Equal(MatchKind.Episode, good[0].Kind);
        var bare = NameMatcher.Match(["S01E01"], ["Breaking Bad - 01x01 - Pilot"]);
        Assert.Equal(MatchKind.Episode, bare[0].Kind);
    }

    [Fact]
    public void naming_style_reads_acronyms_print_sizes_and_release_tags()
    {
        Assert.Null(NameStyle.Parse("Photo 10x15 print").Episode);
        var mash = NameStyle.Parse("M.A.S.H.S01E01.iNTERNAL.720p");
        Assert.Equal("M.A.S.H", mash.ShowText);
        Assert.Equal(string.Empty, mash.TitleText);
        var styled = NameStyle.Apply("<show> - <season:00>x<episode:00>", mash, " ", WordCase.Title, smallWordsLower: true);
        Assert.Equal("M.A.S.H - 01x01", styled.Name);
    }
}
