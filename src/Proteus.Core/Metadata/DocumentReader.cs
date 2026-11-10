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

    /// <summary>Metadata parts are a few KB; anything far bigger is refused rather than read into memory.</summary>
    private const int MaxXmlEntry = 4 * 1024 * 1024;

    private static ZipArchive OpenZip(string path) => new(SharedFile.OpenRead(path), ZipArchiveMode.Read, leaveOpen: false);

    /// <summary>Loads a small XML part of an Office or OpenDocument file, or null when it is missing or too big.</summary>
    private static XDocument? LoadXml(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry == null || entry.Length > MaxXmlEntry) return null;
        using var s = entry.Open();
        // Never trust the size in the zip header: stop reading past the limit whatever it says.
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = s.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > MaxXmlEntry) return null;
        }
        buffer.Position = 0;
        return XDocument.Load(buffer);
    }

    // ---------- Office Open XML ----------
    private static void ReadOoxml(string path, Dictionary<string, object> map)
    {
        using var zip = OpenZip(path);
        var x = LoadXml(zip, "docProps/core.xml");
        if (x != null)
        {
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
        var app = LoadXml(zip, "docProps/app.xml");
        if (app != null)
        {
            foreach (var e in app.Root?.Elements() ?? [])
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
        using var zip = OpenZip(path);
        var x = LoadXml(zip, "meta.xml");
        if (x == null) return;
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
    private static readonly Regex InfoRef = new(@"/Info\s+(\d+)\s+(\d+)\s+R", RegexOptions.Compiled, RegexBudget);
    private static readonly Regex Encrypt = new(@"/Encrypt\s*(?:\d+\s+\d+\s+R|<<)", RegexOptions.Compiled, RegexBudget);
    private static readonly Regex PagesType = new(@"/Type\s*/Pages\b", RegexOptions.Compiled, RegexBudget);
    private static readonly Regex CountKey = new(@"/Count\s+(\d+)", RegexOptions.Compiled, RegexBudget);
    private static readonly Regex XmpTitle = new(@"<dc:title>.{0,2000}?<rdf:li[^>]*>(.*?)</rdf:li>", RegexOptions.Compiled | RegexOptions.Singleline, RegexBudget);
    private static readonly Regex XmpCreator = new(@"<dc:creator>.{0,2000}?<rdf:li[^>]*>(.*?)</rdf:li>", RegexOptions.Compiled | RegexOptions.Singleline, RegexBudget);
    private static readonly Regex XmpCreate = new(@"<xmp:CreateDate>(.*?)</xmp:CreateDate>|xmp:CreateDate=""([^""]+)""", RegexOptions.Compiled, RegexBudget);

    private static void ReadPdf(string path, Dictionary<string, object> map)
    {
        // Metadata normally sits near the start or the end; read both ends of large files.
        string text;
        using (var fs = SharedFile.OpenRead(path))
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

        // Encrypted: the strings are scrambled, so anything read would be nonsense.
        if (Encrypt.IsMatch(text)) return;

        // The document's own properties live in the object the trailer names as /Info. Reading only
        // that object keeps bookmark titles (which use the same /Title key) out of <doc:title>.
        // The last trailer wins: later saves append a new one.
        Match? infoRef = null;
        foreach (Match m in InfoRef.Matches(text)) infoRef = m;
        if (infoRef != null && FindObject(text, infoRef.Groups[1].Value, infoRef.Groups[2].Value) is { } info)
        {
            foreach (var (key, raw) in DictionaryEntries(info))
            {
                if (raw.Length == 0 || (raw[0] != '(' && raw[0] != '<')) continue;
                var value = DecodePdfString(raw).Trim();
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
        }

        // Page count: the largest /Count of a /Type /Pages node, whichever order the keys are in.
        int pages = 0;
        foreach (Match m in PagesType.Matches(text))
        {
            if (EnclosingDictionary(text, m.Index) is not { } dict) continue;
            foreach (Match c in CountKey.Matches(dict))
                if (int.TryParse(c.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) pages = Math.Max(pages, n);
        }
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

    /// <summary>The dictionary of object "num gen obj", or null (for example when it sits in a compressed object stream).</summary>
    private static string? FindObject(string text, string num, string gen)
    {
        Match? last = null;
        try
        {
            foreach (Match m in Regex.Matches(text, $@"(?<![0-9]){num}\s+{gen}\s+obj\b", RegexOptions.None, RegexBudget)) last = m;
        }
        catch (RegexMatchTimeoutException) { return null; }
        if (last == null) return null;
        int open = text.IndexOf("<<", last.Index + last.Length, StringComparison.Ordinal);
        if (open < 0 || open - (last.Index + last.Length) > 64) return null;
        return DictionaryFrom(text, open);
    }

    /// <summary>The dictionary that contains position <paramref name="at"/>, found by looking back for its "&lt;&lt;".</summary>
    private static string? EnclosingDictionary(string text, int at)
    {
        int from = at;
        for (int tries = 0; tries < 8; tries++)
        {
            int open = text.LastIndexOf("<<", from, Math.Min(from + 1, 4096), StringComparison.Ordinal);
            if (open < 0) return null;
            var dict = DictionaryFrom(text, open);
            if (dict != null && open + dict.Length > at) return dict;
            from = open - 1;
            if (from < 0) return null;
        }
        return null;
    }

    /// <summary>Reads a "&lt;&lt; … &gt;&gt;" dictionary starting at <paramref name="open"/>, nested dictionaries and strings included.</summary>
    private static string? DictionaryFrom(string text, int open)
    {
        int depth = 0;
        int limit = Math.Min(text.Length, open + 1_000_000);
        for (int i = open; i < limit; i++)
        {
            char c = text[i];
            if (c == '(') { i = SkipString(text, i) - 1; continue; }
            if (c == '<' && i + 1 < limit && text[i + 1] == '<') { depth++; i++; continue; }
            if (c == '>' && i + 1 < limit && text[i + 1] == '>')
            {
                depth--;
                i++;
                if (depth == 0) return text[open..(i + 1)];
            }
        }
        return null;
    }

    /// <summary>Index just past a literal string starting at "(", allowing balanced brackets and escapes inside.</summary>
    private static int SkipString(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\') { i++; continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i + 1;
        }
        return text.Length;
    }

    /// <summary>The top-level keys of a dictionary with their raw values ("(text)", "&lt;hex&gt;", "12 0 R"...).</summary>
    private static IEnumerable<(string Key, string Raw)> DictionaryEntries(string dict)
    {
        int i = 2, end = dict.Length - 2;
        while (i < end)
        {
            if (dict[i] != '/') { i++; continue; }
            int k = ++i;
            while (i < end && !char.IsWhiteSpace(dict[i]) && "/<>()[]".IndexOf(dict[i]) < 0) i++;
            var key = dict[k..i];
            while (i < end && char.IsWhiteSpace(dict[i])) i++;
            if (i >= end) yield break;
            int v = i;
            if (dict[i] == '(') i = SkipString(dict, i);
            else if (dict[i] == '<' && i + 1 < end && dict[i + 1] == '<')
            {
                var nested = DictionaryFrom(dict, i);
                i += nested?.Length ?? end - i;
            }
            else if (dict[i] == '<') { int close = dict.IndexOf('>', i); i = close < 0 ? end : close + 1; }
            else if (dict[i] == '[') { int close = dict.IndexOf(']', i); i = close < 0 ? end : close + 1; }
            else if (dict[i] == '/') { i++; while (i < end && !char.IsWhiteSpace(dict[i]) && "/<>()[]".IndexOf(dict[i]) < 0) i++; }
            else while (i < end && dict[i] != '/' && dict[i] != '>') i++;
            yield return (key, dict[v..Math.Min(i, end)].Trim());
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
        // D:YYYYMMDDHHmmSSOHH'mm' where O is +, - or Z. With a time zone the date is converted to
        // local time, like Office dates; without one it is taken as written.
        date = default;
        var m = Regex.Match(s, @"(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?(?:([+\-Z])(?:(\d{2})'?(\d{2})?'?)?)?");
        if (!m.Success) return false;
        int G(int i, int d) => m.Groups[i].Success && m.Groups[i].Value.Length > 0 ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : d;
        try
        {
            var local = new DateTime(G(1, 1), Math.Clamp(G(2, 1), 1, 12), 1, G(4, 0) % 24, G(5, 0) % 60, G(6, 0) % 60);
            local = local.AddDays(Math.Clamp(G(3, 1), 1, DateTime.DaysInMonth(local.Year, local.Month)) - 1);
            if (!m.Groups[7].Success) { date = local; return true; }
            var offset = m.Groups[7].Value == "Z" ? TimeSpan.Zero : new TimeSpan(Math.Min(G(8, 0), 14), Math.Min(G(9, 0), 59), 0);
            if (m.Groups[7].Value == "-") offset = -offset;
            date = new DateTimeOffset(local, offset).LocalDateTime;
            return true;
        }
        catch (ArgumentException) { return false; }
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

/// <summary>Opens files for reading without getting in the way of a rename, move or delete.</summary>
internal static class SharedFile
{
    public static FileStream OpenRead(string path, int bufferSize = 4096) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize);
}
