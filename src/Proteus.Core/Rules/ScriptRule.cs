using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Jint;
using Jint.Native;
using Proteus.Core.Tokens;

namespace Proteus.Core.Rules;

/// <summary>
/// JavaScript rule (like Bulk Rename Utility's JavaScript renaming). The script sees the current
/// name and extension as variables and changes them. It runs in a sandbox: no file, network or
/// .NET access, with time, memory and recursion limits. Each file gets a fresh function scope, so
/// let/const work and nothing leaks from one file to the next.
/// </summary>
public sealed partial class ScriptRule : RenameRule
{
    public const string DefaultScript =
        "// 'name' and 'ext' hold the current name and extension (no dot).\n" +
        "// Change them to rename. Example: replace underscores and add the date\n" +
        "name = name.replace(/_/g, ' ');\n" +
        "name = name + ' ' + token('modified:yyyy-MM-dd');\n";

    private const string Parameters = "name, ext, index, number, folderIndex, count, isFolder, size, original, created, modified, token, pad";

    [ObservableProperty] private string _script = DefaultScript;

    private Jint.Engine? _engine;
    private JsValue? _function;
    private string? _compiledSource;

    public override string Title => "JavaScript";
    public override string Icon => "JS";
    public override bool SupportsTarget => false;
    public override string Summary
    {
        get
        {
            var first = Script.Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal));
            return first == null ? "Rename with a script" : first.Length > 60 ? first[..60] + "…" : first;
        }
    }

    /// <summary>The last problem the script hit (syntax or runtime), or null. Same as <see cref="RenameRule.RuntimeError"/>.</summary>
    [JsonIgnore] public string? Error => RuntimeError;

    partial void OnScriptChanged(string value) => RuntimeError = null;

    protected override NameParts ApplyCore(NameParts name, RuleContext ctx)
    {
        var fn = GetFunction();
        if (fn == null || _engine == null) return name;
        var f = ctx.File;
        try
        {
            var original = new
            {
                name = f.BaseName,
                ext = f.Extension.TrimStart('.'),
                fullName = f.FileName,
                folder = Path.GetFileName(f.Directory),
                path = f.FullPath,
            };
            var result = _engine.Invoke(fn,
                name.Base, name.Extension.TrimStart('.'), ctx.Index, ctx.Index + 1, ctx.FolderIndex, ctx.Count,
                f.IsFolder, (double)f.Size, original, f.Created, f.Modified,
                new Func<string, string>(t => TokenEngine.Expand("<" + t.Trim('<', '>') + ">", ctx)),
                new Func<double, double, string>((n, w) => TokenEngine.Pad((long)n, (int)w)));

            var newBase = AsText(result.AsObject().Get("0"));
            var newExt = AsText(result.AsObject().Get("1")).Trim().TrimStart('.');
            RuntimeError = null;
            if (ctx.File.IsFolder) return new NameParts(newBase + (newExt.Length > 0 ? "." + newExt : ""), string.Empty);
            return new NameParts(newBase, newExt.Length == 0 ? string.Empty : "." + newExt);
        }
        catch (Exception ex)
        {
            RuntimeError = Describe(ex);
            return name;
        }
    }

    protected override string Transform(string input, RuleContext ctx) => input;

    /// <summary>Wraps the user's script in a function once; each file is then a separate call.</summary>
    private JsValue? GetFunction()
    {
        if (_compiledSource == Script) return _function;
        _compiledSource = Script;
        _function = null;
        _engine = new Jint.Engine(o => o
            .Strict(false)
            .LimitRecursion(200)
            .TimeoutInterval(TimeSpan.FromMilliseconds(500))
            .MaxStatements(2_000_000)
            .LimitMemory(64_000_000));
        try
        {
            // The script is the function body; the closing lines hand the variables back.
            var source = $"(function ({Parameters}) {{\n{Script}\n;return [name, ext];\n}})";
            _function = _engine.Evaluate(source);
            RuntimeError = null;
        }
        catch (Exception ex)
        {
            RuntimeError = Describe(ex);
        }
        return _function;
    }

    private static string AsText(JsValue v) =>
        v.IsNull() || v.IsUndefined() ? string.Empty : v.IsString() ? v.AsString() : v.ToString();

    private static string Describe(Exception ex)
    {
        static string Line(int line) => line > 1 ? $" (line {line - 1})" : "";
        return ex switch
        {
            Jint.Runtime.JavaScriptException js => $"Script error: {js.Message}{Line(js.Location.Start.Line)}",
            Acornima.ParseErrorException pe => $"Script error: {pe.Description}{Line(pe.LineNumber)}",
            TimeoutException => "The script took too long (over half a second for one file).",
            Jint.Runtime.StatementsCountOverflowException => "The script ran too many steps; is there an endless loop?",
            Jint.Runtime.MemoryLimitExceededException => "The script used too much memory.",
            Jint.Runtime.RecursionDepthOverflowException => "The script recursed too deeply.",
            _ => ex.Message,
        };
    }
}
