using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class OverseerrRequestFetcherTests : IDisposable
{
    public OverseerrRequestFetcherTests()
    {
        OverseerrRequestFetcher.ClearCacheForTests();
    }

    public void Dispose() => OverseerrRequestFetcher.ClearCacheForTests();

    [Fact]
    public async Task FetchAsync_SkipsUnreleasedTvUsingTmdbDetail()
    {
        var handler = new StubHandler
        {
            OnSend = req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/api/v1/request", StringComparison.Ordinal))
                {
                    return Json("""
                        {"results":[{"type":"tv","is4k":false,"media":{"tmdbId":99,"tvdbId":123}}]}
                        """);
                }
                if (path.EndsWith("/api/v1/tv/99", StringComparison.Ordinal))
                {
                    return Json("""{"firstAirDate":"2099-01-01"}""");
                }
                return Json("{}");
            }
        };
        using var http = new HttpClient(handler);
        var fetcher = new OverseerrRequestFetcher(NullLogger.Instance, http);
        var cfg = new OverseerrConfig
        {
            SearchOverseerrRequests = true,
            OverseerrURI = "http://overseerr.test",
            OverseerrAPIKey = "k",
            ApprovedOnly = true
        };

        var result = await fetcher.FetchAsync(cfg, "tv", CancellationToken.None);

        result.TvdbIds.Should().BeEmpty();
        handler.Paths.Should().Contain("/api/v1/tv/99");
    }

    [Fact]
    public async Task FetchAsync_IncludesReleasedMovieTmdbId()
    {
        var handler = new StubHandler
        {
            OnSend = req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/api/v1/request", StringComparison.Ordinal))
                {
                    return Json("""
                        {"results":[{"type":"movie","is4k":false,"media":{"tmdbId":42}}]}
                        """);
                }
                if (path.EndsWith("/api/v1/movie/42", StringComparison.Ordinal))
                {
                    return Json("""{"releaseDate":"2020-01-01"}""");
                }
                return Json("{}");
            }
        };
        using var http = new HttpClient(handler);
        var fetcher = new OverseerrRequestFetcher(NullLogger.Instance, http);
        var cfg = new OverseerrConfig
        {
            OverseerrURI = "http://overseerr.test",
            OverseerrAPIKey = "k"
        };

        var result = await fetcher.FetchAsync(cfg, "movie", CancellationToken.None);

        result.TmdbIds.Should().Equal(42);
    }

    [Fact]
    public async Task FetchAsync_DoesNotReuseMovieReleaseCacheForTvSameTmdbId()
    {
        var moviePhase = true;
        var handler = new StubHandler
        {
            OnSend = req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/api/v1/request", StringComparison.Ordinal))
                {
                    if (moviePhase)
                    {
                        return Json("""{"results":[{"type":"movie","is4k":false,"media":{"tmdbId":100}}]}""");
                    }
                    return Json("""{"results":[{"type":"tv","is4k":false,"media":{"tmdbId":100,"tvdbId":5}}]}""");
                }
                if (path.EndsWith("/api/v1/movie/100", StringComparison.Ordinal))
                    return Json("""{"releaseDate":"2020-01-01"}""");
                if (path.EndsWith("/api/v1/tv/100", StringComparison.Ordinal))
                    return Json("""{"firstAirDate":"2099-01-01"}""");
                return Json("{}");
            }
        };
        using var http = new HttpClient(handler);
        var fetcher = new OverseerrRequestFetcher(NullLogger.Instance, http);
        var cfg = new OverseerrConfig
        {
            OverseerrURI = "http://overseerr.test",
            OverseerrAPIKey = "k"
        };

        var movies = await fetcher.FetchAsync(cfg, "movie", CancellationToken.None);
        movies.TmdbIds.Should().Equal(100);

        moviePhase = false;
        handler.Paths.Clear();
        var tv = await fetcher.FetchAsync(cfg, "tv", CancellationToken.None);
        tv.TvdbIds.Should().BeEmpty();
        handler.Paths.Should().Contain("/api/v1/tv/100");
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> OnSend { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(OnSend(request));
        }
    }
}
