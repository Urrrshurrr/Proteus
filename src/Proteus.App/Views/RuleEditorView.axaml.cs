using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Proteus.Core.Rules;

namespace Proteus.App.Views;

public partial class RuleEditorView : UserControl
{
    public RuleEditorView() => InitializeComponent();

    private static readonly Dictionary<string, string> ScriptExamples = new()
    {
        ["title"] = "// Capitalise every word\nname = name.toLowerCase().replace(/(^|[\\s_-])(\\p{L})/gu, (m, sep, ch) => sep + ch.toUpperCase());\n",
        ["date"] = "// Put the modified date in front\nname = token('modified:yyyy-MM-dd') + ' ' + name;\n",
        ["season"] = "// 'Show 1x02 Title' -> 'Show S01E02 Title'\nname = name.replace(/(\\d{1,2})x(\\d{1,3})/i, (m, s, e) => 'S' + pad(s, 2) + 'E' + pad(e, 2));\n",
        ["slug"] = "// lowercase, words joined with dashes, no symbols\nname = name.normalize('NFD').replace(/[\\u0300-\\u036f]/g, '')\n  .toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');\next = ext.toLowerCase();\n",
        ["parts"] = "// 'Artist - Title' -> 'Title - Artist'\nvar parts = name.split(' - ');\nif (parts.length === 2) name = parts[1] + ' - ' + parts[0];\n",
    };

    private void OnScriptExample(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key, DataContext: ScriptRule rule } && ScriptExamples.TryGetValue(key, out var script))
            rule.Script = script;
    }

    private void OnReshuffle(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RandomizeRule r) r.Seed = Random.Shared.Next(1, int.MaxValue);
    }

    private async void OnImportList(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ListRule rule) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import names",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Text or CSV") { Patterns = ["*.txt", "*.csv", "*.tsv"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        try { rule.Lines = await File.ReadAllTextAsync(path); }
        catch (Exception) { /* unreadable file: leave the list as it was */ }
    }
}
