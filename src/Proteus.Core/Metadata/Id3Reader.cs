using System.Text;

namespace Proteus.Core.Metadata;

public sealed class Id3Tags
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    internal void Set(string key, string? value, bool overwrite = false)
    {
        value = value?.Trim('\0', ' ');
        if (!string.IsNullOrEmpty(value) && (overwrite || !_values.ContainsKey(key))) _values[key] = value;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public bool IsEmpty => _values.Count == 0;
}

/// <summary>Minimal reader for ID3v2.2/2.3/2.4 text frames, with ID3v1 fallback.</summary>
public static class Id3Reader
{
    private static readonly Dictionary<string, string> FrameMap = new()
    {
        ["TIT2"] = "title", ["TT2"] = "title",
        ["TPE1"] = "artist", ["TP1"] = "artist",
        ["TPE2"] = "albumartist", ["TP2"] = "albumartist",
        ["TALB"] = "album", ["TAL"] = "album",
        ["TRCK"] = "track", ["TRK"] = "track",
        ["TPOS"] = "disc", ["TPA"] = "disc",
        ["TYER"] = "year", ["TYE"] = "year", ["TDRC"] = "year",
        ["TCON"] = "genre", ["TCO"] = "genre",
    };

    public static Id3Tags? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var tags = new Id3Tags();
            ReadV2(fs, tags);
            ReadV1(fs, tags);
            Normalize(tags);
            return tags.IsEmpty ? null : tags;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Normalize(Id3Tags tags)
    {
        // "3/12" -> "3"; "2021-05-01" -> "2021"
        var track = tags.Get("track");
        if (track != null && track.Contains('/')) Replace(tags, "track", track[..track.IndexOf('/')]);
        var disc = tags.Get("disc");
        if (disc != null && disc.Contains('/')) Replace(tags, "disc", disc[..disc.IndexOf('/')]);
        var year = tags.Get("year");
        if (year is { Length: > 4 }) Replace(tags, "year", year[..4]);
    }

    private static void Replace(Id3Tags tags, string key, string value) => tags.Set(key, value, overwrite: true);

    private static void ReadV2(FileStream fs, Id3Tags tags)
    {
        var header = new byte[10];
        if (fs.Read(header, 0, 10) != 10) return;
        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return;
        int major = header[3];
        int flags = header[5];
        int size = SyncSafe(header, 6);
        if (size <= 0 || size > fs.Length) return;

        // Text frames come first; big pictures follow. Read at most 2 MB of the tag.
        size = Math.Min(size, 2 * 1024 * 1024);
        var data = new byte[size];
        if (fs.Read(data, 0, size) != size) return;
        if ((flags & 0x80) != 0 && major < 4) data = Unsync(data);

        int pos = 0;
        if ((flags & 0x40) != 0 && major >= 3)
        {
            // Extended header: skip it.
            int ext = major == 4 ? SyncSafe(data, 0) : BigEndian(data, 0, 4) + 4;
            pos += ext;
        }

        bool v22 = major == 2;
        int idLen = v22 ? 3 : 4;
        int headLen = v22 ? 6 : 10;
        while (pos + headLen <= data.Length)
        {
            if (data[pos] == 0) break;
            var id = Encoding.ASCII.GetString(data, pos, idLen);
            int frameSize = v22 ? BigEndian(data, pos + 3, 3)
                : major == 4 ? SyncSafe(data, pos + 4) : BigEndian(data, pos + 4, 4);
            pos += headLen;
            if (frameSize <= 0 || pos + frameSize > data.Length) break;
            if (FrameMap.TryGetValue(id, out var key))
                tags.Set(key, DecodeText(data, pos, frameSize));
            pos += frameSize;
        }
    }

    private static void ReadV1(FileStream fs, Id3Tags tags)
    {
        if (fs.Length < 128) return;
        fs.Seek(-128, SeekOrigin.End);
        var b = new byte[128];
        if (fs.Read(b, 0, 128) != 128) return;
        if (b[0] != 'T' || b[1] != 'A' || b[2] != 'G') return;
        var latin = Encoding.Latin1;
        tags.Set("title", latin.GetString(b, 3, 30));
        tags.Set("artist", latin.GetString(b, 33, 30));
        tags.Set("album", latin.GetString(b, 63, 30));
        tags.Set("year", latin.GetString(b, 93, 4));
        if (b[125] == 0 && b[126] != 0) tags.Set("track", b[126].ToString());
    }

    private static string DecodeText(byte[] data, int offset, int length)
    {
        if (length < 1) return string.Empty;
        byte enc = data[offset];
        int start = offset + 1, len = length - 1;
        string s = enc switch
        {
            0 => Encoding.Latin1.GetString(data, start, len),
            1 => DecodeUtf16Bom(data, start, len),
            2 => Encoding.BigEndianUnicode.GetString(data, start, len),
            3 => Encoding.UTF8.GetString(data, start, len),
            _ => Encoding.Latin1.GetString(data, start, len),
        };
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    private static string DecodeUtf16Bom(byte[] data, int start, int len)
    {
        if (len >= 2 && data[start] == 0xFE && data[start + 1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(data, start + 2, len - 2);
        if (len >= 2 && data[start] == 0xFF && data[start + 1] == 0xFE)
            return Encoding.Unicode.GetString(data, start + 2, len - 2);
        return Encoding.Unicode.GetString(data, start, len);
    }

    private static byte[] Unsync(byte[] data)
    {
        var list = new List<byte>(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            list.Add(data[i]);
            if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0x00) i++;
        }
        return list.ToArray();
    }

    private static int SyncSafe(byte[] b, int o) =>
        (b[o] & 0x7F) << 21 | (b[o + 1] & 0x7F) << 14 | (b[o + 2] & 0x7F) << 7 | (b[o + 3] & 0x7F);

    private static int BigEndian(byte[] b, int o, int n)
    {
        int v = 0;
        for (int i = 0; i < n; i++) v = v << 8 | b[o + i];
        return v;
    }
}
