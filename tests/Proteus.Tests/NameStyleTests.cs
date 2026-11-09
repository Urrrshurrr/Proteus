using Proteus.Core.Engine;

namespace Proteus.Tests;

public class NameStyleTests
{
    private static readonly string[] LibrarySamples =
    [
        "Silicon Valley - 01x01 - Minimum Viable Product",
        "Silicon Valley - 02x10 - Two Days of the Condor",
        "Silicon Valley - 03x03 - Meinertzhagen's Haversack",
    ];

    [Fact]
    public void Parses_a_scene_name()
    {
        var p = NameStyle.Parse("Silicon.Valley.2014.S01E08.Optimal.Tip-to-Tip.Efficiency.1080p.BluRay.x264-GRP");
        Assert.Equal("Silicon Valley", p.Show);
        Assert.Equal(2014, p.Year);
        Assert.Equal(new EpisodeKey(1, 8, 8), p.Episode);
        Assert.Equal("Optimal Tip-to-Tip Efficiency", p.Title);
        Assert.Equal("1080p", p.Quality);
    }

    [Fact]
    public void Learns_the_library_style()
    {
        var style = NameStyle.Learn(LibrarySamples)!;
        Assert.Equal("<show> - <season:00>x<episode:00> - <title>", style.Template);
        Assert.Equal(" ", style.WordSeparator);
        Assert.Equal(WordCase.Title, style.Case);
        Assert.True(style.SmallWordsLower);
        Assert.Equal(3, style.Agreeing);
    }

    [Theory]
    [InlineData("Silicon.Valley.S01E08.Optimal.Tip-to-Tip.Efficiency.1080p.BluRay.x264-GRP", "Silicon Valley - 01x08 - Optimal Tip-to-Tip Efficiency")]
    [InlineData("silicon.valley.s02e10.two.days.of.the.condor.720p.hdtv", "Silicon Valley - 02x10 - Two Days of the Condor")]
    [InlineData("Silicon Valley S06E06 RussFest 2160p WEB-DL", "Silicon Valley - 06x06 - RussFest")]
    public void Applies_the_library_style(string source, string expected)
    {
        var style = NameStyle.Learn(LibrarySamples)!;
        var result = NameStyle.Apply(style.Template, NameStyle.Parse(source), style.WordSeparator, style.Case, style.SmallWordsLower);
        Assert.Equal(expected, result.Name);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void Missing_title_drops_its_separator_and_is_reported()
    {
        var style = NameStyle.Learn(LibrarySamples)!;
        var result = NameStyle.Apply(style.Template, NameStyle.Parse("Silicon.Valley.S02E01.2160p.WEB-DL.x265"), style.WordSeparator, style.Case);
        Assert.Equal("Silicon Valley - 02x01", result.Name);
        Assert.Equal(["episode title"], result.Missing);
    }

    [Fact]
    public void Jellyfin_style_with_year_and_SxxEyy()
    {
        var style = NameStyle.Learn(["Severance (2022) - S01E01 - Good News About Hell", "Severance (2022) - S01E02 - Half Loop"])!;
        Assert.Equal("<show> (<year>) - S<season:00>E<episode:00> - <title>", style.Template);

        var withYear = NameStyle.Apply(style.Template, NameStyle.Parse("The.Bear.2022.S02E06.Fishes.1080p"), style.WordSeparator, style.Case);
        Assert.Equal("The Bear (2022) - S02E06 - Fishes", withYear.Name);

        // No year in the file: the brackets go too, unless a year is typed in.
        var noYear = NameStyle.Apply(style.Template, NameStyle.Parse("The.Bear.S02E06.Fishes.1080p"), style.WordSeparator, style.Case);
        Assert.Equal("The Bear - S02E06 - Fishes", noYear.Name);
        Assert.Equal(["year"], noYear.Missing);
        var typed = NameStyle.Apply(style.Template, NameStyle.Parse("The.Bear.S02E06.Fishes.1080p"), style.WordSeparator, style.Case, yearOverride: "2022");
        Assert.Equal("The Bear (2022) - S02E06 - Fishes", typed.Name);
    }

    [Fact]
    public void Dotted_lowercase_style_and_double_episodes()
    {
        var style = NameStyle.Learn(["the.office.s01e01.pilot", "the.office.s02e05.halloween"])!;
        Assert.Equal("<show>.s<season:00>e<episode:00>.<title>", style.Template);
        Assert.Equal(".", style.WordSeparator);
        Assert.Equal(WordCase.Lower, style.Case);
        var r = NameStyle.Apply(style.Template, NameStyle.Parse("Parks and Recreation - S03E01-E02 - Go Big or Go Home"), style.WordSeparator, style.Case);
        Assert.Equal("parks.and.recreation.s03e01-e02.go.big.or.go.home", r.Name);
    }

    [Fact]
    public void Movie_style()
    {
        var style = NameStyle.Learn(["Heat (1995)", "The Matrix (1999)"])!;
        Assert.Equal("<show> (<year>)", style.Template);
        var r = NameStyle.Apply(style.Template, NameStyle.Parse("Mad.Max.Fury.Road.2015.2160p.UHD.BluRay.x265-GRP"), style.WordSeparator, style.Case);
        Assert.Equal("Mad Max Fury Road (2015)", r.Name);
    }

    [Fact]
    public void Window_model_learns_renders_and_respects_edits()
    {
        using var dir = new TempDir();
        var a = new Proteus.Core.Models.FileEntry(dir.File("Silicon.Valley.S01E08.Optimal.Tip-to-Tip.Efficiency.1080p.BluRay.x264-GRP.mkv"));
        var b = new Proteus.Core.Models.FileEntry(dir.File("Silicon.Valley.S02E01.2160p.WEB-DL.x265.mp4"));
        var c = new Proteus.Core.Models.FileEntry(dir.File("behind the scenes.mkv"));
        var vm = new Proteus.App.ViewModels.MatchStyleViewModel([a, b, c], null, hasOtherRules: false);

        vm.AppendSamples(["Silicon Valley - 01x01 - Minimum Viable Product.mkv", "Silicon Valley - 02x10 - Two Days of the Condor"]);
        Assert.Equal("<show> - <season:00>x<episode:00> - <title>", vm.Template);

        Assert.Equal("Silicon Valley - 01x08 - Optimal Tip-to-Tip Efficiency", vm.Rows[0].NewBaseName);
        Assert.Equal("Ready", vm.Rows[0].Status);
        Assert.Equal("Silicon Valley - 02x01", vm.Rows[1].NewBaseName);
        Assert.Equal("No episode title", vm.Rows[1].Status);
        Assert.Equal("behind the scenes", vm.Rows[2].NewBaseName); // no episode number: left alone
        Assert.Equal("No episode number", vm.Rows[2].Status);
        Assert.Equal(2, vm.ApplyCount);

        // A typed name survives style changes; reset brings the styled one back.
        vm.Rows[1].NewBaseName = "Silicon Valley - 02x01 - Sand Hill Shuffle";
        Assert.Equal("Edited", vm.Rows[1].Status);
        vm.Separator = Proteus.App.ViewModels.MatchStyleViewModel.Separators[1];
        Assert.Equal("Silicon Valley - 02x01 - Sand Hill Shuffle", vm.Rows[1].NewBaseName);
        Assert.Equal("Silicon.Valley - 01x08 - Optimal.Tip-to-Tip.Efficiency", vm.Rows[0].NewBaseName);
        vm.Rows[1].ResetCommand.Execute(null);
        Assert.Equal("Silicon.Valley - 02x01", vm.Rows[1].NewBaseName);

        var pairs = vm.ChosenPairs();
        Assert.Equal(2, pairs.Count);
        Assert.Equal(a.FullPath, pairs[0].Path);
    }

    [Fact]
    public void Show_name_can_be_typed_in()
    {
        var r = NameStyle.Apply("<show> - <season:00>x<episode:00>", NameStyle.Parse("SV.S01E01.720p"), showOverride: "Silicon Valley");
        Assert.Equal("Silicon Valley - 01x01", r.Name);
    }
}
