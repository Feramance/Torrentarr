using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class SearchLibraryGateTests
{
    [Fact]
    public void CombinedGatesUseOneDeterministicReasonAndExpiry()
    {
        var now = DateTimeOffset.Parse("2026-11-21T23:00:00Z").ToUnixTimeSeconds();
        var arr = new ArrInstanceConfig
        {
            Type = "radarr",
            Search = new()
            {
                QuietHoursEnabled = true,
                ExcludeTags = ["skip"],
                RequireRadarrAvailability = true,
                UnsuccessfulSearchRestMinutes = 60
            }
        };
        var observation = new SearchPacingObservation(null, null,
            new(new Dictionary<int, SearchLibraryEvidence> { [1] = new(["skip"], false), [2] = new([], true) }),
            new(new Dictionary<string, long> { ["2:MOVIE"] = now + 60 }));
        Assert.Contains("Quiet hours until", observation.Hold(1, null, arr, now, "Movie"));
        arr.Search.QuietHoursEnabled = false;
        Assert.Equal("Owning-library tag excluded", observation.Hold(1, null, arr, now, "Movie"));
        Assert.Contains("rest until", observation.Hold(2, null, arr, now, "Movie"));
        Assert.Null(observation.Hold(2, null, arr, now + 60, "Movie"));
    }

    [Fact]
    public void EpisodeInheritsSeriesTagsAndExclusionWins()
    {
        var arr = new ArrInstanceConfig { Type = "sonarr", Search = new() { IncludeTags = ["wanted"], ExcludeTags = ["skip"] } };
        var evidence = SearchLibraryGates.Parse(JArray.Parse("""
            [{"id":10,"tags":[1,2]}, {"id":20,"tags":[1]}, {"id":30,"tags":[]}, {"id":40}]
            """), JArray.Parse("""[{"id":1,"label":"Wanted"},{"id":2,"label":"skip"}]"""), true);
        Assert.Contains("excluded", evidence.Hold(1, 10, arr));
        Assert.Null(evidence.Hold(1, 20, arr));
        Assert.Contains("No included", evidence.Hold(1, 30, arr));
        Assert.Contains("unavailable", evidence.Hold(1, 40, arr));
        Assert.Contains("unavailable", evidence.Hold(1, null, arr));
        arr.Search.IncludeTags.Clear();
        Assert.Null(evidence.Hold(1, 30, arr));
    }

    [Fact]
    public void AvailabilityFollowsArrDespiteConflictingReleaseDates()
    {
        var arr = new ArrInstanceConfig { Type = "radarr", Search = new() { RequireRadarrAvailability = true } };
        var evidence = SearchLibraryGates.Parse(JArray.Parse("""
            [{"id":1,"isAvailable":false,"digitalRelease":"2000-01-01"},
             {"id":2,"isAvailable":true,"digitalRelease":"2099-01-01"}, {"id":3}]
            """), [], false);
        Assert.Contains("unavailable", evidence.Hold(1, null, arr));
        Assert.Null(evidence.Hold(2, null, arr));
        Assert.Contains("evidence unavailable", evidence.Hold(3, null, arr));
    }

    [Fact]
    public async Task UnavailableOrMalformedObservationsDoNotInventTags()
    {
        var arr = new ArrInstanceConfig { Type = "radarr", Search = new() { ExcludeTags = ["skip"] } };
        var unavailable = await SearchLibraryGates.ObserveAsync(arr, default, (_, _, _) => throw new IOException());
        Assert.Contains("unavailable", unavailable.Hold(1, null, arr));
        var unknown = SearchLibraryGates.Parse(JArray.Parse("""[{"id":1,"tags":[99]}]"""), [], true);
        Assert.Contains("unavailable", unknown.Hold(1, null, arr));
        var duplicate = SearchLibraryGates.Parse(JArray.Parse("""[{"id":1,"tags":[]},{"id":1,"tags":[]}]"""), [], true);
        Assert.Contains("unavailable", duplicate.Hold(1, null, arr));
    }
}
