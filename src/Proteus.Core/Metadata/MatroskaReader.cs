namespace Proteus.Core.Metadata;

/// <summary>
/// Minimal Matroska / WebM header reader: frame size and duration, for when Windows has no
/// property handler for .mkv / .webm files.
/// </summary>
public static class MatroskaReader
{
    private const uint Segment = 0x18538067, Info = 0x1549A966, Tracks = 0x1654AE6B, TrackEntry = 0xAE,
        Video = 0xE0, PixelWidth = 0xB0, PixelHeight = 0xBA, TimecodeScale = 0x2AD7B1, DurationId = 0x4489,
        Cluster = 0x1F43B675, Ebml = 0x1A45DFA3;

    public sealed record Result(int Width, int Height, TimeSpan? Duration);

    public static Result? TryRead(string path)
    {
        try
        {
            using var fs = SharedFile.OpenRead(path);
            int width = 0, height = 0;
            double duration = 0;
            long scale = 1_000_000;

            // Header element, then the segment.
            if (!ReadId(fs, out var id) || id != Ebml || !ReadSize(fs, out var size)) return null;
            fs.Seek(size, SeekOrigin.Current);
            if (!ReadId(fs, out id) || id != Segment || !ReadSize(fs, out size)) return null;
            long segmentEnd = size < 0 ? fs.Length : Math.Min(fs.Length, fs.Position + size);

            // Walk the top level of the segment until clusters (media data) begin.
            while (fs.Position < segmentEnd && fs.Position < 64L * 1024 * 1024)
            {
                if (!ReadId(fs, out id) || !ReadSize(fs, out size)) break;
                if (id == Cluster) break;
                long end = size < 0 ? segmentEnd : fs.Position + size;
                if (id == Info)
                {
                    while (fs.Position < end && ReadId(fs, out var cid) && ReadSize(fs, out var csize))
                    {
                        if (cid == TimecodeScale) scale = (long)ReadUInt(fs, csize);
                        else if (cid == DurationId) duration = ReadFloat(fs, csize);
                        else fs.Seek(csize, SeekOrigin.Current);
                    }
                }
                else if (id == Tracks)
                {
                    WalkTracks(fs, end, ref width, ref height);
                }
                fs.Seek(end, SeekOrigin.Begin);
                if (width > 0 && duration > 0) break;
            }
            TimeSpan? d = duration > 0 ? TimeSpan.FromTicks((long)(duration * scale / 100.0)) : null;
            return width > 0 || d != null ? new Result(width, height, d) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WalkTracks(Stream fs, long end, ref int width, ref int height)
    {
        while (fs.Position < end && ReadId(fs, out var id) && ReadSize(fs, out var size))
        {
            long elementEnd = fs.Position + size;
            if (id == TrackEntry || id == Video)
            {
                WalkTracks(fs, elementEnd, ref width, ref height);
                if (width > 0 && height > 0) return;
            }
            else if (id == PixelWidth) width = (int)ReadUInt(fs, size);
            else if (id == PixelHeight) height = (int)ReadUInt(fs, size);
            fs.Seek(elementEnd, SeekOrigin.Begin);
        }
    }

    private static bool ReadId(Stream s, out uint id)
    {
        id = 0;
        int first = s.ReadByte();
        if (first <= 0) return false;
        int len = first >= 0x80 ? 1 : first >= 0x40 ? 2 : first >= 0x20 ? 3 : first >= 0x10 ? 4 : 0;
        if (len == 0) return false;
        id = (uint)first;
        for (int i = 1; i < len; i++)
        {
            int b = s.ReadByte();
            if (b < 0) return false;
            id = id << 8 | (uint)b;
        }
        return true;
    }

    /// <summary>Reads an element size; -1 means "unknown size".</summary>
    private static bool ReadSize(Stream s, out long size)
    {
        size = 0;
        int first = s.ReadByte();
        if (first <= 0) return false;
        int len = 1;
        int mask = 0x80;
        while (len <= 8 && (first & mask) == 0) { len++; mask >>= 1; }
        if (len > 8) return false;
        long value = first & (mask - 1);
        bool allOnes = value == mask - 1;
        for (int i = 1; i < len; i++)
        {
            int b = s.ReadByte();
            if (b < 0) return false;
            value = value << 8 | (uint)b;
            allOnes &= b == 0xFF;
        }
        size = allOnes ? -1 : value;
        return true;
    }

    private static ulong ReadUInt(Stream s, long size)
    {
        ulong v = 0;
        for (int i = 0; i < size && i < 8; i++) v = v << 8 | (uint)s.ReadByte();
        return v;
    }

    private static double ReadFloat(Stream s, long size)
    {
        // A float is 4 or 8 bytes; never allocate a buffer whose size comes from a damaged file.
        if (size is not (4 or 8))
        {
            s.Seek(size, SeekOrigin.Current);
            return 0;
        }
        var b = new byte[size];
        s.ReadExactly(b);
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        return size == 4 ? BitConverter.ToSingle(b) : size == 8 ? BitConverter.ToDouble(b) : 0;
    }
}
