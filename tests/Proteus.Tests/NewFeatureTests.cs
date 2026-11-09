using System.IO.Compression;
using System.Text;
using Proteus.Core.Engine;
using Proteus.Core.Metadata;
using Proteus.Core.Models;
using Proteus.Core.Presets;
using Proteus.Core.Rules;
using Proteus.Core.Tokens;

namespace Proteus.Tests;

public class NewRuleTests
{
    private static string Run(string fileName, params RenameRule[] rules)
    {
        using var dir = new TempDir();
        var entry = new FileEntry(dir.File(fileName));
        return RenamePipeline.Run(entry, rules, new RuleContext(entry, 0, 0, 1));
    }

    [Fact]
    public void Rearrange_swaps_parts()
    {
        Assert.Equal("Title - Artist.mp3", Run("Artist - Title.mp3", new RearrangeRule()));
        Assert.Equal("c b a.txt", Run("a b c.txt", new RearrangeRule { Delimiter = "", Pattern = "$3 $2 $1" }));
        Assert.Equal("c-a.txt", Run("a-b-c.txt", new RearrangeRule { Delimiter = "-", Pattern = "$L-$1" }));
        Assert.Equal("b.txt", Run("a-b-c.txt", new RearrangeRule { Delimiter = "-", Pattern = "$-2" }));
    }

    [Fact]
    public void Crop_modes()
    {
        Assert.Equal("Movie (", Run("Movie (2010) x.mkv", new CropRule { Marker = "(" }).Replace(".mkv", ""));
        Assert.Equal("Movie.mkv", Run("Movie (2010) x.mkv", new CropRule { Marker = " (", IncludeMarker = true }));
        Assert.Equal("2010.mkv", Run("Movie (2010) x.mkv", new CropRule { Mode = CropMode.KeepBetween, Marker = "(", EndMarker = ")" }));
        Assert.Equal("Movie  x.mkv", Run("Movie (2010) x.mkv", new CropRule { Mode = CropMode.RemoveBetween, Marker = "(", EndMarker = ")", IncludeMarker = true }));
        Assert.Equal("b_c.txt", Run("a_b_c.txt", new CropRule { Mode = CropMode.RemoveBefore, Marker = "_", IncludeMarker = true }));
        Assert.Equal("c.txt", Run("a_b_c.txt", new CropRule { Mode = CropMode.RemoveBefore, Marker = "_", IncludeMarker = true, UseLastMatch = true }));
    }

    [Fact]
    public void Move_characters_and_text()
    {
        Assert.Equal("Report 2024.pdf", Run("2024 Report.pdf", new MoveRule { Count = 4 }));
        Assert.Equal("2024 Report.pdf", Run("Report 2024.pdf", new MoveRule { Source = MoveSource.Text, Text = @"\d{4}", TextIsRegex = true, Destination = MoveDestination.Start }));
        Assert.Equal("2024 Report.pdf", Run("Report 2024.pdf", new MoveRule { Count = 4, FromEnd = true, Destination = MoveDestination.Start }));
    }

    [Fact]
    public void Reformat_dates()
    {
        Assert.Equal("Invoice 2024-12-31.pdf", Run("Invoice 31.12.2024.pdf", new ReformatDateRule()));
        Assert.Equal("Trip 2023-03-05.jpg", Run("Trip 5 Mar 2023.jpg", new ReformatDateRule { SourceFormats = "d MMM yyyy" }));
        Assert.Equal("x 99.99.2024.txt", Run("x 99.99.2024.txt", new ReformatDateRule())); // not a date: untouched
        Assert.Equal("20241231 a.txt", Run("12-31-2024 a.txt", new ReformatDateRule { SourceFormats = "MM-dd-yyyy", TargetFormat = "yyyyMMdd" }));
    }

    [Fact]
    public void Transliterate()
    {
        Assert.Equal("Moskva.txt", Run("Москва.txt", new TransliterateRule { Alphabet = TranslitAlphabet.Cyrillic }));
        Assert.Equal("Athina.txt", Run("Αθηνα.txt", new TransliterateRule { Alphabet = TranslitAlphabet.Greek }));
        Assert.Equal("Muenchen Strasse.txt", Run("München Straße.txt", new TransliterateRule { Alphabet = TranslitAlphabet.German }));
        Assert.Equal("Cafe Aeroskobing Lodz.txt", Run("Café Ærøskøbing Łódź.txt", new TransliterateRule()));
        Assert.Equal("Tom and Jerry.txt", Run("Tom & Jerry.txt", new TransliterateRule { CustomPairs = " & = and " }));
    }

    [Fact]
    public void Randomize_is_stable_and_reshuffles()
    {
        var r = new RandomizeRule { Length = 8, Charset = RandomCharset.Digits };
        var a = Run("a.txt", r);
        Assert.Matches(@"^a_\d{8}\.txt$", a);
        Assert.Equal(a.Length, Run("a.txt", r).Length);
        using var dir = new TempDir();
        var e = new FileEntry(dir.File("a.txt"));
        var ctx = new RuleContext(e, 0, 0, 1);
        var first = RenamePipeline.Run(e, [r], ctx);
        Assert.Equal(first, RenamePipeline.Run(e, [r], ctx));
        r.Seed = 42;
        Assert.NotEqual(first, RenamePipeline.Run(e, [r], ctx));
    }

    [Fact]
    public void List_in_order_and_mapping()
    {
        using var dir = new TempDir();
        var files = new[] { new FileEntry(dir.File("x.jpg")), new FileEntry(dir.File("y.jpg")), new FileEntry(dir.File("z.jpg")) };
        var list = new ListRule { Lines = "First\n\nSecond <n:2>\n" };
        var res = RenamePipeline.Preview(files, [true, true, true], [list]);
        Assert.Equal(["First.jpg", "Second 02.jpg", "z.jpg"], res.Items.Select(i => i.NewName));

        var map = new ListRule { Mode = ListMode.Mapping, Lines = "y.jpg -> Why.jpg\nz\tZed\nx.jpg,Ex" };
        res = RenamePipeline.Preview(files, [true, true, true], [map]);
        Assert.Equal(["Ex.jpg", "Why.jpg", "Zed.jpg"], res.Items.Select(i => i.NewName));
        Assert.Equal(3, map.LineCount);
    }

    [Fact]
    public void Unpad_and_max_length()
    {
        Assert.Equal("Track 7 of 12.mp3", Run("Track 007 of 012.mp3", new PadNumbersRule { RemovePadding = true }));
        Assert.Equal("0.txt", Run("000.txt", new PadNumbersRule { RemovePadding = true }));
        Assert.Equal("A very.txt", Run("A very long name.txt", new CleanupRule { MaxLength = 7 }));
    }

    [Fact]
    public void New_rules_round_trip_in_presets()
    {
        using var dir = new TempDir();
        var store = new PresetStore(dir.Path);
        RenameRule[] rules =
        [
            new RearrangeRule { Pattern = "$1" }, new CropRule { Mode = CropMode.KeepBetween }, new MoveRule { Destination = MoveDestination.Start },
            new ReformatDateRule { TargetFormat = "yy" }, new TransliterateRule { Alphabet = TranslitAlphabet.Greek },
            new RandomizeRule { Seed = 5 }, new ListRule { Mode = ListMode.Mapping, Lines = "a,b" },
        ];
        store.Save("new", rules);
        var loaded = store.Load("new")!;
        Assert.Equal(rules.Select(r => r.GetType()), loaded.Rules.Select(r => r.GetType()));
        Assert.Equal(5, ((RandomizeRule)loaded.Rules[5]).Seed);
        Assert.Equal("a,b", ((ListRule)loaded.Rules[6]).Lines);
    }
}

public class MetadataTokenTests
{
    private static string Expand(string template, FileEntry e) => TokenEngine.Expand(template, new RuleContext(e, 0, 0, 1));

    [Fact]
    public void Office_document_properties()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "report.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                w.Write(xml);
            }
            Add("docProps/core.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                  xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/">
                  <dc:title>Quarterly Results: Q3</dc:title><dc:creator>Jane Doe</dc:creator>
                  <cp:lastModifiedBy>John Roe</cp:lastModifiedBy><cp:revision>7</cp:revision>
                  <dcterms:created>2024-07-01T09:00:00Z</dcterms:created>
                </cp:coreProperties>
                """);
            Add("docProps/app.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties">
                  <Pages>12</Pages><Words>3400</Words><Company>Acme Corp</Company>
                </Properties>
                """);
        }
        var e = new FileEntry(path);
        Assert.Equal("Quarterly Results- Q3", Expand("<doc:title>", e));
        Assert.Equal("Jane Doe | John Roe | Acme Corp", Expand("<doc:author> | <doc:lastauthor> | <doc:company>", e));
        Assert.Equal("012 pages, 3400 words, rev 7", Expand("<doc:pages:3> pages, <doc:words> words, rev <doc:revision>", e));
        Assert.Equal("2024", Expand("<doc:created:yyyy>", e));
    }

    [Fact]
    public void Pdf_properties()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "paper.pdf");
        var pdf = "%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n" +
                  "2 0 obj << /Type /Pages /Kids [3 0 R] /Count 5 >> endobj\n" +
                  "9 0 obj << /Title (Deep \\(Learning\\)) /Author <FEFF004100640061> /CreationDate (D:20210315120000Z) >> endobj\n" +
                  "trailer << /Info 9 0 R >>\n%%EOF";
        File.WriteAllText(path, pdf, Encoding.Latin1);
        var e = new FileEntry(path);
        Assert.Equal("Deep (Learning)", Expand("<doc:title>", e));
        Assert.Equal("Ada", Expand("<doc:author>", e));
        Assert.Equal("5", Expand("<doc:pages>", e));
        Assert.Equal("2021-03-15", Expand("<doc:created:yyyy-MM-dd>", e));
    }

    [Fact]
    public void Hashes_size_firstline()
    {
        using var dir = new TempDir();
        var e = new FileEntry(dir.File("notes.txt", "\n\n# Shopping list\nmilk"));
        Assert.Equal("Shopping list", Expand("<firstline>", e));
        Assert.Equal(8, Expand("<hash:md5:8>", e).Length);
        Assert.Equal(64, Expand("<hash:sha256>", e).Length);
        Assert.Matches("^[0-9a-f]{8}$", Expand("<crc32>", e));
        Assert.Equal(new FileInfo(e.FullPath).Length.ToString(), Expand("<size:b>", e));
    }

    [Fact]
    public void Duration_formatting()
    {
        var d = new TimeSpan(1, 2, 3);
        Assert.Equal("01-02-03", TokenEngine.FormatDuration(d, null));
        Assert.Equal("02-03", TokenEngine.FormatDuration(new TimeSpan(0, 2, 3), null));
        Assert.Equal("1h02m", TokenEngine.FormatDuration(d, @"h\hmm\m"));
        Assert.Equal(string.Empty, TokenEngine.FormatDuration(null, null));
    }

    [Fact]
    public void Windows_properties_for_a_system_sound()
    {
        // Every Windows install has these; the shell reports their length.
        var wav = Directory.Exists(@"C:\Windows\Media") ? Directory.GetFiles(@"C:\Windows\Media", "*.wav").FirstOrDefault() : null;
        if (wav == null) return;
        var e = new FileEntry(wav);
        Assert.Matches(@"^\d\d-\d\d$", Expand("<audio:duration>", e));
        Assert.NotEqual(string.Empty, Expand("<type>", e));
        Assert.NotEqual(string.Empty, Expand("<prop:System.ItemTypeText>", e));
    }

    [Fact]
    public void Missing_metadata_expands_to_empty()
    {
        using var dir = new TempDir();
        var e = new FileEntry(dir.File("plain.txt"));
        Assert.Equal("[][][]", Expand("[<video:resolution>][<doc:title>][<music:artist>]", e));
    }
}
