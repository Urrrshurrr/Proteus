using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Jint;
using Acornima.Ast;
using Jint.Native;
using Proteus.Core.Tokens;

namespace Proteus.Core.Rules;

/// <summary>
/// JavaScript rule (like Bulk Rename Utility's JavaScript renaming). The script sees the current
/// name and extension as variables and changes them. It runs in a sandbox: no file, network or
/// .NET access, with time, memory and recursion limits. Each file runs in a fresh engine, so
/// let/const work and nothing (not even an undeclared variable) leaks from one file to the next.
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

    /// <summary>The first problem the script hit (syntax or runtime), or null. Same as <see cref="RenameRule.RuntimeError"/>.</summary>
    [JsonIgnore] public string? Error => RuntimeError;

    private Prepared<Script>? _prepared;
    private string? _compileError;
    private string? _firstError;
    private int _failures;

    partial void OnScriptChanged(string value)
    {
        RuntimeError = null;
        _firstError = null;
        _failures = 0;
    }

    /// <summary>
    /// Runs the script for one file in a brand-new engine, so nothing (globals, changed prototypes)
    /// carries over from one file to the next. A failure is thrown on, so the file is marked
    /// Invalid; the rule keeps the first error and counts the files that failed.
    /// </summary>
    protected override NameParts ApplyCore(NameParts name, RuleContext ctx)
    {
        if (Prepare() is not { } prepared) throw Fail(_compileError!);
        var f = ctx.File;
        try
        {
            var engine = new Jint.Engine(o => o
                .Strict(false)
                .LimitRecursion(200)
                .TimeoutInterval(TimeSpan.FromMilliseconds(500))
                .MaxStatements(2_000_000)
                .LimitMemory(64_000_000));
            var fn = engine.Evaluate(prepared);
            var original = new
            {
                name = f.BaseName,
                ext = f.Extension.TrimStart('.'),
                fullName = f.FileName,
                folder = Path.GetFileName(f.Directory),
                path = f.FullPath,
            };
            var result = engine.Invoke(fn,
                name.Base, name.Extension.TrimStart('.'), ctx.Index, ctx.Index + 1, ctx.FolderIndex, ctx.Count,
                f.IsFolder, (double)f.Size, original, f.Created, f.Modified,
                new Func<string, string>(t => TokenEngine.Expand("<" + t.Trim('<', '>') + ">", ctx)),
                new Func<double, double, string>((n, w) => TokenEngine.Pad((long)n, (int)w)));

            var newBase = AsText(result.AsObject().Get("0"));
            var newExt = AsText(result.AsObject().Get("1")).Trim().TrimStart('.');
            if (ctx.File.IsFolder) return new NameParts(newBase + (newExt.Length > 0 ? "." + newExt : ""), string.Empty);
            return new NameParts(newBase, newExt.Length == 0 ? string.Empty : "." + newExt);
        }
        catch (Exception ex)
        {
            throw Fail(Describe(ex));
        }
    }

    private ScriptFailedException Fail(string message)
    {
        _failures++;
        _firstError ??= message;
        RuntimeError = _failures == 1 ? _firstError : $"{_firstError} ({_failures:N0} files failed; the first problem is shown)";
        return new ScriptFailedException(message);
    }

    protected override string Transform(string input, RuleContext ctx) => input;

    /// <summary>Parses the user's script, wrapped in a function, once per script text.</summary>
    private Prepared<Script>? Prepare()
    {
        if (_compiledSource == Script) return _prepared;
        _compiledSource = Script;
        _prepared = null;
        _compileError = null;
        try
        {
            // The script is the function body; the closing lines hand the variables back.
            var source = $"(function ({Parameters}) {{\n{Script}\n;return [name, ext];\n}})";
            _prepared = Jint.Engine.PrepareScript(source);
        }
        catch (Exception ex)
        {
            _compileError = Describe(ex);
        }
        return _prepared;
    }

    private string? _compiledSource;

    private static string AsText(JsValue v) =>
        v.IsNull() || v.IsUndefined() ? string.Empty : v.IsString() ? v.AsString() : v.ToString();

    private static string Describe(Exception ex)
    {
        static string Line(int line) => line > 1 ? $" (line {line - 1})" : "";
        if (ex.InnerException is Acornima.ParseErrorException inner) ex = inner;
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

/// <summary>The JavaScript rule failed for one file; the message is already in plain words.</summary>
public sealed class ScriptFailedException(string message) : Exception(message);
