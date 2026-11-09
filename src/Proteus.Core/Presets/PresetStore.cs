using System.Text.Json;
using Proteus.Core.Rules;

namespace Proteus.Core.Presets;

public sealed class Preset
{
    public string Name { get; set; } = string.Empty;
    public List<RenameRule> Rules { get; set; } = [];
    public Engine.BatchOptions? Options { get; set; }
}

/// <summary>Saves rule stacks as JSON files in %APPDATA%\Proteus\Presets.</summary>
public sealed class PresetStore
{
    private readonly string _folder;

    public PresetStore(string? folder = null)
    {
        _folder = folder ?? AppPaths.Presets;
    }

    public IReadOnlyList<string> Names()
    {
        if (!Directory.Exists(_folder)) return [];
        return Directory.EnumerateFiles(_folder, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void Save(string name, IEnumerable<RenameRule> rules, Engine.BatchOptions? options = null)
    {
        Directory.CreateDirectory(_folder);
        var preset = new Preset { Name = name, Rules = rules.ToList(), Options = options };
        File.WriteAllText(PathFor(name), JsonSerializer.Serialize(preset, RuleJson.Options));
    }

    public Preset? Load(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Preset>(File.ReadAllText(path), RuleJson.Options);
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return Path.Combine(_folder, (safe.Length == 0 ? "preset" : safe) + ".json");
    }
}
