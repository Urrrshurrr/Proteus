using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Proteus.Core.Rules;

/// <summary>Which part of the name a rule works on.</summary>
public enum RuleTarget
{
    Name,
    Extension,
    FullName,
}

/// <summary>
/// One step in the rule stack. Rules are observable so the UI can bind straight to their
/// settings, and any change triggers a new preview.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ReplaceRule), "replace")]
[JsonDerivedType(typeof(InsertRule), "insert")]
[JsonDerivedType(typeof(RemoveRule), "remove")]
[JsonDerivedType(typeof(CaseRule), "case")]
[JsonDerivedType(typeof(NumberingRule), "numbering")]
[JsonDerivedType(typeof(TemplateRule), "template")]
[JsonDerivedType(typeof(CleanupRule), "cleanup")]
[JsonDerivedType(typeof(ExtensionRule), "extension")]
[JsonDerivedType(typeof(PadNumbersRule), "padnumbers")]
[JsonDerivedType(typeof(RearrangeRule), "rearrange")]
[JsonDerivedType(typeof(CropRule), "crop")]
[JsonDerivedType(typeof(MoveRule), "move")]
[JsonDerivedType(typeof(ReformatDateRule), "reformatdate")]
[JsonDerivedType(typeof(TransliterateRule), "transliterate")]
[JsonDerivedType(typeof(RandomizeRule), "randomize")]
[JsonDerivedType(typeof(ListRule), "list")]
[JsonDerivedType(typeof(ScriptRule), "javascript")]
public abstract partial class RenameRule : ObservableObject
{
    private Regex? _scopeRegex;
    private string? _scopeRegexSource;

    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private RuleTarget _target = RuleTarget.Name;

    /// <summary>Optional filter: rule only applies to files whose original name matches.</summary>
    [ObservableProperty] private string _scopePattern = string.Empty;
    [ObservableProperty] private bool _scopeIsRegex;
    [ObservableProperty] private bool _scopeInvert;

    [JsonIgnore] public abstract string Title { get; }
    [JsonIgnore] public abstract string Icon { get; }
    [JsonIgnore] public abstract string Summary { get; }

    /// <summary>Whether the target (name / extension / full) option makes sense for this rule.</summary>
    [JsonIgnore] public virtual bool SupportsTarget => true;

    [JsonIgnore] public bool HasScope => ScopePattern.Split(';').Any(p => p.Trim().Length > 0);

    private string? _runtimeError;

    /// <summary>The last problem this rule hit while previewing (script error, bad pattern...), or null.</summary>
    [JsonIgnore]
    public string? RuntimeError
    {
        get => _runtimeError;
        set
        {
            if (_runtimeError == value) return;
            _runtimeError = value;
            OnPropertyChanged();
        }
    }

    public NameParts Apply(NameParts name, RuleContext ctx)
    {
        if (!IsEnabled || !InScope(ctx)) return name;
        return ApplyCore(name, ctx);
    }

    protected virtual NameParts ApplyCore(NameParts name, RuleContext ctx)
    {
        var target = SupportsTarget ? Target : RuleTarget.Name;
        switch (target)
        {
            case RuleTarget.Extension:
            {
                // Work on the extension without its dot, then put the dot back.
                var ext = name.Extension.TrimStart('.');
                var result = Transform(ext, ctx);
                return name with { Extension = result.Length == 0 ? string.Empty : "." + result.TrimStart('.') };
            }
            case RuleTarget.FullName:
                return NameParts.FromFull(Transform(name.Full, ctx), ctx.File.IsFolder);
            default:
                return name with { Base = Transform(name.Base, ctx) };
        }
    }

    protected abstract string Transform(string input, RuleContext ctx);

    public bool InScope(RuleContext ctx)
    {
        // Blank, or separators only (";"), means no filter at all.
        if (!HasScope) return true;
        bool match;
        var subject = ctx.File.FileName;
        if (ScopeIsRegex)
        {
            try
            {
                if (_scopeRegex == null || _scopeRegexSource != ScopePattern)
                {
                    _scopeRegex = new Regex(ScopePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                    _scopeRegexSource = ScopePattern;
                }
                match = _scopeRegex.IsMatch(subject);
            }
            catch (RegexMatchTimeoutException)
            {
                RuntimeError = "The \u201COnly apply to\u201D pattern is too slow on some names, so this rule is skipped for them. Try a simpler pattern.";
                match = false;
            }
            catch (Exception)
            {
                RuntimeError = "The \u201COnly apply to\u201D pattern is not a valid regular expression, so this rule is skipped.";
                match = false;
            }
        }
        else
        {
            match = Wildcard.IsMatchAny(subject, ScopePattern);
        }
        return ScopeInvert ? !match : match;
    }

    private IReadOnlyList<RuleContext?>? _scopedFor;
    private Dictionary<RuleContext, (int Index, int FolderIndex)>? _scoped;

    /// <summary>
    /// The file's position among the files this rule applies to. Without an "Only apply to" filter
    /// that is simply its position in the list; with one, files the rule skips are not counted,
    /// so numbering has no gaps.
    /// </summary>
    protected (int Index, int FolderIndex) ScopedPosition(RuleContext ctx)
    {
        if (!HasScope || ctx.All == null) return (ctx.Index, ctx.FolderIndex);
        if (!ReferenceEquals(_scopedFor, ctx.All) || _scoped == null)
        {
            var map = new Dictionary<RuleContext, (int, int)>();
            var perFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var c in ctx.All)
            {
                if (c == null || !InScope(c)) continue;
                perFolder.TryGetValue(c.File.Directory, out var fi);
                perFolder[c.File.Directory] = fi + 1;
                map[c] = (n++, fi);
            }
            _scoped = map;
            _scopedFor = ctx.All;
        }
        return _scoped.TryGetValue(ctx, out var pos) ? pos : (ctx.Index, ctx.FolderIndex);
    }

    /// <summary>Creates an independent copy with the same settings.</summary>
    public RenameRule Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, typeof(RenameRule), RuleJson.Options);
        return (RenameRule)System.Text.Json.JsonSerializer.Deserialize(json, typeof(RenameRule), RuleJson.Options)!;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(Summary) && e.PropertyName != nameof(HasScope) && e.PropertyName != nameof(RuntimeError))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Summary)));
            if (e.PropertyName == nameof(ScopePattern)) base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasScope)));
        }
    }

    protected string TargetSuffix => SupportsTarget && Target != RuleTarget.Name
        ? Target == RuleTarget.Extension ? " (extension)" : " (full name)"
        : string.Empty;

    protected static string Quote(string s) => s.Length == 0 ? "nothing" : $"“{s}”";
}

public static class RuleJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Simple * and ? wildcards; several patterns can be separated with ';'.</summary>
public static class Wildcard
{
    public static bool IsMatchAny(string text, string patterns)
    {
        foreach (var raw in patterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (IsMatch(text, raw)) return true;
        return false;
    }

    public static bool IsMatch(string text, string pattern)
    {
        int t = 0, p = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                t++; p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++; starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1; t = ++starT;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
