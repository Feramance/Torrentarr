using FluentAssertions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class SonarrSearchScopeTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
    private readonly SearchConfig _config = new();
    private readonly List<SeriesFilesModel> _series = [new() { ArrId = 7, Monitored = true }];

    internal static List<EpisodeFilesModel> Episodes(int total, int missing, int season = 1, int firstId = 1) =>
        Enumerable.Range(firstId, total).Select((id, index) => new EpisodeFilesModel
        {
            ArrId = id,
            ArrInstance = "Sonarr-test",
            ArrSeriesId = 7,
            SeasonNumber = season,
            EpisodeNumber = index + 1,
            Monitored = true,
            HasFile = index >= missing,
            QualityMet = true,
            CustomFormatMet = true,
            AirDateUtc = Now.AddYears(-index - 1)
        }).ToList();

    private List<SearchCandidate> Needs(List<EpisodeFilesModel> episodes) => SonarrSearchScope.Candidates(episodes, _config, Now);
    private List<SonarrSearchPlan> Plan(List<EpisodeFilesModel> episodes, List<SearchCandidate>? requested = null) =>
        SonarrSearchScope.Plan(requested ?? Needs(episodes), episodes, _series, _config, Now);

    [Theory]
    [InlineData(5, 2, "EpisodeSearch", 2)]
    [InlineData(4, 2, "EpisodeSearch", 2)]
    [InlineData(5, 3, "SeriesSearch", 1)]
    [InlineData(1, 1, "EpisodeSearch", 1)]
    public void StrictMajority_RequiresAtLeastTwo(int total, int missing, string command, int count)
    {
        var plans = Plan(Episodes(total, missing));
        plans.Should().HaveCount(count).And.OnlyContain(p => p.Command == command);
        plans.SelectMany(p => p.Covered).Should().HaveCount(missing);
    }

    [Fact]
    public void SeasonMajority_DoesNotSearchOtherSeasons_OrDuplicateCoveredEpisodes()
    {
        var episodes = Episodes(5, 3).Concat(Episodes(10, 1, 2, 10)).ToList();
        var plans = Plan(episodes);
        plans.Select(p => p.Command).Should().Equal("SeasonSearch", "EpisodeSearch");
        plans[0].Covered.Select(c => c.ArrId).Should().Equal(1, 2, 3);
        plans[0].EligibleCount.Should().Be(3);
        plans[0].TotalCount.Should().Be(5);
    }

    [Fact]
    public void MixedNeeds_UseConfiguration_AndCountAcrossAllYears()
    {
        var episodes = Episodes(5, 1);
        episodes[1].QualityMet = false;
        episodes[2].CustomFormatMet = false;
        _config.QualityUnmetSearch = true;
        _config.CustomFormatUnmetSearch = true;
        var currentPass = Needs(episodes).Take(1).ToList();
        var plan = Plan(episodes, currentPass).Single();
        plan.Command.Should().Be("SeriesSearch");
        plan.Covered.Select(c => c.Reason).Should().Equal("Missing", "Quality", "CustomFormat");
        _config.CustomFormatUnmetSearch = false;
        Plan(episodes, currentPass).Single().Command.Should().Be("EpisodeSearch");
        _config.SearchMissing = false;
        Needs(episodes).Should().ContainSingle(c => c.Reason == "Quality");
    }

    [Fact]
    public void UpgradeMode_IsExclusive_AndHonorsCompletionFlags()
    {
        var episodes = Episodes(5, 2);
        _config.DoUpgradeSearch = true;
        Needs(episodes).Should().HaveCount(3).And.OnlyContain(c => c.Reason == "Upgrade");
        episodes[2].Upgrade = true;
        Needs(episodes).Should().HaveCount(2);
        Plan(episodes).Should().OnlyContain(p => p.Command == "EpisodeSearch");
        _config.DoUpgradeSearch = false;
        episodes[0].Searched = true;
        Needs(episodes).Should().ContainSingle(c => c.ArrId == 2);
    }

    [Fact]
    public void MonitoringSpecialsAndAirdates_ControlThePopulation()
    {
        var episodes = Episodes(7, 3);
        episodes[3].Monitored = false;
        episodes[4].SeasonNumber = 0;
        episodes[5].AirDateUtc = Now.AddDays(1);
        episodes[6].AirDateUtc = null;
        var plan = Plan(episodes).Single();
        plan.Command.Should().Be("SeriesSearch");
        plan.TotalCount.Should().Be(3);
        _config.AlsoSearchSpecials = true;
        Plan(episodes).Single().TotalCount.Should().Be(4);
        _series[0].Monitored = false;
        Plan(episodes).Should().OnlyContain(p => p.Command == "EpisodeSearch");
    }

    [Fact]
    public void UnmonitoredNeeds_AreSearchedIndividually()
    {
        var episodes = Episodes(4, 4);
        _config.Unmonitored = true;
        episodes[0].Monitored = false;
        var plans = Plan(episodes);
        plans[0].Command.Should().Be("EpisodeSearch");
        plans[1].Command.Should().Be("SeriesSearch");
        plans[1].Covered.Should().HaveCount(3).And.NotContain(c => c.ArrId == 1);
    }

    [Fact]
    public void MissingMetadataAndUnknownAirdates_FallBackToEpisodes()
    {
        var episodes = Episodes(2, 2);
        var requested = Needs(episodes);
        _series.Clear();
        Plan(episodes, requested).Should().OnlyContain(p => p.Command == "EpisodeSearch");
        _series.Add(new SeriesFilesModel { ArrId = 7, Monitored = true });
        episodes[0].AirDateUtc = null;
        requested[0].Reason = "Retry";
        Plan(episodes, requested).Should().OnlyContain(p => p.Command == "EpisodeSearch");
        episodes[1].ArrSeriesId = 0;
        requested[1].SeriesId = null;
        Plan(episodes, requested).Should().OnlyContain(p => p.Command == "EpisodeSearch");
    }

    [Fact]
    public void RetryNeeds_OverrideCompletion_AndDeduplicateWithOutstandingNeeds()
    {
        var episodes = Episodes(5, 2);
        episodes[2].Searched = true;
        var retries = new List<SearchCandidate>
        {
            new() { ArrId = 2, Reason = "Retry" }, new() { ArrId = 3, Reason = "Retry" },
            new() { ArrId = 3, Reason = "Retry" }
        };
        var plan = Plan(episodes, retries).Single();
        plan.Command.Should().Be("SeriesSearch");
        plan.Covered.Select(c => c.ArrId).Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData("true", "SeriesSearch", 1)]
    [InlineData("TRUE", "SeriesSearch", 1)]
    [InlineData("false", "EpisodeSearch", 3)]
    public void ExplicitModes_PreserveRouting(string mode, string command, int count)
    {
        _config.SearchBySeries = mode;
        Plan(Episodes(3, 3)).Should().HaveCount(count).And.OnlyContain(p => p.Command == command);
    }

    [Fact]
    public void ExplicitSeriesSearch_CoversOutstandingNeedsOutsideCurrentPass()
    {
        _config.SearchBySeries = "true";
        var episodes = Episodes(5, 3);
        var plan = Plan(episodes, Needs(episodes).Take(1).ToList()).Single();
        plan.Command.Should().Be("SeriesSearch");
        plan.Covered.Select(c => c.ArrId).Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData("smart")]
    [InlineData("series")]
    [InlineData("season")]
    [InlineData("episode")]
    public void Specials_RequireOptInAndAtLeastOneMonitoredSpecialPerSeries(string mode)
    {
        _config.SearchBySeries = mode;
        _config.Unmonitored = true;
        var episodes = Episodes(2, 2, season: 0);
        foreach (var episode in episodes) episode.Monitored = false;
        _config.AlsoSearchSpecials = true;
        Needs(episodes).Should().BeEmpty();
        Plan(episodes, [new SearchCandidate { ArrId = 1, SeriesId = 7, SeasonNumber = 0, Reason = "Retry" }]).Should().BeEmpty();
        episodes[0].Monitored = true;
        Needs(episodes).Should().HaveCount(2);
        Plan(episodes).SelectMany(p => p.Covered).Select(c => c.ArrId).Should().BeEquivalentTo(new[] { 1, 2 });
        _config.Unmonitored = false;
        Needs(episodes).Should().ContainSingle(c => c.ArrId == 1);
        _config.AlsoSearchSpecials = false;
        Needs(episodes).Should().BeEmpty();
    }

    [Fact]
    public void SpecialsPermission_IsPerSeries_AndDoesNotRequireAnAiredSpecial()
    {
        _config.AlsoSearchSpecials = true;
        _config.Unmonitored = true;
        var episodes = Episodes(2, 2, season: 0);
        episodes[0].AirDateUtc = Now.AddDays(1);
        episodes[1].Monitored = false;
        var other = Episodes(1, 1, season: 0, firstId: 10).Single();
        other.ArrSeriesId = 8;
        other.Monitored = false;
        episodes.Add(other);
        Needs(episodes).Should().ContainSingle(c => c.ArrId == 2);
    }

    [Fact]
    public void ForcedSeason_DeduplicatesBySeriesAndSeasonWithoutMajority()
    {
        _config.SearchBySeries = "season";
        var episodes = Episodes(20, 1).Concat(Episodes(20, 2, season: 2, firstId: 30)).ToList();
        var plans = Plan(episodes);
        plans.Select(p => p.Command).Should().Equal("SeasonSearch", "SeasonSearch");
        plans.Select(p => p.Trigger.SeasonNumber).Should().Equal(1, 2);
        plans.SelectMany(p => p.Covered).Should().HaveCount(3);
    }

    [Theory]
    [InlineData("smart")]
    [InlineData("series")]
    [InlineData("season")]
    public void FutureAndUnknownAirdates_DoNotPromoteOrDuplicateBroadCommands(string mode)
    {
        _config.SearchBySeries = mode;
        _config.QualityUnmetSearch = true;
        var episodes = Episodes(3, 0);
        episodes[0].AirDateUtc = Now.AddDays(1);
        episodes[1].AirDateUtc = null;
        episodes[0].QualityMet = episodes[1].QualityMet = false;
        Plan(episodes).Should().HaveCount(2).And.OnlyContain(p => p.Command == "EpisodeSearch");
    }

    [Fact]
    public void CompletedCandidates_CannotTriggerAnotherBroadSearch()
    {
        var episodes = Episodes(3, 3);
        var requested = Needs(episodes);
        foreach (var episode in episodes) episode.Searched = true;
        Plan(episodes, requested).Should().BeEmpty();
        Plan(episodes, []).Should().BeEmpty();
    }
}
