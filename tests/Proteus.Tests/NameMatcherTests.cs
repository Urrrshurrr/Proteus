using Proteus.App.ViewModels;
using Proteus.Core.Engine;
using Proteus.Core.Models;
using Proteus.Core.Rules;

namespace Proteus.Tests;

public class NameMatcherTests
{
    [Theory]
    [InlineData("Silicon Valley (2014) - S01E03 - Articles of Incorporation", 1, 3, 3)]
    [InlineData("Silicon.Valley.S01E03.1080p.BluRay.x265-GRP", 1, 3, 3)]
    [InlineData("silicon valley s1e3 720p", 1, 3, 3)]
    [InlineData("Show.S02.E10.WEB-DL", 2, 10, 10)]
    [InlineData("Show S01E01E02 Pilot", 1, 1, 2)]
    [InlineData("Show - S01E01-E02 - Pilot", 1, 1, 2)]
    [InlineData("Show 3x07 Title", 3, 7, 7)]
    [InlineData("Show Season 2 Episode 4", 2, 4, 4)]
    [InlineData("Show - Episode 12", -1, 12, 12)]
    public void Finds_season_and_episode(string name, int season, int episode, int last)
    {
        var key = NameMatcher.FindEpisode(name);
        Assert.NotNull(key);
        Assert.Equal(new EpisodeKey(season, episode, last), key.Value);
    }

    [Theory]
    [InlineData("Movie 1920x1080 2014")]
    [InlineData("Holiday photos 2019")]
    [InlineData("Show.2160p.x265")]
    public void No_false_episode_markers(string name) => Assert.Null(NameMatcher.FindEpisode(name));

    [Fact]
    public void Pairs_tv_episodes_by_season_and_episode()
    {
        string[] sources =
        [
            "Silicon.Valley.S01E02.The.Cap.Table.2160p.WEB-DL.x265",
            "Silicon.Valley.S01E01.Minimum.Viable.Product.2160p.WEB-DL.x265",
            "Silicon.Valley.S02E01.2160p.WEB-DL.x265",
            "silicon valley s01e03 1080p",
        ];
        string[] references =
        [
            "Silicon Valley (2014) - S01E01 - Minimum Viable Product",
            "Silicon Valley (2014) - S01E02 - The Cap Table",
            "Silicon Valley (2014) - S01E03 - Articles of Incorporation",
            "Silicon Valley (2014) - S02E01 - Sand Hill Shuffle",
            "Silicon Valley (2014) - S02E02 - Runaway Devaluation",
        ];
        var m = NameMatcher.Match(sources, references);
        Assert.Equal(1, m[0].ReferenceIndex);
        Assert.Equal(0, m[1].ReferenceIndex);
        Assert.Equal(3, m[2].ReferenceIndex);
        Assert.Equal(2, m[3].ReferenceIndex);
        Assert.All(m, x => Assert.Equal(MatchKind.Episode, x.Kind));
    }

    [Fact]
    public void Pairs_scene_names_with_NxNN_library_names()
    {
        string[] sources =
        [
            "Silicon.Valley.S03E03.Meinertzhagens.Haversack.1080p.BluRay.x264-GRP",
            "Silicon.Valley.S01E08.Optimal.Tip-to-Tip.Efficiency.1080p.BluRay.x264-GRP",
            "Silicon.Valley.S06E03.Hooli.Smokes.2160p.HMAX.WEB-DL.DDP5.1.HDR.H.265-GRP",
        ];
        string[] references =
        [
            "Silicon Valley - 01x08 - Optimal Tip-to-Tip Efficiency",
            "Silicon Valley - 03x03 - Meinertzhagen's Haversack",
            "Silicon Valley - 03x08 - Bachman's Earnings Over-Ride",
            "Silicon Valley - 06x03 - Hooli Smokes!",
        ];
        var m = NameMatcher.Match(sources, references);
        Assert.Equal([1, 0, 3], m.Select(x => x.ReferenceIndex).ToArray());
        Assert.All(m, x => Assert.Equal(MatchKind.Episode, x.Kind));
        Assert.True(m[0].Score > 0.9);
    }

    [Fact]
    public void Different_episode_numbers_never_pair()
    {
        var m = NameMatcher.Match(["Show S01E05 Title"], ["Show S01E06 Title"]);
        Assert.Equal(-1, m[0].ReferenceIndex);
        Assert.Equal(MatchKind.None, m[0].Kind);
    }

    [Fact]
    public void Pairs_movies_by_similar_title_and_uses_each_name_once()
    {
        string[] sources = ["The.Matrix.1999.2160p.UHD.BluRay.x265", "Heat.1995.1080p.BluRay", "Holiday video"];
        string[] references = ["Heat (1995)", "The Matrix (1999)"];
        var m = NameMatcher.Match(sources, references);
        Assert.Equal(1, m[0].ReferenceIndex);
        Assert.Equal(MatchKind.Similar, m[0].Kind);
        Assert.Equal(0, m[1].ReferenceIndex);
        Assert.Equal(-1, m[2].ReferenceIndex);
    }

    [Fact]
    public void More_files_than_names_leaves_the_extra_unmatched()
    {
        var m = NameMatcher.Match(["Show S01E01 a", "Show S01E01 b"], ["Show - S01E01 - Pilot"]);
        Assert.Single(m, x => x.ReferenceIndex == 0);
        Assert.Single(m, x => x.ReferenceIndex == -1);
    }

    [Fact]
    public async Task Window_model_reads_folder_and_maps_with_own_extension()
    {
        using var lib = new TempDir();
        using var src = new TempDir();
        Directory.CreateDirectory(Path.Combine(lib.Path, "Season 01"));
        File.WriteAllText(Path.Combine(lib.Path, "Season 01", "Silicon Valley (2014) - S01E01 - Minimum Viable Product.mkv"), "");
        File.WriteAllText(Path.Combine(lib.Path, "Season 01", "Silicon Valley (2014) - S01E01 - Minimum Viable Product.nfo"), "");
        File.WriteAllText(Path.Combine(lib.Path, "Season 01", "Silicon Valley (2014) - S01E02 - The Cap Table.mkv"), "");
        File.WriteAllText(Path.Combine(lib.Path, "poster.jpg"), "");
        File.WriteAllText(Path.Combine(lib.Path, "tvshow-trailer.mp4"), "");
        Directory.CreateDirectory(Path.Combine(lib.Path, "Season 01", "extras"));
        File.WriteAllText(Path.Combine(lib.Path, "Season 01", "extras", "Gag reel S01E01.mkv"), "");
        var a = new FileEntry(src.File("Silicon.Valley.S01E02.2160p.x265.mp4"));
        var b = new FileEntry(src.File("Silicon.Valley.S01E01.2160p.x265.mkv"));

        var vm = new MatchFolderViewModel([a, b], null, hasOtherRules: true);
        Assert.Contains("*.mkv", vm.Filter);
        vm.ReferenceFolder = lib.Path;
        await vm.ScanAsync();

        Assert.Equal(3, vm.Options.Count); // "leave as is" + two episodes; .nfo and poster are skipped
        Assert.Equal("Silicon Valley (2014) - S01E02 - The Cap Table.mp4", vm.Rows[0].NewName);
        Assert.Equal("Silicon Valley (2014) - S01E01 - Minimum Viable Product.mkv", vm.Rows[1].NewName);
        Assert.Equal("Episode", vm.Rows[0].Confidence);
        Assert.Equal(2, vm.ApplyCount);

        // Picking the same name twice is flagged; leaving a file as is takes it out.
        vm.Rows[0].Selected = vm.Rows[1].Selected;
        Assert.True(vm.Rows[0].IsDuplicate);
        Assert.Equal("Your pick", vm.Rows[0].Confidence);
        vm.Rows[0].Selected = ReferenceOption.None;
        Assert.False(vm.Rows[0].IsIncluded);
        Assert.False(vm.Rows[1].IsDuplicate);
        Assert.Single(vm.ChosenPairs());
    }

    [Fact]
    public async Task Applying_matches_renames_through_a_mapping_rule()
    {
        using var dir = new TempDir();
        var file = dir.File("Show.S01E01.1080p.mkv");
        using var hist = new TempDir();
        using var presets = new TempDir();
        var vm = new MainViewModel(new Proteus.Core.History.UndoJournal(hist.Path), new Proteus.Core.Presets.PresetStore(presets.Path));
        vm.Rules.Clear();
        vm.Rules.Add(new CaseRule());
        await vm.AddPathsAsync([file], recurse: false, foldersAsItems: false);

        vm.ApplyNameMatches([(file, "Show (2020) - S01E01 - Pilot")], turnOffOtherRules: true);

        Assert.False(vm.Rules[0].IsEnabled);
        Assert.IsType<ListRule>(vm.Rules[1]);
        for (int i = 0; i < 100 && vm.VisibleFiles[0].NewName != "Show (2020) - S01E01 - Pilot.mkv"; i++) await Task.Delay(50);
        Assert.Equal("Show (2020) - S01E01 - Pilot.mkv", vm.VisibleFiles[0].NewName);
    }
}
