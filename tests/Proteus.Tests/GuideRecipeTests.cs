using Proteus.Core.Engine;
using Proteus.Core.Models;
using Proteus.Core.Rules;

namespace Proteus.Tests;

/// <summary>Keeps the examples in docs/guide honest: each recipe is run exactly as written there.</summary>
public class GuideRecipeTests
{
    private static string Run(string fileName, params RenameRule[] rules)
    {
        using var dir = new TempDir();
        var e = new FileEntry(dir.File(fileName));
        var (name, problem) = RenamePipeline.RunSafe(e, rules, new RuleContext(e, 0, 0, 1));
        Assert.Null(problem);
        return name;
    }

    [Fact]
    public void Tv_episodes_with_rules() => Assert.Equal("Show Name - S01E02.mkv", Run("Show.Name.s01e02.1080p.WEB.mkv",
        new CleanupRule { DotsToSpaces = true },
        new ReplaceRule { Find = @"^(.*?) s(\d+)e(\d+).*$", ReplaceWith = "$1 - S$2E$3", UseRegex = true },
        new CaseRule { Mode = CaseMode.Title },
        new ReplaceRule { Find = @"S(\d+)e(\d+)", ReplaceWith = "S$1E$2", UseRegex = true, CaseSensitive = true }));

    [Fact]
    public void Tv_episodes_with_script() => Assert.Equal("Show Name - S01E02.mkv", Run("Show.Name.s1e2.1080p.WEB.mkv",
        new ScriptRule
        {
            Script = "const m = name.match(/^(.*?)[ ._-]+s(\\d{1,2})e(\\d{1,3})/i);\nif (m) {\n  const show = m[1].replace(/[._]/g, ' ').trim();\n  name = `${show} - S${pad(m[2], 2)}E${pad(m[3], 2)}`;\n}",
        }));

    [Fact]
    public void Season_example_chip() => Assert.Equal("Show S01E02 Title.mkv", Run("Show 1x02 Title.mkv",
        new ScriptRule { Script = "name = name.replace(/(\\d{1,2})x(\\d{1,3})/i, (m, s, e) => 'S' + pad(s, 2) + 'E' + pad(e, 2));" }));

    [Fact]
    public void Title_case_script() => Assert.Equal("My Holiday-Photo.jpg", Run("my HOLIDAY-photo.jpg",
        new ScriptRule { Script = "name = name.toLowerCase().replace(/(^|[\\s_-])(\\p{L})/gu, (m, sep, ch) => sep + ch.toUpperCase());" }));

    [Fact]
    public void Slug_script() => Assert.Equal("cafe-del-mar-2024.jpg", Run("Café del Mar (2024).JPG",
        new ScriptRule { Script = "name = name.normalize('NFD').replace(/[\\u0300-\\u036f]/g, '')\n  .toLowerCase()\n  .replace(/[^a-z0-9]+/g, '-')\n  .replace(/^-+|-+$/g, '');\next = ext.toLowerCase();" }));

    [Fact]
    public void Group_numbering_script()
    {
        using var dir = new TempDir();
        var e = new FileEntry(dir.File("x.txt"));
        var rule = new ScriptRule { Script = "const group = String.fromCharCode(65 + Math.floor(index / 10));\nname = group + '-' + pad(index % 10 + 1, 2) + ' ' + name;" };
        Assert.Equal("B-03 x.txt", RenamePipeline.Run(e, [rule], new RuleContext(e, 12, 0, 20)));
    }

    [Fact]
    public void Date_script_uses_js_dates()
    {
        using var dir = new TempDir();
        var path = dir.File("a.txt");
        File.SetLastWriteTime(path, new DateTime(2024, 3, 9, 10, 0, 0));
        var e = new FileEntry(path);
        var rule = new ScriptRule { Script = "const d = modified;\nconst two = n => String(n).padStart(2, '0');\nname = `${d.getFullYear()}${two(d.getMonth() + 1)}${two(d.getDate())}_${name}`;" };
        Assert.Equal("20240309_a.txt", RenamePipeline.Run(e, [rule], new RuleContext(e, 0, 0, 1)));
    }

    [Fact]
    public void Web_download_cleanup() => Assert.Equal("My Report Final.pdf", Run("My%20Report_FINAL(1).pdf",
        new CleanupRule { RemoveRoundBrackets = true }, new ReplaceRule { Find = "final", ReplaceWith = "Final" }));

    [Fact]
    public void Remove_prefix() => Assert.Equal("0001.jpg", Run("DSC_0001.jpg",
        new CropRule { Mode = CropMode.RemoveBefore, Marker = "_", IncludeMarker = true }));

    [Fact]
    public void Invoice_dates_to_front() => Assert.Equal("2024-12-31 Invoice Acme.pdf", Run("Invoice 31.12.2024 Acme.pdf",
        new ReformatDateRule { SourceFormats = "dd.MM.yyyy" },
        new MoveRule { Source = MoveSource.Text, Text = @"\d{4}-\d{2}-\d{2}", TextIsRegex = true, Destination = MoveDestination.Start }));

    [Fact]
    public void Rule_reference_examples()
    {
        Assert.Equal("2024-0105.txt", Run("20240105.txt", new InsertRule { Text = "-", Position = InsertPosition.AtPosition, Index = 5 }));
        Assert.Equal("Tolkien, John.txt", Run("John Ronald Tolkien.txt", new RearrangeRule { Delimiter = "", Pattern = "$L, $1" }));
        Assert.Equal("Movie Name 2010.mkv", Run("Movie.Name.2010.[1080p]_(x264).mkv",
            new CleanupRule { DotsToSpaces = true, RemoveSquareBrackets = true, RemoveRoundBrackets = true }));
        Assert.Equal("The Quick Brown-Fox.txt", Run("the QUICK brown-fox.txt", new CaseRule()));
        Assert.Equal("a  c.txt", Run("a (b) c.txt", new CropRule { Mode = CropMode.RemoveBetween, Marker = "(", EndMarker = ")", IncludeMarker = true }));
    }
}
