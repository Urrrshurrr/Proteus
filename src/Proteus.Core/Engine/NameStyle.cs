using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Proteus.Core.Engine;

/// <summary>How words are capitalised in a naming style.</summary>
public enum WordCase
{
    /// <summary>Words are used exactly as they appear in each file's name.</summary>
    AsIs,
    /// <summary>Title Case: lowercase words get a capital; small words like "of" stay lowercase when the style does that.</summary>
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
    /// <summary>The show or film name exactly as written, punctuation included ("The Office (US)", "Schitt's Creek").</summary>
    public string ShowText { get; set; } = string.Empty;
    /// <summary>The episode title exactly as written ("Rome, in a Day", "Caretaker (1)").</summary>
    public string TitleText { get; set; } = string.Empty;
    public int? Year { get; set; }
    public EpisodeKey? Episode { get; set; }
    public string? Quality { get; set; }
    /// <summary>The separator between words in this name: ' ', '.', '_' or '-'.</summary>
    public char WordSeparator { get; set; } = ' ';
    public List<NamePart> Parts { get; } = [];

    public string Show => string.Join(' ', ShowWords);
    public string Title => string.Join(' ', TitleWords);

    /// <summary>A one-line summary of what was read, for tooltips.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (ShowText.Length > 0) parts.Add($"Name: {ShowText}");
        if (Year != null) parts.Add($"Year: {Year}");
        if (Episode is { } e) parts.Add(e.Season >= 0 ? $"Season {e.Season}, episode {(e.LastEpisode != e.Episode ? $"{e.Episode}-{e.LastEpisode}" : e.Episode)}" : $"Episode {e.Episode}");
        if (TitleText.Length > 0) parts.Add($"Title: {TitleText}");
        if (Quality != null) parts.Add($"Quality: {Quality}");
        return parts.Count == 0 ? "Nothing recognised in this name." : "Read from the name: " + string.Join("  ·  ", parts);
    }
}

/// <summary>A naming style learned from sample names: a template plus word spacing and capitalisation.</summary>
public sealed record LearnedStyle(string Template, string WordSeparator, WordCase Case, bool SmallWordsLower, int Agreeing, int Samples);

/// <summary>The result of applying a style to one name.</summary>
public sealed record StyledName(string Name, IReadOnlyList<string> Missing, bool FromSample = false)
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
    private static readonly Regex Word = new(@"[\p{L}\p{N}'’`!?&+]+(?:-[\p{L}\p{N}'’`!?&+]+)*", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex YearWord = new(@"^(?:19|20)\d{2}$", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex QualityWord = new(@"^(?:\d{3,4}[pi]|[48]k)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex TokenRx = new(@"<(show|year|season|episode|title|quality)(?::(0+))?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    // "[SubsPlease] " or "[GROUP][Tag] " in front of a name.
    private static readonly Regex LeadingTags = new(@"^\s*(?:\[[^\]]{1,40}\]\s*)+", RegexOptions.CultureInvariant, Timeout);
    // Anime-style absolute numbering: "Frieren - 05 (1080p)", "Show - 112 [hash]".
    private static readonly Regex Absolute = new(@"(?<=\S\s-\s)(?<e>\d{1,3})(?:v\d)?(?=\s*$|\s*[\[(]|\s+-\s)", RegexOptions.CultureInvariant, Timeout);

    // Release words that could also be real words in a title ("Max", "Extended").
    private static readonly HashSet<string> Ambiguous = new(StringComparer.Ordinal)
    {
        "max", "ma", "dd", "dv", "hd", "sd", "audio", "dual", "english", "eng", "limited", "extended", "uncut", "unrated",
        "remastered", "sub", "subs", "multi", "ray", "blu", "internal", "opus",
    };

    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "nor", "but", "of", "in", "on", "at", "to", "for", "by", "with", "from", "as", "vs",
    };

    private static bool IsQuality(string word) =>
        QualityWord.IsMatch(word) || word.Split('-').Any(QualityWord.IsMatch);

    private static bool IsNoise(string word, bool strict)
    {
        var w = word.ToLowerInvariant();
        if (QualityWord.IsMatch(w)) return true;
        if (NameMatcher.Noise.Contains(w)) return !strict || !Ambiguous.Contains(w);
        var parts = w.Split('-', StringSplitOptions.RemoveEmptyEntries);
        // "WEB-DL", "x265-GROUP", "WEBDL-1080p": a hyphenated release tag starts with a release word.
        return parts.Length > 1 && (NameMatcher.Noise.Contains(parts[0]) || QualityWord.IsMatch(parts[0]) || parts.Any(QualityWord.IsMatch));
    }

    private static Match? FindMarker(string name, int from)
    {
        foreach (var rx in new[] { NameMatcher.SxE, NameMatcher.SeasonWords, NameMatcher.NxN, NameMatcher.EpOnly, Absolute })
        {
            try
            {
                var m = rx.Match(name, from);
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
        int start = 0;
        try { start = LeadingTags.Match(name) is { Success: true } t ? t.Length : 0; }
        catch (RegexMatchTimeoutException) { }

        var marker = FindMarker(name, start);
        int headEnd = marker?.Index ?? name.Length;
        var head = Words(name, start, headEnd);

        // Quality can sit anywhere ("1080p", "WEBDL-1080p").
        foreach (var (text, at) in Words(name, start, name.Length))
            if (IsQuality(text))
            {
                var q = text.Split('-').First(QualityWord.IsMatch);
                p.Quality = q.ToLowerInvariant();
                if (q.Length == text.Length) p.Parts.Add(new NamePart("quality", at, text.Length));
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
            if (yearAt < 0 && (marker != null || i > 0) && IsNoise(head[i].Text, strict: true)) break;
            if (i > 0 && head[i].Start > 0 && name[head[i].Start - 1] == '[') break;
            show.Add(head[i]);
        }
        p.ShowText = AddWords(p, p.ShowWords, show, "show", name);

        if (marker != null)
        {
            var s = marker.Groups["s"];
            var e = marker.Groups["e"];
            var e2 = marker.Groups["e2"];
            int season = s.Success ? int.Parse(s.Value, CultureInfo.InvariantCulture) : -1;
            int episode = int.Parse(e.Value, CultureInfo.InvariantCulture);
            int last = episode;
            if (e2.Success && int.TryParse(e2.Value, out var e2v) && e2v > episode && e2v - episode < 10) last = e2v;
            p.Episode = new EpisodeKey(season, episode, last);
            if (s.Success) p.Parts.Add(new NamePart("season", s.Index, s.Length, s.Length));
            int end = last != episode ? e2.Index + e2.Length : e.Index + e.Length;
            p.Parts.Add(new NamePart("episode", e.Index, end - e.Index, e.Length));

            // Title: the words after the marker, up to the first release word or bracketed tag.
            var title = new List<(string Text, int Start)>();
            foreach (var w in Words(name, marker.Index + marker.Length, name.Length))
            {
                if (IsNoise(w.Text, strict: true)) break;
                if (w.Start > 0 && name[w.Start - 1] == '[') break; // "[A1B2C3D4]", "[WEBDL-1080p]"
                title.Add(w);
            }
            p.TitleText = AddWords(p, p.TitleWords, title, "title", name);
        }
        p.Parts.Sort((a, b) => a.Start.CompareTo(b.Start));
        return p;
    }

    /// <summary>Records a run of words and returns the text they span, punctuation included.</summary>
    private static string AddWords(ParsedName p, List<string> target, List<(string Text, int Start)> words, string field, string name)
    {
        if (words.Count == 0) return string.Empty;
        foreach (var (text, _) in words) target.Add(text);
        int start = words[0].Start, end = words[^1].Start + words[^1].Text.Length;

        // Include a closing bracket that belongs to the text: "Caretaker (1)", "The Office (US)".
        var span = name[start..end];
        int open = span.Count(c => c == '(') - span.Count(c => c == ')');
        int stretched = end;
        while (open > 0 && stretched < name.Length && name[stretched] == ')')
        {
            open--;
            stretched++;
        }
        p.Parts.Add(new NamePart(field, start, stretched - start));

        if (words.Count > 1 && (field == "show" || p.ShowWords.Count <= 1))
        {
            var gap = name.Substring(words[0].Start + words[0].Text.Length, words[1].Start - words[0].Start - words[0].Text.Length);
            if (gap.Length > 0) p.WordSeparator = gap.Contains(' ') ? ' ' : gap[0];
        }
        return name[start..stretched];
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
        else if (words.Where(w => !SmallWords.Contains(w)).All(w => !char.IsLower(w.First(char.IsLetter)))) wordCase = WordCase.Title;
        else wordCase = WordCase.AsIs;
        // Only lowercase "of", "the"... when the samples show it; otherwise leave them as each file has them.
        bool smallLower = midWords.Any(w => SmallWords.Contains(w) && w == w.ToLowerInvariant())
                          && !midWords.Any(w => SmallWords.Contains(w) && char.IsUpper(w[0]));

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

    /// <summary>A show name reduced to letters and digits, without "the" and a year, for comparing names.</summary>
    public static string ShowKey(string show)
    {
        var words = new List<string>();
        try
        {
            foreach (Match w in Word.Matches(RemoveAccents(show.ToLowerInvariant())))
            {
                var t = new string(w.Value.Where(char.IsLetterOrDigit).ToArray());
                if (t.Length == 0 || t is "the" or "a" or "an" or "and" || YearWord.IsMatch(t)) continue;
                words.Add(t);
            }
        }
        catch (RegexMatchTimeoutException) { }
        return string.Concat(words);
    }

    private static string RemoveAccents(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Finds the sample that is the same episode of the same show, so its exact title can be reused
    /// (samples from a media library carry the official titles, apostrophes and all).
    /// </summary>
    public static ParsedName? FindSameEpisode(ParsedName target, IEnumerable<ParsedName> samples)
    {
        if (target.Episode is not { } key) return null;
        var showKey = ShowKey(target.ShowText);
        ParsedName? best = null;
        foreach (var s in samples)
        {
            if (s.Episode is not { } sk || sk.Episode != key.Episode || sk.LastEpisode != key.LastEpisode) continue;
            if (sk.Season != key.Season) continue;
            if (s.TitleText.Length == 0) continue;
            var other = ShowKey(s.ShowText);
            bool sameShow = other == showKey || (showKey.Length > 0 && other.Length > 0 && NameMatcher.Similarity(s.ShowText, target.ShowText) >= 0.75);
            if (!sameShow) continue;
            if (best != null && !string.Equals(best.TitleText, s.TitleText, StringComparison.Ordinal)) return null; // two different candidates: don't guess
            best ??= s;
        }
        return best;
    }

    /// <summary>
    /// Builds a name in the given style from what <paramref name="source"/> contains.
    /// Fields the name doesn't have are left out together with the separator in front of them, and listed in Missing.
    /// When <paramref name="sameEpisode"/> (a sample of the same episode) is given, its show name and title are used as written.
    /// </summary>
    public static StyledName Apply(string template, ParsedName source, string wordSeparator = " ", WordCase wordCase = WordCase.AsIs,
        bool smallWordsLower = true, string? showOverride = null, string? yearOverride = null, ParsedName? sameEpisode = null)
    {
        var pieces = new List<(bool Token, string Text)>();
        var missing = new List<string>();
        int at = 0;
        MatchCollection matches;
        try { matches = TokenRx.Matches(template); }
        catch (RegexMatchTimeoutException) { return new StyledName(source.Original, ["template"]); }

        bool usedSample = false;
        foreach (Match m in matches)
        {
            if (m.Index > at) pieces.Add((false, template[at..m.Index]));
            string field = m.Groups[1].Value.ToLowerInvariant();
            int pad = m.Groups[2].Success ? m.Groups[2].Length : 1;
            string prevLiteral = pieces.Count > 0 && !pieces[^1].Token ? pieces[^1].Text : string.Empty;
            int? year = source.Year ?? sameEpisode?.Year;
            string value;
            switch (field)
            {
                case "show":
                    if (!string.IsNullOrWhiteSpace(showOverride)) value = showOverride.Trim();
                    else if (sameEpisode != null && sameEpisode.ShowText.Length > 0) { value = sameEpisode.ShowText; usedSample = true; }
                    else value = Render(source.ShowText, source.WordSeparator, wordSeparator, wordCase, smallWordsLower);
                    break;
                case "title":
                    if (sameEpisode != null) { value = sameEpisode.TitleText; usedSample = true; }
                    else value = Render(source.TitleText, source.WordSeparator, wordSeparator, wordCase, smallWordsLower);
                    break;
                case "year":
                    value = !string.IsNullOrWhiteSpace(yearOverride) ? yearOverride.Trim() : year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                    break;
                case "quality":
                    value = source.Quality ?? string.Empty;
                    break;
                case "season":
                    value = source.Episode is { Season: >= 0 } s ? s.Season.ToString(new string('0', pad), CultureInfo.InvariantCulture) : string.Empty;
                    break;
                case "episode":
                    value = source.Episode is { } e ? EpisodeText(e, pad, prevLiteral) : string.Empty;
                    break;
                default:
                    value = string.Empty;
                    break;
            }
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

            // Part of a compound marker ("S<season>E<episode>", "<season>x<episode>"): drop only the glued letters.
            bool PureGlue(int k) => k >= 0 && k < pieces.Count && !pieces[k].Token && pieces[k].Text.Trim().Length is > 0 and <= 2 && pieces[k].Text.Trim().All(char.IsLetter);
            bool gluedAfter = PureGlue(i + 1) && i + 2 < pieces.Count && pieces[i + 2].Token && pieces[i + 2].Text.Length > 0;
            bool gluedBefore = PureGlue(i - 1) && i >= 2 && pieces[i - 2].Token && pieces[i - 2].Text.Length > 0;
            if ((gluedAfter || gluedBefore) && i > 0 && !pieces[i - 1].Token && GluedLetters(pieces[i - 1].Text) is > 0 and var glued)
            {
                pieces[i - 1] = (false, pieces[i - 1].Text[..^glued]);
                continue;
            }
            if (gluedAfter)
            {
                pieces[i + 1] = (false, string.Empty);
                continue;
            }

            int opened = 0;
            if (i > 0 && !pieces[i - 1].Token)
            {
                var prev = pieces[i - 1].Text;
                // Letters glued to the empty field ("S" in " - S<season>", "x" in "<season>x<episode>") go with it.
                int cut = prev.Length - GluedLetters(prev);
                if (prev.Trim().Length is > 0 and <= 2 && prev.Trim().All(char.IsLetter)) cut = 0;
                while (cut > 0 && " -._,([{".Contains(prev[cut - 1]))
                {
                    if ("([{".Contains(prev[cut - 1])) opened++;
                    cut--;
                }
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
        return new StyledName(result, missing, usedSample);
    }

    /// <summary>
    /// Re-spaces and re-cases a show name or title. With spaces between words, punctuation is kept
    /// ("Rome, in a Day"); dotted or underscored styles keep the words only.
    /// </summary>
    private static string Render(string text, char sourceSeparator, string separator, WordCase wordCase, bool smallWordsLower)
    {
        if (text.Length == 0) return string.Empty;
        var t = sourceSeparator is '.' or '_' ? text.Replace(sourceSeparator, ' ') : text;
        t = Regex.Replace(t, @"\s+", " ").Trim();

        List<Match> words;
        try { words = Word.Matches(t).ToList(); }
        catch (RegexMatchTimeoutException) { return t; }
        var sb = new StringBuilder(t.Length);
        int at = 0;
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            sb.Append(t, at, w.Index - at);
            sb.Append(wordCase switch
            {
                WordCase.Lower => w.Value.ToLowerInvariant(),
                WordCase.Upper => w.Value.ToUpperInvariant(),
                WordCase.Title => TitleWord(w.Value, first: i == 0, last: i == words.Count - 1, smallWordsLower),
                _ => w.Value,
            });
            at = w.Index + w.Length;
        }
        sb.Append(t, at, t.Length - at);
        var cased = sb.ToString();

        if (separator == " ") return cased;
        // Dots, underscores or hyphens between words leave no room for commas and brackets.
        try { return string.Join(separator, Word.Matches(cased).Select(m => m.Value)); }
        catch (RegexMatchTimeoutException) { return cased.Replace(" ", separator); }
    }

    /// <summary>How many letters (1 or 2) sit glued to the end of a literal, like the "S" in " - S".</summary>
    private static int GluedLetters(string literal)
    {
        int n = 0;
        while (n < literal.Length && char.IsLetter(literal[literal.Length - 1 - n])) n++;
        if (n is 0 or > 2) return 0;
        return n == literal.Length || !char.IsLetterOrDigit(literal[literal.Length - 1 - n]) ? n : 0;
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

    private static string TitleWord(string word, bool first, bool last, bool smallWordsLower)
    {
        var parts = word.Split('-');
        for (int i = 0; i < parts.Length; i++)
        {
            var w = parts[i];
            if (!w.Any(char.IsLetter)) continue;
            bool edge = (first && i == 0) || (last && i == parts.Length - 1);
            if (SmallWords.Contains(w) && !edge)
            {
                // "of", "the"... are lowercased only when the style does that; otherwise left as the file has them.
                if (smallWordsLower) parts[i] = w.ToLowerInvariant();
                continue;
            }
            bool hasUpper = w.Any(char.IsUpper), hasLower = w.Any(char.IsLower);
            if (hasUpper && hasLower) continue;      // already cased: "Condor", "RussFest", "McDonald", "iPhone"
            if (hasUpper && w.Length > 1) continue;  // acronyms: "NASA", "TV", "II"
            int firstLetter = 0;
            while (firstLetter < w.Length && !char.IsLetter(w[firstLetter])) firstLetter++;
            // "46th" stays; "rose" -> "Rose"; "'til" -> "'Til".
            if (firstLetter > 0 && char.IsDigit(w[0])) continue;
            parts[i] = w[..firstLetter] + char.ToUpperInvariant(w[firstLetter]) + w[(firstLetter + 1)..].ToLowerInvariant();
        }
        return string.Join('-', parts);
    }
}
