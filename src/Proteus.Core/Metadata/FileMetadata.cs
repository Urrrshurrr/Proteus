using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Proteus.Core.Models;

namespace Proteus.Core.Metadata;

/// <summary>Lazily read metadata for one file. Every getter is safe to call on any file.</summary>
public sealed class FileMetadata
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".tif", ".tiff", ".png", ".heic", ".heif", ".webp", ".cr2", ".cr3", ".nef",
        ".arw", ".dng", ".orf", ".rw2", ".raf", ".gif", ".bmp"
    };

    private readonly FileEntry _entry;
    private Dictionary<string, string>? _exif;
    private DateTime? _taken;
    private bool _exifLoaded;
    private Id3Tags? _id3;
    private bool _id3Loaded;

    public FileMetadata(FileEntry entry) => _entry = entry;

    public DateTime? DateTaken
    {
        get { LoadExif(); return _taken; }
    }

    public string? Exif(string key)
    {
        LoadExif();
        return _exif != null && _exif.TryGetValue(key, out var v) ? v : null;
    }

    public string? Id3(string key)
    {
        lock (_gate)
        {
            if (!_id3Loaded)
            {
                if (!_entry.IsFolder && _entry.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                    _id3 = Id3Reader.TryRead(_entry.FullPath);
                _id3Loaded = true;
            }
            return _id3?.Get(key);
        }
    }

    // ---------- Windows property system ----------

    /// <summary>Properties fetched together the first time any common one is asked for.</summary>
    public static readonly string[] CommonShellProperties =
    [
        "System.ItemTypeText", "System.FileOwner", "System.Title", "System.Subject", "System.Author",
        "System.Keywords", "System.Category", "System.Comment", "System.Company", "System.ApplicationName",
        "System.Document.LastAuthor", "System.Document.PageCount", "System.Document.WordCount",
        "System.Document.DateCreated", "System.Document.DateSaved", "System.Document.RevisionNumber",
        "System.Presentation.SlideCount", "System.Document.Manager",
        "System.Media.Duration", "System.Media.Year", "System.Media.DateEncoded", "System.Media.SubTitle",
        "System.Video.FrameWidth", "System.Video.FrameHeight", "System.Video.FrameRate",
        "System.Video.EncodingBitrate", "System.Video.TotalBitrate", "System.Video.Compression",
        "System.Audio.EncodingBitrate", "System.Audio.SampleRate", "System.Audio.ChannelCount",
        "System.Music.Artist", "System.Music.AlbumArtist", "System.Music.AlbumTitle", "System.Music.TrackNumber",
        "System.Music.Genre", "System.Music.Composer", "System.Music.DiscNumber", "System.Music.Conductor",
        "System.Image.HorizontalSize", "System.Image.VerticalSize", "System.Photo.DateTaken",
        "System.Photo.CameraModel", "System.Photo.CameraManufacturer",
    ];

    private readonly object _gate = new();
    private Dictionary<string, object>? _shell;
    private Dictionary<string, object>? _doc;
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private string? _firstLine;
    private readonly HashSet<string> _shellMisses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Any Windows property by canonical name, e.g. "System.Media.Duration".</summary>
    public object? Shell(string canonicalName)
    {
        if (!ShellProperties.IsSupported) return null;
        lock (_gate)
        {
            if (_shell == null)
                _shell = ShellProperties.Read(_entry.FullPath, CommonShellProperties);
            if (_shell.TryGetValue(canonicalName, out var v)) return v;
            if (CommonShellProperties.Contains(canonicalName, StringComparer.OrdinalIgnoreCase)) return null;
            if (_shellMisses.Contains(canonicalName)) return null;
            var extra = ShellProperties.Read(_entry.FullPath, [canonicalName]);
            if (extra.TryGetValue(canonicalName, out v)) { _shell[canonicalName] = v; return v; }
            _shellMisses.Add(canonicalName);
            return null;
        }
    }

    /// <summary>Document property read from the file itself (Office, OpenDocument, PDF), then from Windows.</summary>
    public object? Document(string key)
    {
        lock (_gate)
        {
            _doc ??= !_entry.IsFolder && DocumentReader.Handles(_entry.Extension)
                ? DocumentReader.Read(_entry.FullPath)
                : new Dictionary<string, object>();
            if (_doc.TryGetValue(key, out var v)) return v;
        }
        return key.ToLowerInvariant() switch
        {
            "title" => Shell("System.Title"),
            "subject" => Shell("System.Subject"),
            "author" => Shell("System.Author"),
            "lastauthor" => Shell("System.Document.LastAuthor"),
            "keywords" => Shell("System.Keywords"),
            "category" => Shell("System.Category"),
            "description" or "comment" => Shell("System.Comment"),
            "company" => Shell("System.Company"),
            "manager" => Shell("System.Document.Manager"),
            "app" => Shell("System.ApplicationName"),
            "revision" => Shell("System.Document.RevisionNumber"),
            "pages" => Shell("System.Document.PageCount"),
            "words" => Shell("System.Document.WordCount"),
            "slides" => Shell("System.Presentation.SlideCount"),
            "created" => Shell("System.Document.DateCreated"),
            "modified" => Shell("System.Document.DateSaved"),
            _ => null,
        };
    }

    /// <summary>Music tag: Windows first (MP3, FLAC, M4A, WMA, OGG...), then the built-in ID3 reader.</summary>
    public object? Music(string key)
    {
        object? v = key.ToLowerInvariant() switch
        {
            "artist" => Shell("System.Music.Artist"),
            "albumartist" => Shell("System.Music.AlbumArtist"),
            "album" => Shell("System.Music.AlbumTitle"),
            "title" => Shell("System.Title"),
            "track" => Shell("System.Music.TrackNumber"),
            "disc" => Shell("System.Music.DiscNumber"),
            "year" => Shell("System.Media.Year"),
            "genre" => Shell("System.Music.Genre"),
            "composer" => Shell("System.Music.Composer"),
            "conductor" => Shell("System.Music.Conductor"),
            _ => null,
        };
        if (v is long or ulong or int && Convert.ToInt64(v, CultureInfo.InvariantCulture) == 0) v = null;
        return v ?? Id3(key);
    }

    /// <summary>Length of a video or audio file, from Windows or from the file's own headers.</summary>
    public TimeSpan? Duration
    {
        get
        {
            if (Shell("System.Media.Duration") is ulong ticks && ticks > 0) return TimeSpan.FromTicks((long)ticks);
            if (Matroska?.Duration is { } md) return md;
            var desc = FindMetadataExtractorTag("Duration");
            if (desc != null && TimeSpan.TryParse(desc, CultureInfo.InvariantCulture, out var ts) && ts > TimeSpan.Zero) return ts;
            return null;
        }
    }

    /// <summary>Pixel size of a video or image.</summary>
    public (int Width, int Height)? Dimensions
    {
        get
        {
            int w = ToInt(Shell("System.Video.FrameWidth")), h = ToInt(Shell("System.Video.FrameHeight"));
            if (w <= 0 || h <= 0) { w = ToInt(Shell("System.Image.HorizontalSize")); h = ToInt(Shell("System.Image.VerticalSize")); }
            if ((w <= 0 || h <= 0) && Matroska is { Width: > 0, Height: > 0 } mk) { w = mk.Width; h = mk.Height; }
            if ((w <= 0 || h <= 0) && int.TryParse(Exif("width"), out var ew) && int.TryParse(Exif("height"), out var eh)) { w = ew; h = eh; }
            if (w <= 0 || h <= 0)
            {
                int.TryParse(FirstDigits(FindMetadataExtractorTag("Width")), out w);
                int.TryParse(FirstDigits(FindMetadataExtractorTag("Height")), out h);
            }
            return w > 0 && h > 0 ? (w, h) : null;
        }
    }

    /// <summary>Hex digest of the file contents: md5, sha1, sha256 or crc32.</summary>
    public string? Hash(string algorithm)
    {
        if (_entry.IsFolder) return null;
        algorithm = algorithm.ToLowerInvariant();
        lock (_gate)
        {
            if (_hashes.TryGetValue(algorithm, out var cached)) return cached;
        }
        try
        {
            using var fs = SharedFile.OpenRead(_entry.FullPath, 1 << 16);
            var ct = MetadataCancellation.Current;
            string? hex = algorithm switch
            {
                "md5" => HashWith(System.Security.Cryptography.HashAlgorithmName.MD5, fs, ct),
                "sha1" => HashWith(System.Security.Cryptography.HashAlgorithmName.SHA1, fs, ct),
                "sha256" => HashWith(System.Security.Cryptography.HashAlgorithmName.SHA256, fs, ct),
                "crc32" => Crc32(fs, ct).ToString("X8", CultureInfo.InvariantCulture),
                _ => null,
            };
            if (hex == null) return null;
            hex = hex.ToLowerInvariant();
            lock (_gate) _hashes[algorithm] = hex;
            return hex;
        }
        catch (Exception) { return null; }
    }

    /// <summary>First non-empty line of a small text file (.txt, .md, .csv, ...).</summary>
    public string? FirstLine
    {
        get
        {
            lock (_gate) return ReadFirstLine();
        }
    }

    private string? ReadFirstLine()
    {
        {
            if (_firstLine != null) return _firstLine.Length == 0 ? null : _firstLine;
            string line = string.Empty;
            try
            {
                if (!_entry.IsFolder && _entry.Size < 50_000_000)
                {
                    using var reader = new StreamReader(SharedFile.OpenRead(_entry.FullPath), detectEncodingFromByteOrderMarks: true);
                    for (int i = 0; i < 50; i++)
                    {
                        var l = reader.ReadLine();
                        if (l == null) break;
                        l = l.Trim().TrimStart('#', ' ').Trim();
                        if (l.Length > 0) { line = l.Length > 120 ? l[..120] : l; break; }
                    }
                    if (line.Any(c => c == '\0')) line = string.Empty; // binary file
                }
            }
            catch (Exception) { }
            _firstLine = line;
            return line.Length == 0 ? null : line;
        }
    }

    private MatroskaReader.Result? _mkv;
    private bool _mkvLoaded;

    private MatroskaReader.Result? Matroska
    {
        get
        {
            lock (_gate)
            {
                if (_mkvLoaded) return _mkv;
                var ext = _entry.Extension.ToLowerInvariant();
                if (ext is ".mkv" or ".webm" or ".mka" or ".mk3d") _mkv = MatroskaReader.TryRead(_entry.FullPath);
                _mkvLoaded = true;
                return _mkv;
            }
        }
    }

    private List<MetadataExtractor.Directory>? _allDirs;

    private string? FindMetadataExtractorTag(string tagName)
    {
        if (_entry.IsFolder) return null;
        lock (_gate)
        {
            if (_allDirs == null)
            {
                try
                {
                    using var fs = SharedFile.OpenRead(_entry.FullPath);
                    _allDirs = ImageMetadataReader.ReadMetadata(fs).ToList();
                }
                catch (Exception) { _allDirs = []; }
            }
        }
        foreach (var d in _allDirs)
            foreach (var t in d.Tags)
                if (t.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(t.Description))
                    return t.Description;
        return null;
    }

    private static int ToInt(object? v) => v switch
    {
        null => 0,
        int i => i,
        long l => (int)l,
        ulong u => (int)u,
        double d => (int)d,
        string s when int.TryParse(s, out var x) => x,
        _ => 0,
    };

    private static string FirstDigits(string? s) =>
        s == null ? string.Empty : new string(s.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());

    /// <summary>Hashes a stream in chunks, so a newer preview can stop it part-way through a huge file.</summary>
    private static string HashWith(System.Security.Cryptography.HashAlgorithmName name, Stream s, CancellationToken ct)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(name);
        var buf = new byte[1 << 16];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buf, 0, n);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static uint Crc32(Stream s, CancellationToken ct)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        uint crc = 0xFFFFFFFFu;
        var buf = new byte[1 << 16];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < n; i++) crc = table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
        }
        return ~crc;
    }

    private void LoadExif()
    {
        lock (_gate)
        {
            if (_exifLoaded) return;
            LoadExifCore();
            _exifLoaded = true;
        }
    }

    private void LoadExifCore()
    {
        if (_entry.IsFolder || !ImageExtensions.Contains(_entry.Extension)) return;
        try
        {
            IReadOnlyList<MetadataExtractor.Directory> dirs;
            using (var fs = SharedFile.OpenRead(_entry.FullPath)) dirs = ImageMetadataReader.ReadMetadata(fs);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();

            if (sub != null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dto)) _taken = dto;
            else if (ifd0 != null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt)) _taken = dt;

            void Put(string key, MetadataExtractor.Directory? d, int tag)
            {
                var s = d?.GetDescription(tag) ?? d?.GetString(tag);
                if (!string.IsNullOrWhiteSpace(s)) map[key] = s.Trim();
            }

            Put("make", ifd0, ExifDirectoryBase.TagMake);
            Put("model", ifd0, ExifDirectoryBase.TagModel);
            Put("lens", sub, ExifDirectoryBase.TagLensModel);
            Put("iso", sub, ExifDirectoryBase.TagIsoEquivalent);
            Put("fnumber", sub, ExifDirectoryBase.TagFNumber);
            if (map.TryGetValue("fnumber", out var fn)) map["fnumber"] = fn.Replace("f/", "f", StringComparison.OrdinalIgnoreCase);
            Put("exposure", sub, ExifDirectoryBase.TagExposureTime);
            Put("focal", sub, ExifDirectoryBase.TagFocalLength);

            if (sub != null && sub.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var w)) map["width"] = w.ToString(CultureInfo.InvariantCulture);
            if (sub != null && sub.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var h)) map["height"] = h.ToString(CultureInfo.InvariantCulture);
            if (!map.ContainsKey("width"))
            {
                foreach (var d in dirs)
                {
                    var wt = d.Tags.FirstOrDefault(t => t.Name == "Image Width");
                    var ht = d.Tags.FirstOrDefault(t => t.Name == "Image Height");
                    if (wt != null && ht != null)
                    {
                        map["width"] = FirstNumber(wt.Description);
                        map["height"] = FirstNumber(ht.Description);
                        break;
                    }
                }
            }
            if (map.TryGetValue("model", out var model)) map["camera"] = model;
            _exif = map;
        }
        catch (Exception)
        {
            // Unreadable or unsupported file: no EXIF, never an error for the user.
        }
    }

    private static string FirstNumber(string? s)
    {
        if (s == null) return string.Empty;
        var digits = new string(s.TakeWhile(c => !char.IsDigit(c)).ToArray());
        return new string(s.Skip(digits.Length).TakeWhile(char.IsDigit).ToArray());
    }
}

/// <summary>
/// The cancellation of the preview being worked out on this thread, so slow readers (hashing a
/// large file) can stop as soon as a newer preview replaces it.
/// </summary>
public static class MetadataCancellation
{
    [ThreadStatic] private static CancellationToken _current;

    public static CancellationToken Current => _current;

    /// <summary>Sets the token for the current thread until the returned scope is disposed.</summary>
    public static Scope Use(CancellationToken ct)
    {
        var previous = _current;
        _current = ct;
        return new Scope(previous);
    }

    public readonly struct Scope(CancellationToken previous) : IDisposable
    {
        public void Dispose() => _current = previous;
    }
}
