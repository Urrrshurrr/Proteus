using System.IO.Compression;
using System.Text;
using Proteus.Core.Metadata;

namespace Proteus.Tests;

/// <summary>Document properties read from hand-made files.</summary>
public class DocumentReaderTests
{
    private static string Pdf(TempDir d, string body, string trailer, string name = "doc.pdf")
    {
        var text = "%PDF-1.4\n" + body + "\ntrailer\n" + trailer + "\nstartxref\n0\n%%EOF\n";
        var path = Path.Combine(d.Path, name);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text));
        return path;
    }

    private const string Pages = "2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>\nendobj\n";

    [Fact]
    public void the_title_comes_from_the_info_object_not_from_bookmarks()
    {
        using var d = new TempDir();
        var path = Pdf(d,
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Outlines 7 0 R >>\nendobj\n" + Pages +
            "6 0 obj\n<< /Title (Annual Report 2024) /Author (Finance) /CreationDate (D:20240101120000Z) >>\nendobj\n" +
            "8 0 obj\n<< /Title (Introduction) /Parent 7 0 R /Next 9 0 R >>\nendobj\n" +
            "9 0 obj\n<< /Title (Appendix B \\(tables\\)) /Parent 7 0 R >>\nendobj\n",
            "<< /Size 10 /Root 1 0 R /Info 6 0 R >>");
        var map = DocumentReader.Read(path);
        Assert.Equal("Annual Report 2024", map["title"]);
        Assert.Equal("Finance", map["author"]);
        Assert.Equal("3", map["pages"]);
    }

    [Fact]
    public void titles_with_brackets_are_read_whole()
    {
        using var d = new TempDir();
        var path = Pdf(d, Pages + "6 0 obj\n<< /Title (Results (final) for Q3) >>\nendobj\n", "<< /Info 6 0 R >>");
        Assert.Equal("Results (final) for Q3", DocumentReader.Read(path)["title"]);
    }

    [Fact]
    public void the_page_count_is_found_whatever_the_key_order()
    {
        using var d = new TempDir();
        var path = Pdf(d, "2 0 obj\n<< /Count 12 /Kids [3 0 R] /Type /Pages >>\nendobj\n", "<< /Size 3 >>");
        Assert.Equal("12", DocumentReader.Read(path)["pages"]);
    }

    [Fact]
    public void encrypted_pdfs_give_no_text()
    {
        using var d = new TempDir();
        var path = Pdf(d, Pages + "6 0 obj\n<< /Title (é§þ garbled) >>\nendobj\n", "<< /Info 6 0 R /Encrypt 9 0 R >>");
        Assert.False(DocumentReader.Read(path).ContainsKey("title"));
    }

    [Fact]
    public void pdf_dates_respect_the_time_zone()
    {
        using var d = new TempDir();
        var path = Pdf(d, "6 0 obj\n<< /CreationDate (D:20240101120000+02'00') >>\nendobj\n", "<< /Info 6 0 R >>");
        var expected = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)).LocalDateTime;
        Assert.Equal(expected, DocumentReader.Read(path)["created"]);
    }

    [Fact]
    public void an_oversized_office_part_is_refused_quickly()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "bomb.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("docProps/core.xml", CompressionLevel.SmallestSize);
            using var w = new StreamWriter(entry.Open());
            w.Write("<?xml version=\"1.0\"?><cp:coreProperties xmlns:cp=\"x\" xmlns:dc=\"y\"><dc:title>Big</dc:title><!--");
            w.Write(new string(' ', 20 * 1024 * 1024));
            w.Write("--></cp:coreProperties>");
        }
        Assert.True(new FileInfo(path).Length < 1024 * 1024);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var map = DocumentReader.Read(path);
        Assert.False(map.ContainsKey("title"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }

    [Fact]
    public void a_normal_office_file_still_reads()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "ok.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("docProps/core.xml");
            using var w = new StreamWriter(entry.Open());
            w.Write("<?xml version=\"1.0\"?><cp:coreProperties xmlns:cp=\"x\" xmlns:dc=\"y\"><dc:title>Plan</dc:title><dc:creator>Sam</dc:creator></cp:coreProperties>");
        }
        var map = DocumentReader.Read(path);
        Assert.Equal("Plan", map["title"]);
        Assert.Equal("Sam", map["author"]);
    }
}
