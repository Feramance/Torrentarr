using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class ArrSeriesEpisodeFetchTests
{
    [Fact]
    public void ShouldSkipSeries_Http415_ReturnsTrue()
    {
        var ex = new ArrApiException("415", HttpStatusCode.UnsupportedMediaType, "");
        ArrSeriesEpisodeFetch.ShouldSkipSeries(ex).Should().BeTrue();
    }

    [Fact]
    public void ShouldSkipSeries_Transport_Rethrows()
    {
        var ex = new ArrApiException("down", statusCode: null, "timeout");
        var act = () => ArrSeriesEpisodeFetch.ShouldSkipSeries(ex);
        act.Should().Throw<ArrApiException>().Which.Should().BeSameAs(ex);
    }

    [Fact]
    public async Task CollectEpisodeIdsAsync_SkipsHttpErrorAndFlagsPruneSkip()
    {
        var series = new[] { 1, 2, 3 };
        var (skip, ids) = await ArrSeriesEpisodeFetch.CollectEpisodeIdsAsync(
            series,
            (sid, _) => sid == 2
                ? Task.FromException<IReadOnlyList<int>>(new ArrApiException("415", HttpStatusCode.UnsupportedMediaType, ""))
                : Task.FromResult<IReadOnlyList<int>>(new[] { sid * 10 }),
            NullLogger.Instance,
            "Sonarr-TV",
            CancellationToken.None);

        skip.Should().BeTrue();
        ids.Should().Equal(10, 30);
    }

    [Fact]
    public async Task CollectEpisodeIdsAsync_TransportError_AbortsRemaining()
    {
        var seen = new List<int>();
        var series = new[] { 1, 2, 3 };
        var act = async () => await ArrSeriesEpisodeFetch.CollectEpisodeIdsAsync(
            series,
            (sid, _) =>
            {
                seen.Add(sid);
                if (sid == 2)
                    return Task.FromException<IReadOnlyList<int>>(new HttpRequestException("down"));
                return Task.FromResult<IReadOnlyList<int>>(new[] { sid });
            },
            NullLogger.Instance,
            "Sonarr-TV",
            CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        seen.Should().Equal(1, 2);
    }

    [Fact]
    public async Task CollectEpisodeIdsAsync_AllHttpFail_FlagsPruneSkip()
    {
        var (skip, ids) = await ArrSeriesEpisodeFetch.CollectEpisodeIdsAsync(
            new[] { 1, 2 },
            (_, _) => Task.FromException<IReadOnlyList<int>>(
                new ArrApiException("415", HttpStatusCode.UnsupportedMediaType, "")),
            NullLogger.Instance,
            "Sonarr-TV",
            CancellationToken.None);

        skip.Should().BeTrue();
        ids.Should().BeEmpty();
    }

    [Fact]
    public async Task CollectEpisodeIdsAsync_AllOk_DoesNotSkipPrune()
    {
        var (skip, ids) = await ArrSeriesEpisodeFetch.CollectEpisodeIdsAsync(
            new[] { 1, 2 },
            (sid, _) => Task.FromResult<IReadOnlyList<int>>(new[] { sid }),
            NullLogger.Instance,
            "Sonarr-TV",
            CancellationToken.None);

        skip.Should().BeFalse();
        ids.Should().Equal(1, 2);
    }
}
