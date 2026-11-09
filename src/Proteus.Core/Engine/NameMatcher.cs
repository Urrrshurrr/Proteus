using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Proteus.Core.Engine;

/// <summary>How a file was paired with a reference name.</summary>
public enum MatchKind
{
    /// <summary>No reference name was close enough.</summary>
    None,
    /// <summary>Both names carry the same season and episode (S01E03, 1x03...).</summary>
    Episode,
    /// <summary>Paired on how alike the names read.</summary>
    Similar,
}

/// <summary>A season / episode marker found in a name. Season is -1 when only an episode number was found.</summary>
public readonly record struct EpisodeKey(int Season, int Episode, int LastEpisode)
{
    public bool SameAs(EpisodeKey other) =>
        Episode == other.Episode && LastEpisode == other.LastEpisode &&
        (Season == other.Season || Season < 0 || other.Season < 0);

    public override string ToString()
    {
        var s = Season >= 0 ? $"S{Season:00}E{Episode:00}" : $"E{Episode:00}";
        return LastEpisode != Episode ? s + $"-E{LastEpisode:00}" : s;
    }
}

/// <summary>One pairing: the source at <see cref="SourceIndex"/> takes the reference at <see cref="ReferenceIndex"/> (-1 for none).</summary>
public sealed record NameMatch(int SourceIndex, int ReferenceIndex, double Score, MatchKind Kind);

/// <summary>
/// Pairs each file with the closest name from a reference set: first by season and episode,
/// then by how similar the names are once release noise (1080p, x265, BluRay...) is ignored.
/// Every reference name is used at most once.
/// </summary>
public static class NameMatcher
{
    /// <summary>Pairs scoring below this are left unmatched.</summary>
    public const double DefaultMinScore = 0.3;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // S01E03, S1E3, S01.E03, S01E03E04, S01E03-E04, S01E03-04
    internal static readonly Regex SxE = new(
        @"(?<![a-z0-9])s(?<s>\d{1,3})[ ._-]?e(?<e>\d{1,4})(?:(?:(?:[ ._]?-[ ._]?)?e|-)(?<e2>\d{1,4})(?![\dp]))?", Opts, Timeout);
    // 1x03, 01x03
    internal static readonly Regex NxN = new(@"(?<![\da-z])(?<s>\d{1,2})x(?<e>\d{2,3})(?!\d)", Opts, Timeout);
    // Season 1 Episode 3
    internal static readonly Regex SeasonWords = new(@"season[ ._-]*(?<s>\d{1,3})[^a-z0-9]{0,5}(?:episode|ep)[ ._-]*(?<e>\d{1,4})", Opts, Timeout);
    // Episode 3, Ep 03, E03 (no season)
    internal static readonly Regex EpOnly = new(@"(?<![a-z0-9])(?:episode|ep|e)[ ._-]?(?<e>\d{1,4})(?!\d)", Opts, Timeout);

    internal static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "480p", "576p", "720p", "1080p", "1080i", "2160p", "4320p", "4k", "8k", "uhd", "fhd", "hd", "sd",
        "hdr", "hdr10", "hdr10plus", "dv", "dovi", "sdr", "10bit", "8bit", "12bit",
        "x264", "x265", "h264", "h265", "hevc", "avc", "av1", "vp9", "xvid", "divx",
        "bluray", "blu", "ray", "bdrip", "brrip", "bdremux", "remux", "web", "webrip", "webdl", "dl", "hdtv", "pdtv",
        "dvdrip", "dvd", "dvdscr", "hdrip", "amzn", "nf", "hulu", "hmax", "dsnp", "atvp", "pcok", "pmtp", "max", "itunes",
        "proper", "repack", "internal", "extended", "uncut", "unrated", "remastered", "limited",
        "aac", "aac2", "ac3", "eac3", "ddp", "ddp5", "ddp2", "dd", "dd5", "dd2", "dts", "dtshd", "ma", "truehd", "atmos",
        "flac", "opus", "mp3", "lpcm", "pcm", "6ch", "2ch", "8ch",
        "multi", "dual", "audio", "subs", "sub", "subbed", "dubbed", "eng", "english",
        "mkv", "mp4", "avi", "m4v",
    };

    /// <summary>Finds a season / episode marker in a name, or null.</summary>
    public static EpisodeKey? FindEpisode(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        try
        {
            var m = SxE.Match(name);
            if (m.Success)
            {
                int s = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
                int e = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture);
                int last = e;
                if (m.Groups["e2"].Success && int.TryParse(m.Groups["e2"].Value, out var e2) && e2 > e && e2 - e < 10) last = e2;
                return new EpisodeKey(s, e, last);
            }
            m = SeasonWords.Match(name);
            if (m.Success)
            {
                int e = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture);
                return new EpisodeKey(int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture), e, e);
            }
            m = NxN.Match(name);
            if (m.Success)
            {
                int e = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture);
                return new EpisodeKey(int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture), e, e);
            }
            m = EpOnly.Match(name);
            if (m.Success)
            {
                int e = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture);
                return new EpisodeKey(-1, e, e);
            }
        }
        catch (RegexMatchTimeoutException) { }
        return null;
    }

    private static List<string> Tokens(string name)
    {
        var text = RemoveAccents(name.ToLowerInvariant());
        try
        {
            text = SxE.Replace(text, " ");
            text = SeasonWords.Replace(text, " ");
            text = NxN.Replace(text, " ");
        }
        catch (RegexMatchTimeoutException) { }
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\'' || c == '\u2019') continue; // "don't" and "dont" read the same
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        var tokens = new List<string>();
        foreach (var t in sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Noise.Contains(t)) continue;
            // "and" vs "&" and articles are common differences between sources.
            if (t is "and" or "the" or "a" or "an") continue;
            if (t.All(char.IsDigit))
            {
                var trimmed = t.TrimStart('0');
                tokens.Add(trimmed.Length == 0 ? "0" : trimmed);
            }
            else tokens.Add(t);
        }
        return tokens;
    }

    private static string RemoveAccents(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private sealed class Profile
    {
        public Profile(string name)
        {
            Episode = FindEpisode(name);
            var tokens = Tokens(name);
            Words = new HashSet<string>(tokens, StringComparer.Ordinal);
            var joined = string.Concat(tokens);
            Bigrams = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < joined.Length; i++) Bigrams.Add(joined.Substring(i, 2));
        }

        public EpisodeKey? Episode { get; }
        public HashSet<string> Words { get; }
        public HashSet<string> Bigrams { get; }
    }

    private static double Dice(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        int common = 0;
        foreach (var x in small) if (large.Contains(x)) common++;
        return 2.0 * common / (a.Count + b.Count);
    }

    private static double TextSimilarity(Profile a, Profile b) =>
        0.5 * Dice(a.Words, b.Words) + 0.5 * Dice(a.Bigrams, b.Bigrams);

    /// <summary>How alike two names are, from 0 to 1, ignoring episode markers and release noise.</summary>
    public static double Similarity(string a, string b) => TextSimilarity(new Profile(a), new Profile(b));

    private static (double Score, MatchKind Kind) Score(Profile a, Profile b)
    {
        double text = TextSimilarity(a, b);
        if (a.Episode is { } ea && b.Episode is { } eb)
        {
            if (!ea.SameAs(eb)) return (0, MatchKind.None);
            // Same episode: a strong signal on its own; the title decides between duplicates.
            double bonus = ea.Season == eb.Season ? 0.7 : 0.6;
            return (bonus + 0.3 * text, MatchKind.Episode);
        }
        return (0.9 * text, MatchKind.Similar);
    }

    /// <summary>
    /// Pairs every source name with at most one reference name (each reference is used once).
    /// Names should be given without their extensions. The result has one entry per source, in order.
    /// </summary>
    public static IReadOnlyList<NameMatch> Match(IReadOnlyList<string> sources, IReadOnlyList<string> references,
        double minScore = DefaultMinScore, CancellationToken ct = default)
    {
        var src = sources.Select(s => new Profile(s)).ToArray();
        var refs = references.Select(r => new Profile(r)).ToArray();

        var pairs = new List<(int S, int R, double Score, MatchKind Kind)>();
        for (int i = 0; i < src.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (int j = 0; j < refs.Length; j++)
            {
                var (score, kind) = Score(src[i], refs[j]);
                if (score >= minScore && kind != MatchKind.None) pairs.Add((i, j, score, kind));
            }
        }

        // Greedy, best pair first: clear-cut matches are fixed before ambiguous ones compete.
        pairs.Sort((x, y) =>
        {
            int c = y.Score.CompareTo(x.Score);
            if (c != 0) return c;
            c = x.S.CompareTo(y.S);
            return c != 0 ? c : x.R.CompareTo(y.R);
        });

        var result = new NameMatch[src.Length];
        var usedRef = new bool[refs.Length];
        foreach (var (s, r, score, kind) in pairs)
        {
            if (result[s] != null || usedRef[r]) continue;
            result[s] = new NameMatch(s, r, Math.Round(score, 3), kind);
            usedRef[r] = true;
        }
        for (int i = 0; i < result.Length; i++) result[i] ??= new NameMatch(i, -1, 0, MatchKind.None);
        return result;
    }
}
