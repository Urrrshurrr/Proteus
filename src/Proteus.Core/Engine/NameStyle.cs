using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Proteus.Core.Engine;

/// <summary>How words are capitalised in a naming style.</summary>
public enum WordCase
{
    /// <summary>Words are used exactly as they appear in each file's name.</summary>
    AsIs,
    /// <summary>Title Case (small words like "of" and "the" stay lowercase when the samples do that).</summary>
    Title,
    Lower,
    Upper,
}

/// <summary>A part of a name found by <see cref="NameStyle.Parse"/>, with where it sits in the name.</summary>
public sealed record NamePart(string Field, int Start, int Length, int Digits = 0);

/// <summary>What a file name says about itself: show or film, year, season and episode, episode title, quality.</summary>
public sealed class ParsedName
{
    public required string Original { get; init; }
    public List<string> ShowWords { get; } = [];
    public List<string> TitleWords { get; } = [];
    public int? Year { get; set; }
    public EpisodeKey? Episode { get; set; }
    public string? Quality { get; set; }
    /// <summary>The separator between words in the show name or title: ' ', '.', '_' or '-'.</summary>
    public char WordSeparator { get; set; } = ' ';
    public List<NamePart> Parts { get; } = [];

    public string Show => string.Join(' ', ShowWords);
    public string Title => string.Join(' ', TitleWords);
}

/// <summary>A naming style learned from sample names: a template plus word spacing and capitalisation.</summary>
public sealed record LearnedStyle(string Template, string WordSeparator, WordCase Case, bool SmallWordsLower, int Agreeing, int Samples);

/// <summary>The result of applying a style to one name.</summary>
public sealed record StyledName(string Name, IReadOnlyList<string> Missing)
{
    public bool IsComplete => Missing.Count == 0;
}

/// <summary>
/// "Match a naming style": learns a template such as <c>&lt;show&gt; - &lt;season:00&gt;x&lt;episode:00&gt; - &lt;title&gt;</c>
/// from sample names, then rebuilds other names in that style from what their own names contain.
/// </summary>
public static class NameStyle
{
    /// <summary>Fields a template can use.</summary>
    public static readonly IReadOnlyList<(string Token, string Meaning)> Fields =
    [
        ("<show>", "Show or film name"),
        ("<year>", "Year, e.g. 2014"),
        ("<season:00>", "Season number; the zeros set the padding"),
        ("<episode:00>", "Episode number; double episodes become 01-E02"),
        ("<title>", "Episode title"),
        ("<quality>", "Resolution, e.g. 1080p"),
    ];

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private static readonly Regex Word = new(@"[\p{L}\p{N}'’!?&+]+(?:-[\p{L}\p{N}'’!?&+]+)*", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex YearWord = new(@"^(?:19|20)\d{2}$", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex QualityWord = new(@"^(?:\d{3,4}[pi]|[48]k)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex TokenRx = new(@"<(show|year|season|episode|title|quality)(?::(0+))?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    // Release words that could also be real words in a title ("Max", "Extended"): only noise next to other noise.
    private static readonly HashSet<string> Ambiguous = new(StringComparer.Ordinal)
    {
        "max", "ma", "dd", "dv", "hd", "sd", "audio", "dual", "english", "eng", "limited", "extended", "uncut", "unrated",
        "remastered", "sub", "subs", "multi", "ray", "blu", "internal", "opus",
    };

    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "nor", "but", "of", "in", "on", "at", "to", "for", "by", "with", "from", "as", "vs",
    };

    private static bool IsNoise(string word, bool strict)
    {
        var w = word.ToLowerInvariant();
        if (QualityWord.IsMatch(w)) return true;
        if (NameMatcher.Noise.Contains(w)) return !strict || !Ambiguous.Contains(w);
        var parts = w.Split('-', StringSplitOptions.RemoveEmptyEntries);
        // "WEB-DL", "x265-GROUP", "DDP5-1": a hyphenated release tag starts with a release word.
        return parts.Length > 1 && (NameMatcher.Noise.Contains(parts[0]) || QualityWord.IsMatch(parts[0]));
    }

    private static Match? FindMarker(string name)
    {
        foreach (var rx in new[] { NameMatcher.SxE, NameMatcher.SeasonWords, NameMatcher.NxN, NameMatcher.EpOnly })
        {
            try
            {
                var m = rx.Match(name);
                if (m.Success) return m;
            }
            catch (RegexMatchTimeoutException) { }
        }
        return null;
    }

    private static List<(string Text, int Start)> Words(string name, int from, int to)
    {
        var list = new List<(string, int)>();
        if (to <= from) return list;
        try
        {
            foreach (Match w in Word.Matches(name.Substring(from, to - from)))
                list.Add((w.Value, from + w.Index));
        }
        catch (RegexMatchTimeoutException) { }
        return list;
    }

    /// <summary>Reads the show, year, season, episode, title and quality out of a name (without extension).</summary>
    public static ParsedName Parse(string name)
    {
        var p = new ParsedName { Original = name };
        var marker = FindMarker(name);
        int headEnd = marker?.Index ?? name.Length;
        var head = Words(name, 0, headEnd);

        // Quality can sit anywhere.
        foreach (var (text, start) in Words(name, 0, name.Length))
            if (QualityWord.IsMatch(text))
            {
                p.Quality = text.ToLowerInvariant();
                p.Parts.Add(new NamePart("quality", start, text.Length));
                break;
            }

        // Year: the last year-like word in the head that isn't the very first word ("1917 (2019)").
        int yearAt = -1;
        for (int i = head.Count - 1; i >= 1; i--)
            if (YearWord.IsMatch(head[i].Text)) { yearAt = i; break; }
        if (yearAt >= 0)
        {
            p.Year = int.Parse(head[yearAt].Text, CultureInfo.InvariantCulture);
            p.Parts.Add(new NamePart("year", head[yearAt].Start, 4));
        }

        // Show: the head words before the year (or before the episode marker / first release word).
        var show = new List<(string Text, int Start)>();
        int limit = yearAt >= 0 ? yearAt : head.Count;
        for (int i = 0; i < limit; i++)
        {
            if (yearAt < 0 && marker == null && i > 0 && IsNoise(head[i].Text, strict: true)) break;
            if (yearAt < 0 && marker != null && IsNoise(head[i].Text, strict: true)) break;
            show.Add(head[i]);
        }
        AddWords(p, p.ShowWords, show, "show", name);

        if (marker != null)
        {
            var key = NameMatcher.FindEpisode(marker.Value);
            p.Episode = key;
            var s = marker.Groups["s"];
            var e = marker.Groups["e"];
            var e2 = marker.Groups["e2"];
            if (s.Success) p.Parts.Add(new NamePart("season", s.Index, s.Length, s.Length));
            if (e.Success)
            {
                int end = key is { } k && k.LastEpisode != k.Episode && e2.Success ? e2.Index + e2.Length : e.Index + e.Length;
                p.Parts.Add(new NamePart("episode", e.Index, end - e.Index, e.Length));
            }

            // Title: the words after the marker, up to the first release word.
            var title = new List<(string Text, int Start)>();
            foreach (var w in Words(name, marker.Index + marker.Length, name.Length))
            {
                if (IsNoise(w.Text, strict: true)) break;
                title.Add(w);
            }
            AddWords(p, p.TitleWords, title, "title", name);
        }
        p.Parts.Sort((a, b) => a.Start.CompareTo(b.Start));
        return p;
    }

    private static void AddWords(ParsedName p, List<string> target, List<(string Text, int Start)> words, string field, string name)
    {
        if (words.Count == 0) return;
        foreach (var (text, _) in words) target.Add(text);
        int start = words[0].Start, end = words[^1].Start + words[^1].Text.Length;
        p.Parts.Add(new NamePart(field, start, end - start));
        if (words.Count > 1)
        {
            var gap = name.Substring(words[0].Start + words[0].Text.Length, words[1].Start - words[0].Start - words[0].Text.Length);
            if (gap.Length > 0 && field == "show") p.WordSeparator = gap.Contains(' ') ? ' ' : gap[0];
            else if (gap.Length > 0 && p.ShowWords.Count <= 1) p.WordSeparator = gap.Contains(' ') ? ' ' : gap[0];
        }
    }

    /// <summary>The template one sample name follows, e.g. "&lt;show&gt; (&lt;year&gt;) - S&lt;season:00&gt;E&lt;episode:00&gt;".</summary>
    public static string TemplateOf(ParsedName p)
    {
        var sb = new StringBuilder();
        int at = 0;
        foreach (var part in p.Parts)
        {
            if (part.Start < at) continue; // overlapping (quality inside a title is never used)
            sb.Append(p.Original, at, part.Start - at);
            sb.Append(part.Field switch
            {
                "season" or "episode" => $"<{part.Field}:{new string('0', Math.Max(1, part.Digits))}>",
                _ => $"<{part.Field}>",
            });
            at = part.Start + part.Length;
        }
        sb.Append(p.Original, at, p.Original.Length - at);
        return sb.ToString();
    }

    /// <summary>Learns a style from sample names (without extensions). Returns null when there are no samples.</summary>
    public static LearnedStyle? Learn(IEnumerable<string> samples)
    {
        var parsed = samples.Select(s => s.Trim()).Where(s => s.Length > 0).Select(Parse).ToList();
        if (parsed.Count == 0) return null;

        var votes = parsed.GroupBy(TemplateOf).Select(g => (Template: g.Key, Count: g.Count(), First: parsed.IndexOf(g.First())))
            .OrderByDescending(g => g.Count).ThenBy(g => g.First).ToList();
        var best = votes[0];

        var words = parsed.SelectMany(p => p.ShowWords.Concat(p.TitleWords))
            .SelectMany(w => w.Split('-', StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Any(char.IsLetter)).ToList();
        var midWords = parsed.SelectMany(p => p.ShowWords.Skip(1).Concat(p.TitleWords.Skip(1))).ToList();

        WordCase wordCase;
        if (words.Count == 0) wordCase = WordCase.AsIs;
        else if (words.All(w => w == w.ToLowerInvariant())) wordCase = WordCase.Lower;
        else if (words.All(w => w == w.ToUpperInvariant()) && words.Any(w => w.Length > 1)) wordCase = WordCase.Upper;
        else if (words.Where(w => !SmallWords.Contains(w)).All(w => char.IsUpper(w.First(char.IsLetter)))) wordCase = WordCase.Title;
        else wordCase = WordCase.AsIs;
        bool smallLower = midWords.Any(w => SmallWords.Contains(w) && w == w.ToLowerInvariant());

        var sep = parsed.Where(p => p.ShowWords.Count > 1 || p.TitleWords.Count > 1)
            .GroupBy(p => p.WordSeparator).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(' ');

        return new LearnedStyle(best.Template, sep.ToString(), wordCase, smallLower, best.Count, parsed.Count);
    }

    /// <summary>Which fields a template uses.</summary>
    public static HashSet<string> FieldsIn(string template)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (Match m in TokenRx.Matches(template)) set.Add(m.Groups[1].Value.ToLowerInvariant()); }
        catch (RegexMatchTimeoutException) { }
        return set;
    }

    /// <summary>
    /// Builds a name in the given style from what <paramref name="source"/> contains.
    /// Fields the name doesn't have are left out together with the separator in front of them, and listed in Missing.
    /// </summary>
    public static StyledName Apply(string template, ParsedName source, string wordSeparator = " ", WordCase wordCase = WordCase.AsIs,
        bool smallWordsLower = true, string? showOverride = null, string? yearOverride = null)
    {
        var pieces = new List<(bool Token, string Text)>();
        var missing = new List<string>();
        int at = 0;
        MatchCollection matches;
        try { matches = TokenRx.Matches(template); }
        catch (RegexMatchTimeoutException) { return new StyledName(source.Original, ["template"]); }

        foreach (Match m in matches)
        {
            if (m.Index > at) pieces.Add((false, template[at..m.Index]));
            string field = m.Groups[1].Value.ToLowerInvariant();
            int pad = m.Groups[2].Success ? m.Groups[2].Length : 1;
            string prevLiteral = pieces.Count > 0 && !pieces[^1].Token ? pieces[^1].Text : string.Empty;
            string value = field switch
            {
                "show" => !string.IsNullOrWhiteSpace(showOverride) ? showOverride.Trim() : Words(source.ShowWords, wordSeparator, wordCase, smallWordsLower),
                "title" => Words(source.TitleWords, wordSeparator, wordCase, smallWordsLower),
                "year" => !string.IsNullOrWhiteSpace(yearOverride) ? yearOverride.Trim() : source.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                "quality" => source.Quality ?? string.Empty,
                "season" => source.Episode is { Season: >= 0 } s ? s.Season.ToString(new string('0', pad), CultureInfo.InvariantCulture) : string.Empty,
                "episode" => source.Episode is { } e ? EpisodeText(e, pad, prevLiteral) : string.Empty,
                _ => string.Empty,
            };
            if (value.Length == 0) missing.Add(field switch
            {
                "show" => "name", "title" => "episode title", "season" => "season", "episode" => "episode number",
                _ => field,
            });
            pieces.Add((true, value));
            at = m.Index + m.Length;
        }
        if (at < template.Length) pieces.Add((false, template[at..]));

        // Drop the separators around empty fields: "Show (2014) - S01E01 - " -> "Show - S01E01".
        for (int i = 0; i < pieces.Count; i++)
        {
            if (!pieces[i].Token || pieces[i].Text.Length > 0) continue;
            int opened = 0;
            if (i > 0 && !pieces[i - 1].Token)
            {
                var prev = pieces[i - 1].Text;
                int cut = prev.Length;
                while (cut > 0 && " -._,([{".Contains(prev[cut - 1]))
                {
                    if ("([{".Contains(prev[cut - 1])) opened++;
                    cut--;
                }
                // A field at the very start keeps no leading separator either way.
                pieces[i - 1] = (false, prev[..cut]);
            }
            if (i + 1 < pieces.Count && !pieces[i + 1].Token)
            {
                var next = pieces[i + 1].Text;
                int skip = 0;
                while (opened > 0 && skip < next.Length && ")]}".Contains(next[skip])) { skip++; opened--; }
                // With nothing in front, the separator after an empty leading field goes too.
                if (pieces.Take(i).All(x => x.Text.Length == 0))
                    while (skip < next.Length && " -._,".Contains(next[skip])) skip++;
                pieces[i + 1] = (false, next[skip..]);
            }
        }

        var result = string.Concat(pieces.Select(x => x.Text));
        result = Regex.Replace(result, @"\(\s*\)|\[\s*\]|\{\s*\}", string.Empty);
        result = Regex.Replace(result, @" {2,}", " ").Trim(' ', '-', '.', '_', ',');
        return new StyledName(result, missing);
    }

    private static string EpisodeText(EpisodeKey e, int pad, string prevLiteral)
    {
        var fmt = new string('0', pad);
        var text = e.Episode.ToString(fmt, CultureInfo.InvariantCulture);
        if (e.LastEpisode != e.Episode)
        {
            char letter = prevLiteral.Length > 0 && (prevLiteral[^1] == 'E' || prevLiteral[^1] == 'e') ? prevLiteral[^1] : '\0';
            text += "-" + (letter != '\0' ? letter.ToString() : string.Empty) + e.LastEpisode.ToString(fmt, CultureInfo.InvariantCulture);
        }
        return text;
    }

    private static string Words(List<string> words, string separator, WordCase wordCase, bool smallWordsLower)
    {
        if (words.Count == 0) return string.Empty;
        var cased = words.Select((w, i) => wordCase switch
        {
            WordCase.Lower => w.ToLowerInvariant(),
            WordCase.Upper => w.ToUpperInvariant(),
            WordCase.Title => TitleWord(w, i == 0 || i == words.Count - 1, smallWordsLower),
            _ => w,
        });
        return string.Join(separator, cased);
    }

    private static string TitleWord(string word, bool edge, bool smallWordsLower)
    {
        var parts = word.Split('-');
        for (int i = 0; i < parts.Length; i++)
        {
            var w = parts[i];
            if (!w.Any(char.IsLetter)) continue;
            // "of", "the"... stay lowercase inside a title, but not as its first or last word.
            if (smallWordsLower && SmallWords.Contains(w) && !(edge && i == 0))
            {
                parts[i] = w.ToLowerInvariant();
                continue;
            }
            bool hasUpper = w.Any(char.IsUpper), hasLower = w.Any(char.IsLower);
            if (hasUpper && hasLower) continue;      // already cased: "Condor", "RussFest", "McDonald", "iPhone"
            if (hasUpper && w.Length > 1) continue;  // acronyms: "NASA", "TV"
            parts[i] = char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
        }
        return string.Join('-', parts);
    }
}
