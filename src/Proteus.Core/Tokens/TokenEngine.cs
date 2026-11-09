using System.Globalization;
using System.Text;
using Proteus.Core.Rules;

namespace Proteus.Core.Tokens;

/// <summary>
/// Expands tokens like &lt;name&gt;, &lt;n:3&gt;, &lt;modified:yyyy-MM-dd&gt;, &lt;exif:taken:yyyyMMdd&gt;.
/// Unknown tokens are left untouched. Write \&lt; for a literal angle bracket.
/// </summary>
public static class TokenEngine
{
    public sealed record TokenInfo(string Syntax, string Description, string Group);

    public static readonly IReadOnlyList<TokenInfo> Catalog =
    [
        // File
        new("<name>", "Original name without extension", "File"),
        new("<ext>", "Original extension without the dot", "File"),
        new("<fullname>", "Original name with extension", "File"),
        new("<parent>", "Name of the containing folder", "File"),
        new("<parent:2>", "Name of the folder two levels up", "File"),
        new("<drive>", "Drive letter, e.g. D", "File"),
        new("<size>", "File size, human readable (e.g. 2.4 MB)", "File"),
        new("<size:kb>", "File size in KB (also :b, :mb, :gb)", "File"),
        new("<type>", "Windows file type, e.g. PDF Document", "File"),
        new("<owner>", "File owner account", "File"),
        new("<firstline>", "First line of a text file (.txt, .md, .csv…)", "File"),
        new("<hash:md5:8>", "Content hash, first 8 characters (md5, sha1, sha256, crc32)", "File"),
        // Counters
        new("<n>", "Position in the list, starting at 1", "Counter"),
        new("<n:3>", "Position, zero-padded to 3 digits", "Counter"),
        new("<fn:2>", "Position within its folder, padded", "Counter"),
        new("<count>", "Number of files being renamed", "Counter"),
        // Dates
        new("<modified:yyyy-MM-dd>", "Last modified date (any .NET date format)", "Dates"),
        new("<created:yyyy-MM-dd>", "Creation date", "Dates"),
        new("<accessed:yyyy-MM-dd>", "Last opened date", "Dates"),
        new("<now:yyyy-MM-dd>", "Today's date", "Dates"),
        new("<now:HHmmss>", "Current time", "Dates"),
        // Photo
        new("<exif:taken:yyyy-MM-dd_HHmmss>", "Photo date taken (falls back to modified)", "Photo"),
        new("<exif:camera>", "Camera model", "Photo"),
        new("<exif:make>", "Camera maker", "Photo"),
        new("<exif:lens>", "Lens model", "Photo"),
        new("<exif:iso>", "ISO speed", "Photo"),
        new("<exif:fnumber>", "Aperture, e.g. f/2.8", "Photo"),
        new("<exif:exposure>", "Shutter speed", "Photo"),
        new("<exif:focal>", "Focal length", "Photo"),
        new("<exif:width>", "Image width in pixels", "Photo"),
        new("<exif:height>", "Image height in pixels", "Photo"),
        // Video
        new("<video:duration>", "Length, e.g. 01-02-33 (or your own pattern, e.g. <video:duration:h\\hmm\\m>)", "Video"),
        new("<video:resolution>", "Frame size, e.g. 1920x1080", "Video"),
        new("<video:quality>", "Common name: 4K, 1080p, 720p, SD", "Video"),
        new("<video:width>", "Frame width in pixels", "Video"),
        new("<video:height>", "Frame height in pixels", "Video"),
        new("<video:fps>", "Frame rate, e.g. 29.97", "Video"),
        new("<video:bitrate>", "Total bitrate in kbps", "Video"),
        new("<video:date:yyyy-MM-dd>", "Date recorded (falls back to created)", "Video"),
        new("<video:title>", "Embedded title", "Video"),
        new("<video:year>", "Year", "Video"),
        // Music & audio
        new("<music:artist>", "Artist (MP3, FLAC, M4A, WMA, OGG…)", "Music & audio"),
        new("<music:albumartist>", "Album artist", "Music & audio"),
        new("<music:album>", "Album", "Music & audio"),
        new("<music:title>", "Song title", "Music & audio"),
        new("<music:track:2>", "Track number, padded", "Music & audio"),
        new("<music:disc>", "Disc number", "Music & audio"),
        new("<music:year>", "Year", "Music & audio"),
        new("<music:genre>", "Genre", "Music & audio"),
        new("<music:composer>", "Composer", "Music & audio"),
        new("<audio:duration>", "Length, e.g. 03-45", "Music & audio"),
        new("<audio:bitrate>", "Bitrate in kbps", "Music & audio"),
        new("<audio:samplerate>", "Sample rate in Hz", "Music & audio"),
        new("<audio:channels>", "Number of channels", "Music & audio"),
        // Documents
        new("<doc:title>", "Title (Word, Excel, PowerPoint, PDF, OpenDocument…)", "Documents"),
        new("<doc:author>", "Author", "Documents"),
        new("<doc:lastauthor>", "Last saved by", "Documents"),
        new("<doc:subject>", "Subject", "Documents"),
        new("<doc:company>", "Company", "Documents"),
        new("<doc:keywords>", "Keywords / tags", "Documents"),
        new("<doc:category>", "Category", "Documents"),
        new("<doc:pages>", "Page count", "Documents"),
        new("<doc:words>", "Word count", "Documents"),
        new("<doc:slides>", "Slide count", "Documents"),
        new("<doc:revision>", "Revision number", "Documents"),
        new("<doc:app>", "Program that created it", "Documents"),
        new("<doc:created:yyyy-MM-dd>", "Date the document was created", "Documents"),
        new("<doc:modified:yyyy-MM-dd>", "Date the document was last saved", "Documents"),
        // Anything else
        new("<prop:System.Comment>", "Any Windows property by name (as in Explorer's Details tab)", "Any Windows property"),
        new("<prop:System.Photo.DateTaken:yyyy-MM-dd>", "Date properties take a format", "Any Windows property"),
    ];

    public static bool ContainsToken(string? text) => text != null && text.Contains('<');

    public static string Expand(string? template, RuleContext ctx)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        if (!template.Contains('<')) return template.Replace("\\>", ">");

        var sb = new StringBuilder(template.Length + 16);
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c == '\\' && i + 1 < template.Length && (template[i + 1] == '<' || template[i + 1] == '>'))
            {
                sb.Append(template[i + 1]);
                i += 2;
                continue;
            }
            if (c == '<')
            {
                int end = template.IndexOf('>', i + 1);
                if (end > i + 1)
                {
                    var body = template.Substring(i + 1, end - i - 1);
                    string? value;
                    try { value = Resolve(body, ctx); }
                    catch (Exception) { value = string.Empty; } // bad metadata never breaks a name
                    if (value != null)
                    {
                        sb.Append(value);
                        i = end + 1;
                        continue;
                    }
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static string? Resolve(string body, RuleContext ctx)
    {
        var parts = body.Split(':', 2);
        var key = parts[0].Trim().ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : null;
        var f = ctx.File;

        switch (key)
        {
            case "name": return f.BaseName;
            case "ext": return f.Extension.TrimStart('.');
            case "fullname": return f.FileName;
            case "parent": return Parent(f.FullPath, ParseInt(arg, 1));
            case "n": return Pad(ctx.Index + 1, ParseInt(arg, 0));
            case "fn": return Pad(ctx.FolderIndex + 1, ParseInt(arg, 0));
            case "count": return ctx.Count.ToString(CultureInfo.InvariantCulture);
            case "modified": return FormatDate(f.Modified, arg);
            case "created": return FormatDate(f.Created, arg);
            case "now": return FormatDate(DateTime.Now, arg);
            case "accessed": return SafeAccessed(f.FullPath) is { } acc ? FormatDate(acc, arg) : string.Empty;
            case "size": return FormatSize(f.Size, arg);
            case "drive": return Path.GetPathRoot(f.FullPath)?.TrimEnd('\\', ':') ?? string.Empty;
            case "type": return Sanitize(Text(f.Metadata.Shell("System.ItemTypeText")));
            case "owner": return Sanitize(StripDomain(Text(f.Metadata.Shell("System.FileOwner"))));
            case "firstline": return Sanitize(f.Metadata.FirstLine ?? string.Empty);
            case "hash": return ResolveHash(arg, ctx);
            case "crc32": return ResolveHash("crc32" + (arg != null ? ":" + arg : ""), ctx);
            case "exif": return ResolveExif(arg, ctx);
            case "id3": return ResolveId3(arg, ctx);
            case "music": return ResolveMusic(arg, ctx);
            case "video": return ResolveVideo(arg, ctx);
            case "audio": return ResolveAudio(arg, ctx);
            case "doc": return ResolveDoc(arg, ctx);
            case "prop": return ResolveProp(arg, ctx);
            default: return null;
        }
    }

    private static string ResolveExif(string? arg, RuleContext ctx)
    {
        if (string.IsNullOrEmpty(arg)) return string.Empty;
        var p = arg.Split(':', 2);
        var field = p[0].Trim().ToLowerInvariant();
        if (field is "taken" or "date")
            return FormatDate(ctx.File.Metadata.DateTaken ?? ctx.File.Modified, p.Length > 1 ? p[1] : null);
        return Sanitize(ctx.File.Metadata.Exif(field) ?? string.Empty);
    }

    private static string ResolveId3(string? arg, RuleContext ctx)
    {
        if (string.IsNullOrEmpty(arg)) return string.Empty;
        var p = arg.Split(':', 2);
        var value = ctx.File.Metadata.Id3(p[0].Trim()) ?? string.Empty;
        if (p.Length > 1 && int.TryParse(value, out var num)) return Pad(num, ParseInt(p[1], 0));
        return Sanitize(value);
    }

    private static (string Field, string? Arg) Split(string? arg)
    {
        if (string.IsNullOrEmpty(arg)) return (string.Empty, null);
        var p = arg.Split(':', 2);
        return (p[0].Trim().ToLowerInvariant(), p.Length > 1 ? p[1] : null);
    }

    private static string ResolveHash(string? arg, RuleContext ctx)
    {
        var (algo, len) = Split(arg);
        if (algo.Length == 0) algo = "md5";
        var hex = ctx.File.Metadata.Hash(algo) ?? string.Empty;
        int n = ParseInt(len, 0);
        return n > 0 && n < hex.Length ? hex[..n] : hex;
    }

    private static string ResolveMusic(string? arg, RuleContext ctx)
    {
        var (field, extra) = Split(arg);
        if (field.Length == 0) return string.Empty;
        var text = Text(ctx.File.Metadata.Music(field));
        if (extra != null && long.TryParse(text, out var num)) return Pad(num, ParseInt(extra, 0));
        return Sanitize(text);
    }

    private static string ResolveVideo(string? arg, RuleContext ctx)
    {
        var (field, extra) = Split(arg);
        var m = ctx.File.Metadata;
        switch (field)
        {
            case "duration": case "length": return FormatDuration(m.Duration, extra);
            case "width": return m.Dimensions is { } d1 ? d1.Width.ToString(CultureInfo.InvariantCulture) : string.Empty;
            case "height": return m.Dimensions is { } d2 ? d2.Height.ToString(CultureInfo.InvariantCulture) : string.Empty;
            case "resolution": return m.Dimensions is { } d3 ? $"{d3.Width}x{d3.Height}" : string.Empty;
            case "quality": return m.Dimensions is { } d4 ? Quality(d4.Width, d4.Height) : string.Empty;
            case "fps":
            {
                // Windows reports frames per 1000 seconds.
                var v = m.Shell("System.Video.FrameRate");
                if (v == null) return string.Empty;
                double fps = Convert.ToDouble(v, CultureInfo.InvariantCulture) / 1000.0;
                return fps <= 0 ? string.Empty : Math.Round(fps, 2).ToString("0.##", CultureInfo.InvariantCulture);
            }
            case "bitrate":
            {
                var v = m.Shell("System.Video.TotalBitrate") ?? m.Shell("System.Video.EncodingBitrate");
                return v == null ? string.Empty : (Convert.ToInt64(v, CultureInfo.InvariantCulture) / 1000).ToString(CultureInfo.InvariantCulture);
            }
            case "date":
                return FormatDate(m.Shell("System.Media.DateEncoded") as DateTime? ?? ctx.File.Created, extra);
            case "title": return Sanitize(Text(m.Shell("System.Title")));
            case "year": return Text(m.Shell("System.Media.Year")) is { Length: > 0 } y && y != "0" ? y : string.Empty;
            default: return string.Empty;
        }
    }

    private static string ResolveAudio(string? arg, RuleContext ctx)
    {
        var (field, extra) = Split(arg);
        var m = ctx.File.Metadata;
        switch (field)
        {
            case "duration": case "length": return FormatDuration(m.Duration, extra);
            case "bitrate":
            {
                var v = m.Shell("System.Audio.EncodingBitrate");
                return v == null ? string.Empty : (Convert.ToInt64(v, CultureInfo.InvariantCulture) / 1000).ToString(CultureInfo.InvariantCulture);
            }
            case "samplerate": return Text(m.Shell("System.Audio.SampleRate"));
            case "channels": return Text(m.Shell("System.Audio.ChannelCount"));
            default: return ResolveMusic(arg, ctx);
        }
    }

    private static string ResolveDoc(string? arg, RuleContext ctx)
    {
        var (field, extra) = Split(arg);
        if (field.Length == 0) return string.Empty;
        var value = ctx.File.Metadata.Document(field);
        if (value is DateTime dt) return FormatDate(dt, extra);
        var text = Text(value);
        if (extra != null && long.TryParse(text, out var num)) return Pad(num, ParseInt(extra, 0));
        return Sanitize(text);
    }

    private static string ResolveProp(string? arg, RuleContext ctx)
    {
        if (string.IsNullOrWhiteSpace(arg)) return string.Empty;
        var p = arg.Split(':', 2);
        var value = ctx.File.Metadata.Shell(p[0].Trim());
        if (value is DateTime dt) return FormatDate(dt, p.Length > 1 ? p[1] : null);
        var text = Text(value);
        if (p.Length > 1 && long.TryParse(text, out var num)) return Pad(num, ParseInt(p[1], 0));
        return Sanitize(text);
    }

    /// <summary>Turns a metadata value into text; lists become "a, b".</summary>
    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        string[] arr => string.Join(", ", arr),
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        bool b => b ? "yes" : "no",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string StripDomain(string owner)
    {
        int i = owner.LastIndexOf('\\');
        return i >= 0 ? owner[(i + 1)..] : owner;
    }

    private static DateTime? SafeAccessed(string path)
    {
        try { return File.GetLastAccessTime(path); }
        catch (Exception) { return null; }
    }

    private static string Quality(int w, int h)
    {
        int s = Math.Min(w, h), l = Math.Max(w, h);
        if (l >= 7600 || s >= 4300) return "8K";
        if (l >= 3800 || s >= 2100) return "4K";
        if (l >= 2500 || s >= 1400) return "1440p";
        if (l >= 1900 || s >= 1060) return "1080p";
        if (l >= 1270 || s >= 700) return "720p";
        if (s >= 560) return "576p";
        if (s >= 470) return "480p";
        return "SD";
    }

    /// <summary>
    /// Formats a length. Default is hh-mm-ss (or mm-ss under an hour). A custom pattern may use
    /// h, hh, m, mm, s, ss and any other characters; put \ before a letter to keep it literally.
    /// </summary>
    public static string FormatDuration(TimeSpan? duration, string? pattern)
    {
        if (duration is not { } d) return string.Empty;
        if (string.IsNullOrWhiteSpace(pattern)) pattern = d.TotalHours >= 1 ? "hh-mm-ss" : "mm-ss";
        var sb = new StringBuilder();
        int totalHours = (int)d.TotalHours;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length) { sb.Append(pattern[++i]); continue; }
            bool dbl = i + 1 < pattern.Length && pattern[i + 1] == c;
            switch (c)
            {
                case 'h': sb.Append(totalHours.ToString(dbl ? "00" : "0", CultureInfo.InvariantCulture)); break;
                case 'm': sb.Append(d.Minutes.ToString(dbl ? "00" : "0", CultureInfo.InvariantCulture)); break;
                case 's': sb.Append(d.Seconds.ToString(dbl ? "00" : "0", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); continue;
            }
            if (dbl) i++;
        }
        return sb.ToString();
    }

    private static string FormatSize(long bytes, string? unit) => unit?.Trim().ToLowerInvariant() switch
    {
        "b" or "bytes" => bytes.ToString(CultureInfo.InvariantCulture),
        "kb" => Math.Round(bytes / 1024.0).ToString(CultureInfo.InvariantCulture),
        "mb" => (bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture),
        "gb" => (bytes / 1073741824.0).ToString("0.##", CultureInfo.InvariantCulture),
        _ => HumanSize(bytes),
    };

    /// <summary>Metadata can contain characters Windows forbids in names; swap them for safe ones.</summary>
    private static string Sanitize(string s)
    {
        if (s.Length == 0) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(ch switch
            {
                '/' or '\\' or '|' => '-',
                ':' => '-',
                '*' or '?' or '"' or '<' or '>' => '_',
                _ when char.IsControl(ch) => ' ',
                _ => ch,
            });
        }
        return sb.ToString().Trim();
    }

    private static string Parent(string fullPath, int levels)
    {
        var dir = Path.GetDirectoryName(fullPath);
        for (int i = 1; i < Math.Max(1, levels) && dir != null; i++) dir = Path.GetDirectoryName(dir);
        if (dir == null) return string.Empty;
        var name = Path.GetFileName(dir);
        return string.IsNullOrEmpty(name) ? dir.TrimEnd('\\', ':') : name;
    }

    private static string FormatDate(DateTime d, string? fmt)
    {
        try
        {
            // Culture formats like "d" or "t" can contain / and : ; make them safe for file names.
            return d.ToString(string.IsNullOrWhiteSpace(fmt) ? "yyyy-MM-dd" : fmt, CultureInfo.CurrentCulture)
                .Replace('/', '-').Replace('\\', '-').Replace(':', '.');
        }
        catch (FormatException)
        {
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    public static string Pad(long value, int digits) =>
        digits > 0 ? value.ToString("D" + Math.Min(digits, 18), CultureInfo.InvariantCulture)
                   : value.ToString(CultureInfo.InvariantCulture);

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : v.ToString(v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[u];
    }
}
