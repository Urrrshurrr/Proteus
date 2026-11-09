using System.Text.Json;
using System.Text.Json.Nodes;
using Proteus.Core;
using Proteus.Core.Engine;
using Proteus.Core.Rules;

namespace Proteus.App.Services;

/// <summary>Small per-user settings file, including the last rule stack so a session can carry on.</summary>
public sealed class AppSettings
{
    public bool Dark { get; set; } = true;
    public bool IncludeSubfolders { get; set; } = true;
    public bool HighlightChanges { get; set; } = true;
    public List<RenameRule> Rules { get; set; } = [];
    public BatchOptions? Options { get; set; }

    /// <summary>
    /// Loads settings. Each part is read on its own, so one damaged rule (or a rule type from a
    /// newer version) costs only that rule, not every setting.
    /// </summary>
    public static AppSettings Load()
    {
        var s = new AppSettings();
        try
        {
            if (!File.Exists(AppPaths.Settings)) return s;
            var root = JsonNode.Parse(File.ReadAllText(AppPaths.Settings)) as JsonObject;
            if (root == null) return s;
            if (root["Dark"] is JsonValue d && d.TryGetValue<bool>(out var dark)) s.Dark = dark;
            if (root["IncludeSubfolders"] is JsonValue i && i.TryGetValue<bool>(out var inc)) s.IncludeSubfolders = inc;
            if (root["HighlightChanges"] is JsonValue h && h.TryGetValue<bool>(out var hl)) s.HighlightChanges = hl;
            if (root["Rules"] is JsonArray rules)
            {
                foreach (var node in rules)
                {
                    try
                    {
                        if (node?.Deserialize<RenameRule>(RuleJson.Options) is { } rule) s.Rules.Add(rule);
                    }
                    catch (Exception ex) { AppLog.Write("Skipped a saved rule that could not be read", ex); }
                }
            }
            try { s.Options = root["Options"]?.Deserialize<BatchOptions>(RuleJson.Options); }
            catch (Exception ex) { AppLog.Write("Saved batch options could not be read", ex); }
        }
        catch (Exception ex)
        {
            AppLog.Write("Settings could not be read; using defaults", ex);
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.Settings)!);
            var temp = AppPaths.Settings + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, RuleJson.Options));
            File.Move(temp, AppPaths.Settings, overwrite: true);
        }
        catch (Exception ex) { AppLog.Write("Settings could not be saved", ex); }
    }
}
