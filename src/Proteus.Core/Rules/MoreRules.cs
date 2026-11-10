using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Proteus.Core.Tokens;

namespace Proteus.Core.Rules;

// ---------------------------------------------------------------------------------------------
// Rearrange (ReNamer "Rearrange", Advanced Renamer "Swap"): split the name into parts and put
// them back together in a new order.
// ---------------------------------------------------------------------------------------------
public sealed partial class RearrangeRule : RenameRule
{
    [ObservableProperty] private string _delimiter = " - ";
    [ObservableProperty] private string _pattern = "$2 - $1";

    public override string Title => "Rearrange";
    public override string Icon => "⇅";
    public override string Summary => $"Split at {Quote(Delimiter)} → {Quote(Pattern)}{TargetSuffix}";

    // $L only when no letter follows, so "$Left" is not read as "$L" + "eft".
    private static readonly Regex PartRef = new(@"\$(\d+|L(?![A-Za-z])|-\d+)", RegexOptions.Compiled);

    protected override string Transform(string input, RuleContext ctx)
    {
        if (Pattern.Length == 0) return input;
        var parts = Delimiter.Length == 0
            ? input.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : input.Split(Delimiter);
        // Nothing to rearrange when the name doesn't contain the delimiter: leave it alone.
        if (parts.Length < 2) return input;
        var expanded = PartRef.Replace(Pattern, m =>
        {
            var key = m.Groups[1].Value;
            if (key == "0") return input;
            if (key == "L") return parts.Length > 0 ? parts[^1] : string.Empty;
            if (int.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            {
                int i = n > 0 ? n - 1 : parts.Length + n;
                return i >= 0 && i < parts.Length ? parts[i] : string.Empty;
            }
            return m.Value;
        });
        return TokenEngine.Expand(expanded, ctx);
    }
}

// ---------------------------------------------------------------------------------------------
// Trim around text (BRU "crop", ReNamer "Delete from/until"): remove everything before, after or
// between markers.
// ---------------------------------------------------------------------------------------------
public enum CropMode { RemoveBefore, RemoveAfter, KeepBetween, RemoveBetween }

public sealed partial class CropRule : RenameRule
{
    [ObservableProperty] private CropMode _mode = CropMode.RemoveAfter;
    [ObservableProperty] private string _marker = string.Empty;
    [ObservableProperty] private string _endMarker = string.Empty;
    [ObservableProperty] private bool _includeMarker;
    [ObservableProperty] private bool _useLastMatch;
    [ObservableProperty] private bool _caseSensitive;

    public override string Title => "Trim around text";
    public override string Icon => "[ ]";
    public override string Summary => Marker.Length == 0 ? "Remove text before, after or between markers" : Mode switch
    {
        CropMode.RemoveBefore => $"Remove everything before {Quote(Marker)}",
        CropMode.RemoveAfter => $"Remove everything after {Quote(Marker)}",
        CropMode.KeepBetween => $"Keep only what is between {Quote(Marker)} and {Quote(EndMarker)}",
        _ => $"Remove from {Quote(Marker)} to {Quote(EndMarker)}",
    } + TargetSuffix;

    protected override string Transform(string input, RuleContext ctx)
    {
        if (Marker.Length == 0) return input;
        var cmp = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int a = UseLastMatch ? input.LastIndexOf(Marker, cmp) : input.IndexOf(Marker, cmp);
        if (a < 0) return input;
        int aEnd = a + Marker.Length;

        switch (Mode)
        {
            case CropMode.RemoveBefore:
                return IncludeMarker ? input[aEnd..] : input[a..];
            case CropMode.RemoveAfter:
                return IncludeMarker ? input[..a] : input[..aEnd];
            default:
            {
                var end = EndMarker.Length == 0 ? Marker : EndMarker;
                int b = input.IndexOf(end, aEnd, cmp);
                if (b < 0) return input;
                int bEnd = b + end.Length;
                if (Mode == CropMode.KeepBetween)
                    return IncludeMarker ? input[a..bEnd] : input[aEnd..b];
                return IncludeMarker ? input[..a] + input[bEnd..] : input[..aEnd] + input[b..];
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Move (BRU "Move/Copy parts", Advanced Renamer "Move"): take some characters or a piece of text
// and put it somewhere else.
// ---------------------------------------------------------------------------------------------
public enum MoveSource { Characters, Text }
public enum MoveDestination { Start, End, Position }

public sealed partial class MoveRule : RenameRule
{
    [ObservableProperty] private MoveSource _source = MoveSource.Characters;
    [ObservableProperty] private int _start = 1;
    [ObservableProperty] private int _count = 4;
    [ObservableProperty] private bool _fromEnd;
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private bool _textIsRegex;
    [ObservableProperty] private MoveDestination _destination = MoveDestination.End;
    [ObservableProperty] private int _toPosition = 1;
    [ObservableProperty] private string _separator = " ";

    public override string Title => "Move";
    public override string Icon => "⇥";
    public override string Summary
    {
        get
        {
            var what = Source == MoveSource.Characters
                ? $"{Count} character{(Count == 1 ? "" : "s")} {(FromEnd ? "from the end" : $"from position {Start}")}"
                : Quote(Text);
            var where = Destination switch
            {
                MoveDestination.Start => "to the start",
                MoveDestination.End => "to the end",
                _ => $"to position {ToPosition}",
            };
            return $"Move {what} {where}{TargetSuffix}";
        }
    }

    private const string Separators = " -_.,\u2013\u2014";

    /// <summary>
    /// Takes the piece out and removes one separator run next to the gap it leaves ("2024 - Trip"
    /// minus "2024" is "Trip", "Invoice_2024_Acme" minus "2024" is "Invoice_Acme"). The rest of the
    /// name is left exactly as it was.
    /// </summary>
    private static string CloseGap(string input, int at, int len)
    {
        int left = at, right = at + len;
        int l = left;
        while (l > 0 && Separators.Contains(input[l - 1])) l--;
        int r = right;
        while (r < input.Length && Separators.Contains(input[r])) r++;
        if (left == 0) return input[r..];                      // piece was at the start
        if (right == input.Length) return input[..l];          // piece was at the end
        if (r > right) return input[..left] + input[r..];       // drop the run after the gap
        return input[..l] + input[right..];                     // or the one before it
    }

    protected override string Transform(string input, RuleContext ctx)
    {
        int at, len;
        if (Source == MoveSource.Characters)
        {
            if (Count <= 0 || input.Length == 0) return input;
            if (FromEnd)
            {
                int end = input.Length - Math.Max(0, Start - 1);
                at = Math.Max(0, end - Count);
                len = Math.Max(0, end - at);
            }
            else
            {
                at = Math.Max(0, Start - 1);
                if (at >= input.Length) return input;
                len = Math.Min(Count, input.Length - at);
            }
        }
        else
        {
            if (Text.Length == 0) return input;
            if (TextIsRegex)
            {
                Match m;
                try { m = Regex.Match(input, Text, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)); }
                catch (Exception) { return input; }
                if (!m.Success || m.Length == 0) return input;
                at = m.Index; len = m.Length;
            }
            else
            {
                at = input.IndexOf(Text, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return input;
                len = Text.Length;
            }
        }
        if (len == 0) return input;
        // Never cut an emoji or other two-part character in half.
        int stop = TextSafe.BoundaryAfter(input, at + len);
        at = TextSafe.Boundary(input, at);
        len = stop - at;
        if (len <= 0) return input;

        var piece = input.Substring(at, len).Trim();
        var rest = CloseGap(input, at, len);
        if (piece.Length == 0) return input;
        if (rest.Length == 0) return piece;
        var sep = Separator;

        return Destination switch
        {
            MoveDestination.Start => piece + sep + rest,
            MoveDestination.End => rest + sep + piece,
            _ => rest.Insert(TextSafe.Boundary(rest, Math.Clamp(ToPosition - 1, 0, rest.Length)), piece),
        };
    }
}

// ---------------------------------------------------------------------------------------------
// Reformat date (ReNamer "Reformat Date"): find dates written one way and rewrite them.
// ---------------------------------------------------------------------------------------------
public sealed partial class ReformatDateRule : RenameRule
{
    [ObservableProperty] private string _sourceFormats = "dd.MM.yyyy; dd-MM-yyyy; dd_MM_yyyy";
    [ObservableProperty] private string _targetFormat = "yyyy-MM-dd";
    [ObservableProperty] private bool _englishMonthNames = true;

    private string? _cacheKey;
    private List<(string Format, Regex Regex)> _compiled = [];

    public override string Title => "Reformat dates";
    public override string Icon => "31";
    public override string Summary => $"{SourceFormats.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "?"}… → {TargetFormat}{TargetSuffix}";

    protected override string Transform(string input, RuleContext ctx)
    {
        if (TargetFormat.Length == 0) return input;
        var culture = EnglishMonthNames ? CultureInfo.InvariantCulture : TokenEngine.DateCulture;
        foreach (var (format, regex) in Compile())
        {
            try
            {
                input = regex.Replace(input, m =>
                {
                    if (!DateTime.TryParseExact(m.Value, format, culture, DateTimeStyles.AllowWhiteSpaces, out var d)) return m.Value;
                    try { return Safe(d.ToString(TargetFormat, culture)); }
                    catch (FormatException) { return m.Value; }
                });
            }
            catch (RegexMatchTimeoutException) { }
        }
        return input;
    }

    private static string Safe(string s) => s.Replace('/', '-').Replace(':', '-');

    private List<(string, Regex)> Compile()
    {
        var key = SourceFormats + "|" + EnglishMonthNames;
        if (key == _cacheKey) return _compiled;
        _cacheKey = key;
        var list = new List<(string, Regex)>();
        foreach (var raw in SourceFormats.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { list.Add((raw, new Regex(@"(?<!\d)" + FormatToRegex(raw) + @"(?!\d)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)))); }
            catch (ArgumentException) { }
        }
        _compiled = list;
        return list;
    }

    /// <summary>Turns a .NET date format like dd.MM.yyyy into a regex that finds such dates.</summary>
    public static string FormatToRegex(string format)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < format.Length)
        {
            char c = format[i];
            int run = 1;
            while (i + run < format.Length && format[i + run] == c) run++;
            sb.Append(c switch
            {
                'y' => run >= 3 ? @"\d{4}" : @"\d{2}",
                'M' => run >= 4 ? @"[A-Za-zÀ-ɏ]{3,}" : run == 3 ? @"[A-Za-zÀ-ɏ]{3}" : run == 2 ? @"\d{2}" : @"\d{1,2}",
                'd' => run >= 3 ? @"[A-Za-z]{3,}" : run == 2 ? @"\d{2}" : @"\d{1,2}",
                'H' or 'h' or 'm' or 's' => run == 2 ? @"\d{2}" : @"\d{1,2}",
                't' => run == 2 ? "[AaPp][Mm]" : "[AaPp]",
                _ => Regex.Escape(new string(c, run)),
            });
            i += run;
        }
        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------------------------
// Transliterate (ReNamer "Translit"): convert other alphabets to plain Latin letters.
// ---------------------------------------------------------------------------------------------
public enum TranslitAlphabet { Cyrillic, Greek, German, AllToAscii }

public sealed partial class TransliterateRule : RenameRule
{
    [ObservableProperty] private TranslitAlphabet _alphabet = TranslitAlphabet.AllToAscii;
    [ObservableProperty] private string _customPairs = string.Empty;

    public override string Title => "Transliterate";
    public override string Icon => "Я→A";
    public override string Summary => Alphabet switch
    {
        TranslitAlphabet.Cyrillic => "Cyrillic → Latin",
        TranslitAlphabet.Greek => "Greek → Latin",
        TranslitAlphabet.German => "ä ö ü ß → ae oe ue ss",
        _ => "Any alphabet → plain letters",
    } + (CustomPairs.Length > 0 ? ", plus your own pairs" : "") + TargetSuffix;

    private static readonly Dictionary<char, string> CyrillicMap = BuildMap(
        "аa бb вv гg дd еe ёyo жzh зz иi йy кk лl мm нn оo пp " +
        "рr сs тt уu фf хkh цts чch шsh щshch ъ_ ыy ь_ эe юyu яya " +
        "іi їyi єye ґg ўu јj ђdj ћc џdz љlj њnj ќkj ѓgj ѕdz");

    private static readonly Dictionary<char, string> GreekMap = BuildMap(
        "αa βv γg δd εe ζz ηi θth ιi κk λl μm νn ξx οo πp " +
        "ρr σs ςs τt υy φf χch ψps ωo άa έe ήi ίi όo ύy ώo ϊi ϋy");

    private static readonly Dictionary<char, string> GermanMap = new()
    {
        ['ä'] = "ae", ['ö'] = "oe", ['ü'] = "ue", ['ß'] = "ss",
        ['Ä'] = "Ae", ['Ö'] = "Oe", ['Ü'] = "Ue", ['ẞ'] = "SS",
    };

    private static readonly Dictionary<char, string> LigatureMap = new()
    {
        ['æ'] = "ae", ['Æ'] = "Ae", ['œ'] = "oe", ['Œ'] = "Oe", ['ø'] = "o", ['Ø'] = "O",
        ['ł'] = "l", ['Ł'] = "L", ['đ'] = "d", ['Đ'] = "D", ['ð'] = "d", ['Þ'] = "Th",
        ['þ'] = "th", ['ß'] = "ss", ['ı'] = "i", ['–'] = "-", ['—'] = "-",
        ['‘'] = "'", ['’'] = "'", ['“'] = "", ['”'] = "", ['…'] = "...",
    };

    private static Dictionary<char, string> BuildMap(string spec)
    {
        var map = new Dictionary<char, string>();
        foreach (var pair in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var lower = pair[0];
            var latin = pair[1..] == "_" ? string.Empty : pair[1..];
            map[lower] = latin;
            var upper = char.ToUpperInvariant(lower);
            if (upper != lower)
                map[upper] = latin.Length == 0 ? latin : char.ToUpperInvariant(latin[0]) + latin[1..];
        }
        return map;
    }

    private string? _customKey;
    private List<(string From, string To)> _custom = [];

    protected override string Transform(string input, RuleContext ctx)
    {
        var s = input;
        foreach (var (from, to) in Custom()) s = s.Replace(from, to);

        if (Alphabet == TranslitAlphabet.German) return Map(s, GermanMap);
        if (Alphabet == TranslitAlphabet.Cyrillic) return Map(s, CyrillicMap);
        if (Alphabet == TranslitAlphabet.Greek) return Map(s, GreekMap);

        // Everything: known alphabets, ligatures, then strip accents from Latin letters. Characters
        // with no plain form (Chinese, Arabic, emoji...) are kept as they are, never deleted.
        s = Map(Map(Map(s, CyrillicMap), GreekMap), LigatureMap);
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        var kept = new StringBuilder();
        bool afterAscii = false;
        foreach (var c in normalized)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                if (!afterAscii) sb.Append(c); // a mark on a kept character stays with it
                continue;
            }
            afterAscii = c < 128;
            sb.Append(c);
            if (!afterAscii && kept.Length < 12 && !char.IsLowSurrogate(c)) kept.Append(c);
        }
        if (kept.Length > 0)
            RuntimeError = $"Some characters have no plain-letter form and were kept as they are (for example {kept}).";
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string Map(string s, Dictionary<char, string> map)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s) sb.Append(map.TryGetValue(c, out var r) ? r : c.ToString());
        return sb.ToString();
    }

    private List<(string, string)> Custom()
    {
        if (_customKey == CustomPairs) return _custom;
        _customKey = CustomPairs;
        var list = new List<(string, string)>();
        foreach (var item in CustomPairs.Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = item.IndexOf('=');
            if (eq > 0) list.Add((item[..eq].Trim(), item[(eq + 1)..].Trim()));
        }
        _custom = list;
        return list;
    }
}

// ---------------------------------------------------------------------------------------------
// Randomize (ReNamer "Randomize"): random characters that stay the same between previews.
// ---------------------------------------------------------------------------------------------
public enum RandomCharset { Digits, Lowercase, Letters, LettersAndDigits, Hex }

public sealed partial class RandomizeRule : RenameRule
{
    [ObservableProperty] private int _length = 6;
    [ObservableProperty] private RandomCharset _charset = RandomCharset.LettersAndDigits;
    [ObservableProperty] private NumberPlacement _placement = NumberPlacement.Suffix;
    [ObservableProperty] private string _separator = "_";
    [ObservableProperty] private int _seed = 1;

    public override string Title => "Randomize";
    public override string Icon => "?!";
    public override bool SupportsTarget => false;
    public override string Summary => $"{Length} random {Charset switch
    {
        RandomCharset.Digits => "digits",
        RandomCharset.Lowercase => "lowercase letters",
        RandomCharset.Letters => "letters",
        RandomCharset.Hex => "hex characters",
        _ => "letters and digits",
    }} {Placement switch
    {
        NumberPlacement.Prefix => "before the name",
        NumberPlacement.Suffix => "after the name",
        _ => "as the whole name",
    }}";

    protected override string Transform(string input, RuleContext ctx)
    {
        var chars = Charset switch
        {
            RandomCharset.Digits => "0123456789",
            RandomCharset.Lowercase => "abcdefghijklmnopqrstuvwxyz",
            RandomCharset.Letters => "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ",
            RandomCharset.Hex => "0123456789abcdef",
            _ => "abcdefghijklmnopqrstuvwxyz0123456789",
        };
        // Stable per file and seed, so the preview doesn't flicker and the rename matches it.
        var rng = new Random(StableHash(ctx.File.FullPath) ^ Seed * 7919);
        var sb = new StringBuilder(Math.Clamp(Length, 0, 64));
        for (int i = 0; i < Math.Clamp(Length, 0, 64); i++) sb.Append(chars[rng.Next(chars.Length)]);
        var r = sb.ToString();
        return Placement switch
        {
            NumberPlacement.Prefix => r + Separator + input,
            NumberPlacement.Suffix => input + Separator + r,
            _ => r,
        };
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in s.ToUpperInvariant()) { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }
}

// ---------------------------------------------------------------------------------------------
// List / mapping (ReNamer "User Input" and "Mapping", Advanced Renamer "List"): supply names.
// ---------------------------------------------------------------------------------------------
public enum ListMode { NamesInOrder, Mapping }

public sealed partial class ListRule : RenameRule
{
    [ObservableProperty] private ListMode _mode = ListMode.NamesInOrder;
    [ObservableProperty] private string _lines = string.Empty;
    [ObservableProperty] private bool _keepExtension = true;

    private string? _parsedKey;
    private string[] _names = [];
    private Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public override string Title => "List of names";
    public override string Icon => "☰";
    public override bool SupportsTarget => false;

    [JsonIgnore]
    public int LineCount { get { Parse(); return Mode == ListMode.Mapping ? _map.Count : _names.Length; } }

    public override string Summary => Mode == ListMode.NamesInOrder
        ? $"{LineCount} new name{(LineCount == 1 ? "" : "s")}, applied in list order"
        : $"{LineCount} old → new mapping{(LineCount == 1 ? "" : "s")}";

    partial void OnLinesChanged(string value) => OnPropertyChanged(nameof(LineCount));
    partial void OnModeChanged(ListMode value) => OnPropertyChanged(nameof(LineCount));

    protected override NameParts ApplyCore(NameParts name, RuleContext ctx)
    {
        Parse();
        string? replacement = null;
        if (Mode == ListMode.NamesInOrder)
        {
            var (index, _) = ScopedPosition(ctx);
            if (index < _names.Length) replacement = TokenEngine.Expand(_names[index], ctx);
        }
        else if (_map.TryGetValue(ctx.File.FullPath, out var byFull) || _map.TryGetValue(ctx.File.FileName, out byFull) || _map.TryGetValue(ctx.File.BaseName, out byFull)
                 || _map.TryGetValue(name.Full, out byFull) || _map.TryGetValue(name.Base, out byFull))
        {
            replacement = TokenEngine.Expand(byFull, ctx);
        }
        if (string.IsNullOrEmpty(replacement)) return name;

        if (KeepExtension && name.Extension.Length > 0 &&
            !replacement.EndsWith(name.Extension, StringComparison.OrdinalIgnoreCase))
            return name with { Base = replacement };
        return NameParts.FromFull(replacement, ctx.File.IsFolder);
    }

    protected override string Transform(string input, RuleContext ctx) => input;

    private static readonly string[] Separators = ["\t", " -> ", "->", " => ", "=>", "|", ","];

    private void Parse()
    {
        if (_parsedKey == Lines + "|" + Mode) return;
        _parsedKey = Lines + "|" + Mode;
        var rows = Lines.Replace("\r\n", "\n").Split('\n');
        if (Mode == ListMode.NamesInOrder)
        {
            // Keep blank lines out, so the n-th name always lines up with the n-th file.
            _names = rows.Select(r => r.Trim()).Where(r => r.Length > 0).ToArray();
            return;
        }
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var line = row.Trim();
            if (line.Length == 0) continue;
            foreach (var sep in Separators)
            {
                int i = line.IndexOf(sep, StringComparison.Ordinal);
                if (i <= 0) continue;
                var from = line[..i].Trim().Trim('"');
                var to = line[(i + sep.Length)..].Trim().Trim('"');
                if (from.Length > 0 && to.Length > 0) map[from] = to;
                break;
            }
        }
        _map = map;
    }
}
