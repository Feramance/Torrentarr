using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class SonarrSearchExecutionTests : IDisposable
{
    private readonly TorrentarrDbContext _db = new(new DbContextOptionsBuilder<TorrentarrDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly TorrentarrConfig _config = new();
    private readonly RecordingExecutor _executor;
    private readonly ArrInstanceConfig _arr = new() { Type = "sonarr", Category = "tv" };

    public SonarrSearchExecutionTests()
    {
        _config.Settings.SearchLoopDelay = 1;
        _config.ArrInstances["Sonarr-test"] = _arr;
        var coordinator = new DatabaseRestartCoordinator();
        _executor = new RecordingExecutor(_config, _db,
            new QualityProfileSwitcherService(NullLogger<QualityProfileSwitcherService>.Instance, _db, coordinator), coordinator);
    }

    private async Task<List<SearchCandidate>> SeedAsync(List<EpisodeFilesModel> episodes)
    {
        _db.Episodes.AddRange(episodes);
        _db.Series.Add(new SeriesFilesModel { ArrId = 7, ArrInstance = "Sonarr-test", Monitored = true });
        await _db.SaveChangesAsync();
        return SonarrSearchScope.Candidates(episodes, _arr.Search, DateTime.UtcNow);
    }

    [Fact]
    public async Task SuccessfulBroadSearch_MarksAllYearsAndReasons_AndPreventsUpgradePassDuplicates()
    {
        var episodes = SonarrSearchScopeTests.Episodes(5, 1);
        episodes[1].QualityMet = false;
        episodes[2].CustomFormatMet = false;
        _arr.Search.QualityUnmetSearch = true;
        _arr.Search.CustomFormatUnmetSearch = true;
        var needs = await SeedAsync(episodes);
        var otherInstance = SonarrSearchScopeTests.Episodes(1, 1).Single();
        otherInstance.ArrInstance = "Sonarr-other";
        _db.Episodes.Add(otherInstance);
        await _db.SaveChangesAsync();

        var result = await _executor.ExecuteSearchesAsync("Sonarr-test", needs.Take(1));
        result.SearchesTriggered.Should().Be(1);
        result.ItemsSearched.Should().Be(3);
        result.SearchedIds.Should().Equal(1, 2, 3);
        result.LoopCompleted.Should().BeTrue();
        episodes.Take(3).Should().OnlyContain(e => e.Searched && e.Upgrade);
        episodes.Skip(3).Should().OnlyContain(e => !e.Searched && !e.Upgrade);
        otherInstance.Searched.Should().BeFalse();

        var coordinator = new DatabaseRestartCoordinator();
        var media = new ArrMediaService(NullLogger<ArrMediaService>.Instance, _db, _config, _executor,
            new ArrSyncService(NullLogger<ArrSyncService>.Instance, _config, _db, coordinator));
        (await media.SearchQualityUpgradesAsync("tv")).SearchesTriggered.Should().Be(0);
        (await _executor.ExecuteSearchesAsync("Sonarr-test", needs)).SearchesTriggered.Should().Be(0);
        _executor.Plans.Should().ContainSingle(p => p.Command == "SeriesSearch");
    }

    [Fact]
    public async Task YearFilter_RequiresCurrentPassCandidate_ButMajorityCountsOtherYears()
    {
        var episodes = SonarrSearchScopeTests.Episodes(3, 2);
        episodes[0].AirDateUtc = new DateTime(2021, 1, 1);
        episodes[1].AirDateUtc = new DateTime(2022, 1, 1);
        episodes[2].AirDateUtc = new DateTime(2020, 1, 1);
        await SeedAsync(episodes);
        var coordinator = new DatabaseRestartCoordinator();
        var cursor = new SearchYearCursor();
        var media = new ArrMediaService(NullLogger<ArrMediaService>.Instance, _db, _config, _executor,
            new ArrSyncService(NullLogger<ArrSyncService>.Instance, _config, _db, coordinator), cursor);
        (await media.SearchMissingMediaAsync("tv")).SearchesTriggered.Should().Be(0);
        _executor.Plans.Should().BeEmpty();
        cursor.Advance("Sonarr-test").Should().BeTrue();
        var result = await media.SearchMissingMediaAsync("tv");
        result.SearchesTriggered.Should().Be(1);
        result.SearchedIds.Should().BeEquivalentTo(new[] { 1, 2 });
        _executor.Plans.Should().ContainSingle(p => p.Command == "SeriesSearch" && p.Trigger.ArrId == 1);
    }

    [Fact]
    public async Task FailedSubmission_LeavesCandidatesAvailableForNextPass()
    {
        var episodes = SonarrSearchScopeTests.Episodes(3, 3);
        var needs = await SeedAsync(episodes);
        _executor.Succeed = false;
        var failed = await _executor.ExecuteSearchesAsync("Sonarr-test", needs);
        failed.SearchesTriggered.Should().Be(0);
        failed.SearchedIds.Should().BeEmpty();
        failed.LoopCompleted.Should().BeFalse();
        episodes.Should().OnlyContain(e => !e.Searched && !e.Upgrade);
        _executor.Succeed = true;
        (await _executor.ExecuteSearchesAsync("Sonarr-test", needs)).SearchesTriggered.Should().Be(1);
        episodes.Should().OnlyContain(e => e.Searched && e.Upgrade);
    }

    [Fact]
    public async Task CommandLimit_MarksOnlySuccessfulScope_AndLeavesRemainderQueued()
    {
        var episodes = SonarrSearchScopeTests.Episodes(5, 3)
            .Concat(SonarrSearchScopeTests.Episodes(10, 1, 2, 10)).ToList();
        var needs = await SeedAsync(episodes);
        _arr.Search.SearchLimit = 1;
        _executor.BlockAfterFirst = true;
        var result = await _executor.ExecuteSearchesAsync("Sonarr-test", needs);
        result.SearchesTriggered.Should().Be(1);
        result.LoopCompleted.Should().BeFalse();
        _executor.Plans.Should().ContainSingle(p => p.Command == "SeasonSearch");
        result.SearchedIds.Should().BeEquivalentTo(new[] { 1, 2, 3 });
        episodes.Single(e => e.ArrId == 10).Searched.Should().BeFalse();
    }

    [Theory]
    [InlineData("smart")]
    [InlineData("series")]
    public async Task SeriesSearch_CannotImplicitlySearchDisallowedSpecials(string mode)
    {
        _arr.Search.SearchBySeries = mode;
        var episodes = SonarrSearchScopeTests.Episodes(3, 3);
        var special = SonarrSearchScopeTests.Episodes(1, 1, season: 0, firstId: 10).Single();
        special.Monitored = false;
        episodes.Add(special);
        _arr.Search.AlsoSearchSpecials = true;
        var needs = await SeedAsync(episodes);
        _executor.SpecialsSeasonMonitored = true;
        var result = await _executor.ExecuteSearchesAsync("Sonarr-test", needs);
        _executor.Plans.Should().OnlyContain(p => p.Command == "SeasonSearch" && p.Trigger.SeasonNumber != 0);
        result.SearchedIds.Should().BeEquivalentTo(new[] { 1, 2, 3 });
        special.Searched.Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_DoesNotSubmitOrMarkSearches()
    {
        var episodes = SonarrSearchScopeTests.Episodes(3, 3);
        var needs = await SeedAsync(episodes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var run = () => _executor.ExecuteSearchesAsync("Sonarr-test", needs, cancellation.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        _executor.Plans.Should().BeEmpty();
        episodes.Should().OnlyContain(e => !e.Searched);
    }

    public void Dispose() => _db.Dispose();

    private sealed class RecordingExecutor(TorrentarrConfig config, TorrentarrDbContext db,
        QualityProfileSwitcherService switcher, DatabaseRestartCoordinator coordinator)
        : SearchExecutor(NullLogger<SearchExecutor>.Instance, config, db, switcher, coordinator)
    {
        internal List<SonarrSearchPlan> Plans { get; } = [];
        internal bool Succeed { get; set; } = true;
        internal bool BlockAfterFirst { get; set; }
        public override Task<int> GetActiveCommandCountAsync(string instanceName, CancellationToken cancellationToken = default)
            => Task.FromResult(BlockAfterFirst && Plans.Count > 0 ? 1 : 0);
        internal bool SpecialsSeasonMonitored { get; set; }
        internal override Task<List<SonarrSeries>> GetSonarrSeriesAsync(string instanceName, ArrInstanceConfig config, CancellationToken ct) =>
            Task.FromResult(new List<SonarrSeries> { new()
            {
                Id = 7,
                Seasons = [new() { SeasonNumber = 0, Monitored = SpecialsSeasonMonitored },
                    new() { SeasonNumber = 1, Monitored = true }, new() { SeasonNumber = 2, Monitored = true }]
            }});
        internal override Task<bool> TriggerSonarrSearchAsync(string instanceName, ArrInstanceConfig config,
            SonarrSearchPlan plan, CancellationToken ct)
        {
            Plans.Add(plan);
            return Task.FromResult(Succeed);
        }
    }
}
