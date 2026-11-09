using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Proteus.Core.Metadata;

/// <summary>
/// Reads document properties straight from the file, without Office or Acrobat installed:
/// Office Open XML (docx/xlsx/pptx and macro variants), OpenDocument (odt/ods/odp) and PDF.
/// Keys: title, subject, author, lastauthor, keywords, category, description, company, manager,
/// app, revision, pages, words, slides, created (DateTime), modified (DateTime).
/// </summary>
public static class DocumentReader
{
    private static readonly HashSet<string> Ooxml = new(StringComparer.OrdinalIgnoreCase)
    { ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".xltx", ".pptx", ".pptm", ".potx", ".vsdx" };
    private static readonly HashSet<string> Odf = new(StringComparer.OrdinalIgnoreCase)
    { ".odt", ".ods", ".odp", ".odg", ".ott" };

    public static bool Handles(string extension) =>
        Ooxml.Contains(extension) || Odf.Contains(extension) || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public static Dictionary<string, object> Read(string path)
    {
        var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var ext = Path.GetExtension(path);
            if (Ooxml.Contains(ext)) ReadOoxml(path, map);
            else if (Odf.Contains(ext)) ReadOdf(path, map);
            else if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) ReadPdf(path, map);
        }
        catch (Exception) { /* unreadable or encrypted: no document metadata */ }
        return map;
    }

    // ---------- Office Open XML ----------
    private static void ReadOoxml(string path, Dictionary<string, object> map)
    {
        using var zip = ZipFile.OpenRead(path);
        var core = zip.GetEntry("docProps/core.xml");
        if (core != null)
        {
            using var s = core.Open();
            var x = XDocument.Load(s);
            foreach (var e in x.Root?.Elements() ?? [])
            {
                var v = e.Value.Trim();
                if (v.Length == 0) continue;
                switch (e.Name.LocalName)
                {
                    case "title": map["title"] = v; break;
                    case "subject": map["subject"] = v; break;
                    case "creator": map["author"] = v; break;
                    case "keywords": map["keywords"] = v; break;
                    case "description": map["description"] = v; break;
                    case "lastModifiedBy": map["lastauthor"] = v; break;
                    case "revision": map["revision"] = v; break;
                    case "category": map["category"] = v; break;
                    case "created": if (TryDate(v, out var c)) map["created"] = c; break;
                    case "modified": if (TryDate(v, out var m)) map["modified"] = m; break;
                }
            }
        }
        var app = zip.GetEntry("docProps/app.xml");
        if (app != null)
        {
            using var s = app.Open();
            var x = XDocument.Load(s);
            foreach (var e in x.Root?.Elements() ?? [])
            {
                var v = e.Value.Trim();
                if (v.Length == 0) continue;
                switch (e.Name.LocalName)
                {
                    case "Pages": map["pages"] = v; break;
                    case "Words": map["words"] = v; break;
                    case "Slides": map["slides"] = v; break;
                    case "Company": map["company"] = v; break;
                    case "Manager": map["manager"] = v; break;
                    case "Application": map["app"] = v; break;
                }
            }
        }
    }

    // ---------- OpenDocument ----------
    private static void ReadOdf(string path, Dictionary<string, object> map)
    {
        using var zip = ZipFile.OpenRead(path);
        var meta = zip.GetEntry("meta.xml");
        if (meta == null) return;
        using var s = meta.Open();
        var x = XDocument.Load(s);
        foreach (var e in x.Descendants())
        {
            var v = e.Value.Trim();
            switch (e.Name.LocalName)
            {
                case "title" when v.Length > 0: map["title"] = v; break;
                case "subject" when v.Length > 0: map["subject"] = v; break;
                case "initial-creator" when v.Length > 0: map["author"] = v; break;
                case "creator" when v.Length > 0: map["lastauthor"] = v; map.TryAdd("author", v); break;
                case "keyword" when v.Length > 0:
                    map["keywords"] = map.TryGetValue("keywords", out var k) ? k + "; " + v : v; break;
                case "description" when v.Length > 0: map["description"] = v; break;
                case "editing-cycles" when v.Length > 0: map["revision"] = v; break;
                case "generator" when v.Length > 0: map["app"] = v; break;
                case "creation-date": if (TryDate(v, out var c)) map["created"] = c; break;
                case "date": if (TryDate(v, out var d)) map["modified"] = d; break;
                case "document-statistic":
                    foreach (var a in e.Attributes())
                    {
                        if (a.Name.LocalName == "page-count") map["pages"] = a.Value;
                        else if (a.Name.LocalName == "word-count") map["words"] = a.Value;
                    }
                    break;
            }
        }
    }

    // ---------- PDF ----------
    private static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(1);
    private static readonly Regex PdfInfoField = new(@"/(Title|Author|Subject|Keywords|Creator|Producer|CreationDate|ModDate)\s*(\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]*>)", RegexOptions.Compiled, RegexBudget);
    private static readonly Regex PdfPageCount = new(@"/Type\s*/Pages\b[^>]*?/Count\s+(\d+)", RegexOptions.Compiled | RegexOptions.Singleline, RegexBudget);
    private static readonly Regex XmpTitle = new(@"<dc:title>.{0,2000}?<rdf:li[^>]*>(.*?)</rdf:li>", RegexOptions.Compiled | RegexOptions.Singleline, RegexBudget);
    private static readonly Regex XmpCreator = new(@"<dc:creator>.{0,2000}?<rdf:li[^>]*>(.*?)</rdf:li>", RegexOptions.Compiled | RegexOptions.Singleline, RegexBudget);
    private static readonly Regex XmpCreate = new(@"<xmp:CreateDate>(.*?)</xmp:CreateDate>|xmp:CreateDate=""([^""]+)""", RegexOptions.Compiled, RegexBudget);

    private static void ReadPdf(string path, Dictionary<string, object> map)
    {
        // Metadata normally sits near the start or the end; read both ends of large files.
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            const int head = 4 * 1024 * 1024, tail = 2 * 1024 * 1024;
            byte[] bytes;
            if (fs.Length <= head + tail)
            {
                bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
            }
            else
            {
                bytes = new byte[head + tail];
                fs.ReadExactly(bytes, 0, head);
                fs.Seek(-tail, SeekOrigin.End);
                fs.ReadExactly(bytes, head, tail);
            }
            text = Encoding.Latin1.GetString(bytes);
        }

        foreach (Match m in PdfInfoField.Matches(text))
        {
            var key = m.Groups[1].Value;
            var value = DecodePdfString(m.Groups[2].Value).Trim();
            if (value.Length == 0) continue;
            switch (key)
            {
                case "Title": map["title"] = value; break;
                case "Author": map["author"] = value; break;
                case "Subject": map["subject"] = value; break;
                case "Keywords": map["keywords"] = value; break;
                case "Creator": map["app"] = value; break;
                case "Producer": map.TryAdd("app", value); break;
                case "CreationDate": if (TryPdfDate(value, out var c)) map["created"] = c; break;
                case "ModDate": if (TryPdfDate(value, out var d)) map["modified"] = d; break;
            }
        }

        int pages = 0;
        foreach (Match m in PdfPageCount.Matches(text))
            if (int.TryParse(m.Groups[1].Value, out var n)) pages = Math.Max(pages, n);
        if (pages > 0) map["pages"] = pages.ToString(CultureInfo.InvariantCulture);

        // XMP packet (UTF-8) as a fallback when the Info dictionary is compressed.
        if (!map.ContainsKey("title") && XmpTitle.Match(text) is { Success: true } t) map["title"] = Utf8(t.Groups[1].Value);
        if (!map.ContainsKey("author") && XmpCreator.Match(text) is { Success: true } a) map["author"] = Utf8(a.Groups[1].Value);
        if (!map.ContainsKey("created") && XmpCreate.Match(text) is { Success: true } cd)
        {
            var v = cd.Groups[1].Success && cd.Groups[1].Value.Length > 0 ? cd.Groups[1].Value : cd.Groups[2].Value;
            if (TryDate(v, out var dt)) map["created"] = dt;
        }
    }

    private static string Utf8(string latin1) =>
        System.Net.WebUtility.HtmlDecode(Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(latin1))).Trim();

    private static string DecodePdfString(string raw)
    {
        byte[] bytes;
        if (raw.StartsWith('<'))
        {
            var hex = new string(raw.Where(Uri.IsHexDigit).ToArray());
            if (hex.Length % 2 == 1) hex += "0";
            bytes = Convert.FromHexString(hex);
        }
        else
        {
            var body = raw[1..^1];
            var list = new List<byte>(body.Length);
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c != '\\' || i + 1 >= body.Length) { list.Add((byte)c); continue; }
                char n = body[++i];
                switch (n)
                {
                    case 'n': list.Add((byte)'\n'); break;
                    case 'r': list.Add((byte)'\r'); break;
                    case 't': list.Add((byte)'\t'); break;
                    case 'b': list.Add(8); break;
                    case 'f': list.Add(12); break;
                    case '\r': case '\n': break;
                    default:
                        if (n is >= '0' and <= '7')
                        {
                            int v = n - '0', k = 1;
                            while (k < 3 && i + 1 < body.Length && body[i + 1] is >= '0' and <= '7') { v = v * 8 + (body[++i] - '0'); k++; }
                            list.Add((byte)v);
                        }
                        else list.Add((byte)n);
                        break;
                }
            }
            bytes = list.ToArray();
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.Latin1.GetString(bytes);
    }

    private static bool TryPdfDate(string s, out DateTime date)
    {
        // D:YYYYMMDDHHmmSSOHH'mm'
        date = default;
        var m = Regex.Match(s, @"(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?");
        if (!m.Success) return false;
        int G(int i, int d) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : d;
        try
        {
            date = new DateTime(G(1, 1), Math.Clamp(G(2, 1), 1, 12), Math.Clamp(G(3, 1), 1, 28 + 3), G(4, 0) % 24, G(5, 0) % 60, G(6, 0) % 60);
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static bool TryDate(string s, out DateTime date)
    {
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            date = dto.LocalDateTime;
            return true;
        }
        date = default;
        return false;
    }
}
