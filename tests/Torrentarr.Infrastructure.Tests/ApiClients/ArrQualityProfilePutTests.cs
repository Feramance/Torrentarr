using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

[Collection("ArrQualityProfileHttp")]
public sealed class ArrQualityProfilePutTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private string _lastPath = "";
    private string _lastBody = "";
    private string _lastMethod = "";

    public ArrQualityProfilePutTests()
    {
        var port = GetFreePort();
        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
        _loop = Task.Run(ListenLoopAsync);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* listener stopped */ }
        _cts.Dispose();
    }

    [Fact]
    public void ApplyQualityProfileId_OnlyChangesQualityProfile()
    {
        var resource = Newtonsoft.Json.Linq.JObject.Parse(
            """{"id":1,"qualityProfileId":1,"tags":[3],"metadataProfileId":7}""");
        ArrQualityProfilePut.ApplyQualityProfileId(resource, 4);
        resource.Value<int>("qualityProfileId").Should().Be(4);
        resource.Value<int>("metadataProfileId").Should().Be(7);
        resource["tags"]!.Should().HaveCount(1);
    }

    [Fact]
    public async Task UpdateMovieQualityProfileAsync_PreservesUnmappedMovieFields()
    {
        var client = new RadarrClient(_baseUrl.TrimEnd('/'), "test-key");
        var ok = await client.UpdateMovieQualityProfileAsync(1, 9);

        ok.Should().BeTrue();
        _lastPath.Should().StartWith("/api/v3/movie/1");
        _lastBody.Should().Contain("\"qualityProfileId\":9");
        _lastBody.Should().Contain("\"tags\":[3,8]");
        _lastBody.Should().Contain("\"minimumAvailability\":\"released\"");
    }

    [Fact]
    public async Task UpdateSeriesQualityProfileAsync_PreservesUnmappedSeriesFields()
    {
        var client = new SonarrClient(_baseUrl.TrimEnd('/'), "test-key");
        var ok = await client.UpdateSeriesQualityProfileAsync(1, 9);

        ok.Should().BeTrue();
        _lastPath.Should().StartWith("/api/v3/series/1");
        _lastBody.Should().Contain("\"qualityProfileId\":9");
        _lastBody.Should().Contain("\"tags\":[3,8]");
        _lastBody.Should().Contain("\"seasonFolder\":true");
    }

    [Fact]
    public async Task UpdateArtistQualityProfileAsync_PreservesUnmappedArtistFields()
    {
        var client = new LidarrClient(_baseUrl.TrimEnd('/'), "test-key");
        var ok = await client.UpdateArtistQualityProfileAsync(1, 9);

        ok.Should().BeTrue();
        _lastPath.Should().StartWith("/api/v1/artist/1");
        _lastBody.Should().Contain("\"qualityProfileId\":9");
        _lastBody.Should().Contain("\"tags\":[3,8]");
        _lastBody.Should().Contain("\"metadataProfileId\":7");
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                break;
            }

            _lastPath = ctx.Request.Url?.PathAndQuery ?? "";
            _lastMethod = ctx.Request.HttpMethod ?? "";
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                _lastBody = await reader.ReadToEndAsync();

            var json = "{}";
            if (_lastPath.StartsWith("/api/v3/movie/", StringComparison.Ordinal))
            {
                json = PutOrGet("""{"id":1,"title":"Dune","qualityProfileId":1,"tags":[3,8],"minimumAvailability":"released"}""");
            }
            else if (_lastPath.StartsWith("/api/v3/series/", StringComparison.Ordinal))
            {
                json = PutOrGet("""{"id":1,"title":"Show","qualityProfileId":1,"tags":[3,8],"seasonFolder":true}""");
            }
            else if (_lastPath.StartsWith("/api/v1/artist/", StringComparison.Ordinal))
            {
                json = PutOrGet("""{"id":1,"artistName":"Ada","qualityProfileId":1,"tags":[3,8],"metadataProfileId":7}""");
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private string PutOrGet(string getJson) =>
        string.Equals(_lastMethod, "PUT", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_lastBody)
            ? _lastBody
            : getJson;

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
