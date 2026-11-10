using Proteus.App.ViewModels;
using Proteus.Core.Engine;
using Proteus.Core.History;
using Proteus.Core.Models;
using Proteus.Core.Presets;
using Proteus.Core.Rules;

namespace Proteus.Tests;

/// <summary>Smaller fixes: destinations, dot-files, CSV export, presets and folder scans.</summary>
public class AppBehaviourTests
{
    private static MainViewModel NewVm(TempDir hist, TempDir presets)
    {
        var vm = new MainViewModel(new UndoJournal(hist.Path), new PresetStore(presets.Path));
        vm.Rules.Clear();
        return vm;
    }

    [Fact]
    public void a_drive_relative_destination_is_refused()
    {
        var (_, problem) = RenamePipeline.ResolveDestination("C:Sorted", Path.GetTempPath());
        Assert.NotNull(problem);
    }

    [Fact]
    public void a_destination_starting_with_a_backslash_is_on_the_files_own_drive()
    {
        using var d = new TempDir();
        var (dir, problem) = RenamePipeline.ResolveDestination(Path.DirectorySeparatorChar + "Sorted", d.Path);
        Assert.Null(problem);
        Assert.Equal(Path.Combine(Path.GetPathRoot(d.Path)!, "Sorted"), dir);
    }

    [Fact]
    public void dot_files_are_all_name()
    {
        using var d = new TempDir();
        var e = new FileEntry(d.File(".gitignore"));
        Assert.Equal(".gitignore", e.BaseName);
        Assert.Equal(string.Empty, e.Extension);
        Assert.Equal("X.gitignore", RenamePipeline.Run(e, [new InsertRule { Text = "X" }], new RuleContext(e, 0, 0, 1)));
        var normal = new FileEntry(d.File("a.txt"));
        Assert.Equal(".txt", normal.Extension);
    }

    [Fact]
    public void csv_export_does_not_start_cells_with_formulas()
    {
        var text = Csv.Write([["=SUM(A1)", "+ReadOnly", "-x", "@cmd", "C:\\a.txt"]]);
        Assert.Equal("'=SUM(A1),'+ReadOnly,'-x,'@cmd,C:\\a.txt\r\n", text);
    }

    [Fact]
    public void saving_over_a_preset_needs_a_second_click()
    {
        using var hist = new TempDir();
        using var presets = new TempDir();
        var vm = NewVm(hist, presets);
        var store = new PresetStore(presets.Path);
        vm.Rules.Add(new InsertRule { Text = "first" });
        vm.NewPresetName = "Mine";
        vm.SavePresetCommand.Execute(null);
        Assert.Equal("first", Assert.IsType<InsertRule>(store.Load("Mine")!.Rules.Single()).Text);

        vm.Rules.Clear();
        vm.Rules.Add(new InsertRule { Text = "second" });
        vm.NewPresetName = "Mine";
        vm.SavePresetCommand.Execute(null);
        Assert.Equal("first", Assert.IsType<InsertRule>(store.Load("Mine")!.Rules.Single()).Text);
        Assert.Contains("already exists", vm.Notice);
        vm.SavePresetCommand.Execute(null);
        Assert.Equal("second", Assert.IsType<InsertRule>(store.Load("Mine")!.Rules.Single()).Text);
    }

    [Fact]
    public void loading_a_preset_that_moves_files_says_so()
    {
        using var hist = new TempDir();
        using var presets = new TempDir();
        using var dst = new TempDir();
        new PresetStore(presets.Path).Save("Sort", [new InsertRule { Text = "x" }], new BatchOptions { Mode = BatchMode.Move, DestinationFolder = dst.Path });
        var vm = NewVm(hist, presets);
        vm.LoadPresetCommand.Execute("Sort");
        Assert.Equal(BatchMode.Move, vm.Options.Mode);
        Assert.Contains("Move", vm.Notice);
        Assert.Contains(dst.Path, vm.Notice);
    }

    [Fact]
    public async Task adding_a_folder_skips_hidden_files()
    {
        using var hist = new TempDir();
        using var presets = new TempDir();
        using var d = new TempDir();
        d.File("visible.txt");
        var hidden = d.File("hidden.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var vm = NewVm(hist, presets);
        await vm.AddPathsAsync([d.Path], recurse: true, foldersAsItems: false);
        // Linux has no hidden attribute for files without a leading dot; there the check is moot.
        bool attributeStuck = (File.GetAttributes(hidden) & FileAttributes.Hidden) != 0;
        Assert.Equal(attributeStuck ? 1 : 2, vm.VisibleFiles.Count);
        Assert.Contains(vm.VisibleFiles, f => f.OriginalName == "visible.txt");
    }

    [Fact]
    public async Task ticking_every_file_makes_one_preview()
    {
        using var hist = new TempDir();
        using var presets = new TempDir();
        using var d = new TempDir();
        var paths = Enumerable.Range(0, 300).Select(i => d.File($"f{i}.txt")).ToList();
        var vm = NewVm(hist, presets);
        vm.Rules.Add(new InsertRule { Text = "x" });
        await vm.AddPathsAsync(paths, recurse: false, foldersAsItems: false);
        await vm.WaitForPreviewAsync();
        vm.IncludeAllCommand.Execute("none");
        await vm.WaitForPreviewAsync();
        Assert.All(vm.VisibleFiles, f => Assert.False(f.IsIncluded));
        Assert.Equal(0, vm.ChangeCount);
        vm.IncludeAllCommand.Execute("all");
        await vm.WaitForPreviewAsync();
        Assert.Equal(300, vm.ChangeCount);
    }
}
