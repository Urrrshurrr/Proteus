using Proteus.App.ViewModels;
using Proteus.Core.Rules;

namespace Proteus.Tests;

public class ViewModelCsvTests
{
    private sealed class FakeUi(string? csvIn, string? csvOut) : IUiServices
    {
        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickCsvAsync() => Task.FromResult(csvIn);
        public Task<string?> SaveCsvAsync(string suggestedName) => Task.FromResult(csvOut);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "timed out");
    }

    [Fact]
    public async Task Import_rename_list_then_export_preview()
    {
        using var dir = new TempDir();
        var a = dir.File("a.txt");
        dir.File("b.txt");
        var csv = Path.Combine(dir.Path, "list.csv");
        File.WriteAllText(csv, $"Current,New\r\n\"{a}\",Alpha.txt\r\nb.txt,Bravo\r\n");
        var export = Path.Combine(dir.Path, "out.csv");

        using var hist = new TempDir();
        using var presets = new TempDir();
        var vm = new MainViewModel(new Proteus.Core.History.UndoJournal(hist.Path), new Proteus.Core.Presets.PresetStore(presets.Path)) { Ui = new FakeUi(csv, export) };
        vm.Rules.Clear();
        await vm.ImportCsvCommand.ExecuteAsync(null);

        // a.txt is a full path; b.txt is relative to the CSV's folder. Both are added.
        Assert.Equal(2, vm.VisibleFiles.Count);
        var rule = Assert.IsType<ListRule>(Assert.Single(vm.Rules));
        Assert.Equal(ListMode.Mapping, rule.Mode);
        await WaitFor(() => vm.VisibleFiles.Any(f => f.NewName == "Alpha.txt") && vm.VisibleFiles.Any(f => f.NewName == "Bravo.txt"));

        await vm.ExportCsvCommand.ExecuteAsync(null);
        var text = File.ReadAllText(export);
        Assert.Contains("Current path,New path,Action,Status", text);
        Assert.Contains("Alpha.txt", text);
        Assert.Contains("Bravo.txt", text);
    }
}
