using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Proteus.Core.Tokens;

namespace Proteus.Core.Rules;

public enum Occurrence { All, First, Last }

public sealed partial class ReplaceRule : RenameRule
{
    [ObservableProperty] private string _find = string.Empty;
    [ObservableProperty] private string _replaceWith = string.Empty;
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private Occurrence _occurrence = Occurrence.All;

    private Regex? _cache;
    private string? _cacheKey;

    public override string Title => "Replace";
    public override string Icon => "⇄";
    public override string Summary => Find.Length == 0
        ? "Find and replace text"
        : $"{(UseRegex ? "Regex " : "")}{Quote(Find)} → {Quote(ReplaceWith)}{(Occurrence != Occurrence.All ? $", {Occurrence.ToString().ToLowerInvariant()} only" : "")}{TargetSuffix}";

    [JsonIgnore] public string? Error { get; private set; }

    /// <summary>A readable problem with the pattern, for the editor; null when fine.</summary>
    [JsonIgnore]
    public string? RegexError
    {
        get
        {
            if (!UseRegex || Find.Length == 0) return null;
            try { _ = new Regex(Find); return null; }
            catch (ArgumentException ex) { return ex.Message; }
        }
    }

    partial void OnFindChanged(string value) => OnPropertyChanged(nameof(RegexError));
    partial void OnUseRegexChanged(bool value) => OnPropertyChanged(nameof(RegexError));

    protected override string Transform(string input, RuleContext ctx)
    {
        if (Find.Length == 0) return input;
        var replacement = TokenEngine.Expand(ReplaceWith, ctx);
        var regex = GetRegex();
        if (regex == null) return input;

        if (!UseRegex) replacement = replacement.Replace("$", "$$");
        try
        {
            switch (Occurrence)
            {
                case Occurrence.First:
                    return regex.Replace(input, replacement, 1);
                case Occurrence.Last:
                {
                    Match? last = null;
                    foreach (Match m in regex.Matches(input)) last = m;
                    if (last == null) return input;
                    return input[..last.Index] + last.Result(replacement) + input[(last.Index + last.Length)..];
                }
                default:
                    return regex.Replace(input, replacement);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    private Regex? GetRegex()
    {
        var key = $"{UseRegex}|{CaseSensitive}|{Find}";
        if (_cacheKey == key) return _cache;
        _cacheKey = key;
        try
        {
            var opts = RegexOptions.CultureInvariant | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            _cache = new Regex(UseRegex ? Find : Regex.Escape(Find), opts, TimeSpan.FromMilliseconds(250));
            Error = null;
        }
        catch (ArgumentException ex)
        {
            _cache = null;
            Error = ex.Message;
        }
        return _cache;
    }
}

public enum InsertPosition { Prefix, Suffix, AtPosition, BeforeText, AfterText }

public sealed partial class InsertRule : RenameRule
{
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private InsertPosition _position = InsertPosition.Prefix;
    [ObservableProperty] private int _index = 1;
    [ObservableProperty] private bool _fromEnd;
    [ObservableProperty] private string _anchor = string.Empty;

    public override string Title => "Insert";
    public override string Icon => "+";
    public override string Summary => Text.Length == 0 ? "Insert text" : Position switch
    {
        InsertPosition.Prefix => $"Add {Quote(Text)} at the start",
        InsertPosition.Suffix => $"Add {Quote(Text)} at the end",
        InsertPosition.AtPosition => $"Insert {Quote(Text)} at position {Index}{(FromEnd ? " from end" : "")}",
        InsertPosition.BeforeText => $"Insert {Quote(Text)} before {Quote(Anchor)}",
        _ => $"Insert {Quote(Text)} after {Quote(Anchor)}",
    } + TargetSuffix;

    protected override string Transform(string input, RuleContext ctx)
    {
        var text = TokenEngine.Expand(Text, ctx);
        if (text.Length == 0) return input;
        switch (Position)
        {
            case InsertPosition.Prefix: return text + input;
            case InsertPosition.Suffix: return input + text;
            case InsertPosition.AtPosition:
            {
                int pos = Math.Clamp(Index - 1, 0, input.Length);
                if (FromEnd) pos = Math.Clamp(input.Length - (Index - 1), 0, input.Length);
                return input.Insert(pos, text);
            }
            case InsertPosition.BeforeText:
            case InsertPosition.AfterText:
            {
                if (Anchor.Length == 0) return input;
                int at = input.IndexOf(Anchor, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return input;
                if (Position == InsertPosition.AfterText) at += Anchor.Length;
                return input.Insert(at, text);
            }
        }
        return input;
    }
}

public sealed partial class RemoveRule : RenameRule
{
    [ObservableProperty] private int _start = 1;
    [ObservableProperty] private int _count = 1;
    [ObservableProperty] private bool _fromEnd;

    public override string Title => "Remove characters";
    public override string Icon => "−";
    public override string Summary =>
        $"Remove {Count} character{(Count == 1 ? "" : "s")} {(FromEnd ? "from the end" : "from the start")}{(Start > 1 ? $", skipping {Start - 1}" : "")}{TargetSuffix}";

    protected override string Transform(string input, RuleContext ctx)
    {
        if (Count <= 0 || input.Length == 0) return input;
        int skip = Math.Max(0, Start - 1);
        if (!FromEnd)
        {
            if (skip >= input.Length) return input;
            int n = Math.Min(Count, input.Length - skip);
            return input.Remove(skip, n);
        }
        int endExclusive = input.Length - skip;
        if (endExclusive <= 0) return input;
        int begin = Math.Max(0, endExclusive - Count);
        return input.Remove(begin, endExclusive - begin);
    }
}

public enum CaseMode { Lower, Upper, Title, Sentence, Invert }

public sealed partial class CaseRule : RenameRule
{
    [ObservableProperty] private CaseMode _mode = CaseMode.Title;

    public override string Title => "Change case";
    public override string Icon => "Aa";
    public override string Summary => Mode switch
    {
        CaseMode.Lower => "lowercase",
        CaseMode.Upper => "UPPERCASE",
        CaseMode.Title => "Title Case",
        CaseMode.Sentence => "Sentence case",
        _ => "iNVERT cASE",
    } + TargetSuffix;

    protected override string Transform(string input, RuleContext ctx)
    {
        var culture = CultureInfo.CurrentCulture;
        switch (Mode)
        {
            case CaseMode.Lower: return input.ToLower(culture);
            case CaseMode.Upper: return input.ToUpper(culture);
            case CaseMode.Invert:
                return new string(input.Select(c => char.IsUpper(c) ? char.ToLower(c, culture) : char.ToUpper(c, culture)).ToArray());
            case CaseMode.Sentence:
            {
                var lower = input.ToLower(culture);
                var sb = new StringBuilder(lower);
                for (int i = 0; i < sb.Length; i++)
                {
                    if (char.IsLetter(sb[i])) { sb[i] = char.ToUpper(sb[i], culture); break; }
                }
                return sb.ToString();
            }
            default:
            {
                // Title case: capitalise the first letter after any separator.
                var sb = new StringBuilder(input.Length);
                bool start = true;
                foreach (var c in input)
                {
                    if (char.IsLetter(c))
                    {
                        sb.Append(start ? char.ToUpper(c, culture) : char.ToLower(c, culture));
                        start = false;
                    }
                    else
                    {
                        sb.Append(c);
                        start = c is ' ' or '_' or '-' or '.' or '(' or '[' or '{' or '&' or '+';
                    }
                }
                return sb.ToString();
            }
        }
    }
}

public enum NumberPlacement { Prefix, Suffix, Replace }

public sealed partial class NumberingRule : RenameRule
{
    [ObservableProperty] private int _startAt = 1;
    [ObservableProperty] private int _step = 1;
    [ObservableProperty] private int _digits = 2;
    [ObservableProperty] private NumberPlacement _placement = NumberPlacement.Prefix;
    [ObservableProperty] private string _separator = " - ";
    [ObservableProperty] private bool _resetPerFolder;

    public override string Title => "Numbering";
    public override string Icon => "#";
    public override bool SupportsTarget => false;
    public override string Summary
    {
        get
        {
            var first = TokenEngine.Pad(StartAt, Digits);
            var second = TokenEngine.Pad(StartAt + Step, Digits);
            var where = Placement switch
            {
                NumberPlacement.Prefix => "before the name",
                NumberPlacement.Suffix => "after the name",
                _ => "as the whole name",
            };
            return $"{first}, {second}, … {where}{(ResetPerFolder ? ", per folder" : "")}";
        }
    }

    protected override string Transform(string input, RuleContext ctx)
    {
        long idx = ResetPerFolder ? ctx.FolderIndex : ctx.Index;
        var number = TokenEngine.Pad(StartAt + idx * Step, Digits);
        return Placement switch
        {
            NumberPlacement.Prefix => number + Separator + input,
            NumberPlacement.Suffix => input + Separator + number,
            _ => number,
        };
    }
}

public sealed partial class TemplateRule : RenameRule
{
    [ObservableProperty] private string _template = "<name>";

    public override string Title => "New name";
    public override string Icon => "T";
    public override string Summary => $"Name becomes {Quote(Template)}{TargetSuffix}";

    protected override string Transform(string input, RuleContext ctx) =>
        TokenEngine.Expand(Template.Replace("<current>", input.Replace("<", "\\<").Replace(">", "\\>")), ctx);
}

public sealed partial class CleanupRule : RenameRule
{
    [ObservableProperty] private bool _underscoresToSpaces = true;
    [ObservableProperty] private bool _dotsToSpaces;
    [ObservableProperty] private bool _dashesToSpaces;
    [ObservableProperty] private bool _percent20ToSpaces = true;
    [ObservableProperty] private bool _removeRoundBrackets;
    [ObservableProperty] private bool _removeSquareBrackets;
    [ObservableProperty] private bool _removeCurlyBrackets;
    [ObservableProperty] private bool _stripDigits;
    [ObservableProperty] private bool _stripSymbols;
    [ObservableProperty] private bool _stripDiacritics;
    [ObservableProperty] private string _stripCharacters = string.Empty;
    [ObservableProperty] private bool _collapseSpaces = true;
    [ObservableProperty] private bool _trim = true;
    [ObservableProperty] private string _spaceReplacement = string.Empty;
    [ObservableProperty] private int _maxLength;

    public override string Title => "Clean up";
    public override string Icon => "≈";
    public override string Summary
    {
        get
        {
            var parts = new List<string>();
            if (UnderscoresToSpaces || DotsToSpaces || DashesToSpaces || Percent20ToSpaces) parts.Add("separators → spaces");
            if (RemoveRoundBrackets || RemoveSquareBrackets || RemoveCurlyBrackets) parts.Add("drop bracketed text");
            if (StripDigits) parts.Add("no digits");
            if (StripSymbols) parts.Add("no symbols");
            if (StripDiacritics) parts.Add("no accents");
            if (StripCharacters.Length > 0) parts.Add($"strip {Quote(StripCharacters)}");
            if (CollapseSpaces) parts.Add("single spaces");
            if (MaxLength > 0) parts.Add($"max {MaxLength} characters");
            if (Trim) parts.Add("trim");
            if (SpaceReplacement.Length > 0) parts.Add($"spaces → {Quote(SpaceReplacement)}");
            return parts.Count == 0 ? "Tidy up names" : string.Join(", ", parts) + TargetSuffix;
        }
    }

    private static readonly Regex Round = new(@"\s*\([^()]*\)", RegexOptions.Compiled);
    private static readonly Regex Square = new(@"\s*\[[^\[\]]*\]", RegexOptions.Compiled);
    private static readonly Regex Curly = new(@"\s*\{[^{}]*\}", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);

    protected override string Transform(string input, RuleContext ctx)
    {
        var s = input;
        if (Percent20ToSpaces) s = s.Replace("%20", " ");
        if (UnderscoresToSpaces) s = s.Replace('_', ' ');
        if (DotsToSpaces) s = s.Replace('.', ' ');
        if (DashesToSpaces) s = s.Replace('-', ' ');
        if (RemoveRoundBrackets) s = Round.Replace(s, string.Empty);
        if (RemoveSquareBrackets) s = Square.Replace(s, string.Empty);
        if (RemoveCurlyBrackets) s = Curly.Replace(s, string.Empty);
        if (StripDiacritics) s = RemoveDiacritics(s);
        if (StripDigits) s = new string(s.Where(c => !char.IsDigit(c)).ToArray());
        if (StripSymbols) s = new string(s.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c is '.' or '-' or '_').ToArray());
        if (StripCharacters.Length > 0) s = new string(s.Where(c => !StripCharacters.Contains(c)).ToArray());
        if (CollapseSpaces) s = Spaces.Replace(s, " ");
        if (Trim) s = s.Trim();
        if (SpaceReplacement.Length > 0) s = s.Replace(" ", SpaceReplacement);
        if (MaxLength > 0 && s.Length > MaxLength) s = s[..MaxLength].TrimEnd();
        return s;
    }

    private static string RemoveDiacritics(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

public enum ExtensionMode { Change, Remove, Lowercase, Uppercase, Append }

public sealed partial class ExtensionRule : RenameRule
{
    [ObservableProperty] private ExtensionMode _mode = ExtensionMode.Lowercase;
    [ObservableProperty] private string _newExtension = string.Empty;

    public override string Title => "Extension";
    public override string Icon => ".x";
    public override bool SupportsTarget => false;
    public override string Summary => Mode switch
    {
        ExtensionMode.Change => $"Change extension to .{NewExtension.TrimStart('.')}",
        ExtensionMode.Remove => "Remove extension",
        ExtensionMode.Lowercase => "Lowercase extension",
        ExtensionMode.Uppercase => "Uppercase extension",
        _ => $"Append .{NewExtension.TrimStart('.')}",
    };

    protected override NameParts ApplyCore(NameParts name, RuleContext ctx)
    {
        if (ctx.File.IsFolder) return name;
        var ext = TokenEngine.Expand(NewExtension, ctx).Trim().TrimStart('.');
        return Mode switch
        {
            ExtensionMode.Change => name with { Extension = ext.Length == 0 ? string.Empty : "." + ext },
            ExtensionMode.Remove => name with { Extension = string.Empty },
            ExtensionMode.Lowercase => name with { Extension = name.Extension.ToLowerInvariant() },
            ExtensionMode.Uppercase => name with { Extension = name.Extension.ToUpperInvariant() },
            _ => ext.Length == 0 ? name : new NameParts(name.Base + name.Extension, "." + ext),
        };
    }

    protected override string Transform(string input, RuleContext ctx) => input;
}

public sealed partial class PadNumbersRule : RenameRule
{
    [ObservableProperty] private int _digits = 3;
    [ObservableProperty] private bool _removePadding;

    public override string Title => "Pad numbers";
    public override string Icon => "0#";
    public override string Summary => RemovePadding
        ? $"Remove leading zeros from numbers{TargetSuffix}"
        : $"Zero-pad every number to {Digits} digits{TargetSuffix}";

    private static readonly Regex Numbers = new(@"\d+", RegexOptions.Compiled);

    protected override string Transform(string input, RuleContext ctx)
    {
        if (RemovePadding) return Numbers.Replace(input, m => m.Value.TrimStart('0') is { Length: > 0 } t ? t : "0");
        return Digits <= 0 ? input : Numbers.Replace(input, m => m.Value.Length >= Digits ? m.Value : m.Value.PadLeft(Digits, '0'));
    }
}
