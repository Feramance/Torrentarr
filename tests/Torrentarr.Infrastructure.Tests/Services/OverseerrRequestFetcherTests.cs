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

    [Fact]
    public async Task FetchAsync_DoesNotReuseReleaseCacheAcrossOverseerrHosts()
    {
        var handler = new StubHandler
        {
            OnSend = req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                var host = req.RequestUri.Host;
                if (path.EndsWith("/api/v1/request", StringComparison.Ordinal))
                    return Json("""{"results":[{"type":"movie","is4k":false,"media":{"tmdbId":100}}]}""");
                if (path.EndsWith("/api/v1/movie/100", StringComparison.Ordinal))
                {
                    if (host == "overseerr.test")
                        return Json("""{"releaseDate":"2020-01-01"}""");
                    return Json("""{"releaseDate":"2099-01-01"}""");
                }
                return Json("{}");
            }
        };
        using var http = new HttpClient(handler);
        var fetcher = new OverseerrRequestFetcher(NullLogger.Instance, http);

        var released = await fetcher.FetchAsync(
            new OverseerrConfig { OverseerrURI = "http://overseerr.test", OverseerrAPIKey = "k" },
            "movie",
            CancellationToken.None);
        released.TmdbIds.Should().Equal(100);

        handler.Paths.Clear();
        var other = await fetcher.FetchAsync(
            new OverseerrConfig { OverseerrURI = "http://other.overseerr.test", OverseerrAPIKey = "k" },
            "movie",
            CancellationToken.None);
        other.TmdbIds.Should().BeEmpty();
        handler.Paths.Should().Contain("/api/v1/movie/100");
    }

    [Theory]
    [InlineData(true, "processing")]
    [InlineData(false, "unavailable")]
    public async Task FetchAsync_UsesPageInfoAndResolvesMissingTvdbId(bool approvedOnly, string filter)
    {
        var handler = new StubHandler
        {
            OnSend = req =>
            {
                req.Headers.GetValues("X-Api-Key").Should().Equal("key");
                var uri = req.RequestUri!;
                if (uri.AbsolutePath == "/proxy/api/v1/request")
                {
                    uri.Query.Should().Contain($"filter={filter}");
                    return uri.Query.Contains("skip=0")
                        ? Json("""
                            {"pageInfo":{"pages":2,"pageSize":1,"results":2,"page":1},
                             "results":[{"type":"tv","is4k":false,"media":{"tmdbId":10,"tvdbId":100}}]}
                            """)
                        : Json("""
                            {"pageInfo":{"pages":2,"pageSize":1,"results":2,"page":2},
                             "results":[{"type":"tv","status":2,"is4k":true,"media":{"tmdbId":20,"status":5,"status4k":3}}]}
                            """);
                }
                uri.AbsolutePath.Should().Be("/proxy/api/v1/tv/20");
                return Json("""{"firstAirDate":"2020-01-01","externalIds":{"tvdbId":200}}""");
            }
        };
        using var http = new HttpClient(handler);
        var result = await new OverseerrRequestFetcher(NullLogger.Instance, http).FetchAsync(
            new OverseerrConfig
            {
                OverseerrURI = "http://seerr.test/proxy/",
                OverseerrAPIKey = "key",
                Is4K = true,
                ApprovedOnly = approvedOnly
            }, "tv", CancellationToken.None);

        result.TvdbIds.Should().Equal(200);
        handler.Paths.Should().Equal("/proxy/api/v1/request", "/proxy/api/v1/request", "/proxy/api/v1/tv/20");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"data":[]}""")]
    public async Task FetchAsync_AcceptsLegacyListFormats(string body)
    {
        using var http = new HttpClient(new StubHandler { OnSend = _ => Json(body) });
        var result = await new OverseerrRequestFetcher(NullLogger.Instance, http).FetchAsync(
            new OverseerrConfig { OverseerrURI = "http://overseerr.test", OverseerrAPIKey = "key" },
            "movie", CancellationToken.None);
        result.TmdbIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task FetchAsync_RejectsIncompleteLaterPage(HttpStatusCode status)
    {
        using var http = new HttpClient(new StubHandler
        {
            OnSend = req => req.RequestUri!.Query.Contains("skip=0")
                ? Json("""
                    {"pageInfo":{"pages":2,"pageSize":1,"results":2},
                     "results":[{"type":"tv"}]}
                    """)
                : new HttpResponseMessage(status) { Content = new StringContent("""
                    {"results":[]}
                    """) }
        });
        var fetcher = new OverseerrRequestFetcher(NullLogger.Instance, http);
        var cfg = new OverseerrConfig { OverseerrURI = "http://seerr.test", OverseerrAPIKey = "key" };

        Func<Task> fetch = () => fetcher.FetchAsync(cfg, "movie", CancellationToken.None);
        if (status == HttpStatusCode.OK)
            await fetch.Should().ThrowAsync<Newtonsoft.Json.JsonException>();
        else
            await fetch.Should().ThrowAsync<HttpRequestException>();
    }

    internal static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    internal sealed class StubHandler : HttpMessageHandler
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
