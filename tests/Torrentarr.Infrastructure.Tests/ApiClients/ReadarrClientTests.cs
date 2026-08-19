using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

[Collection("ReadarrClientHttp")]
public sealed class ReadarrClientTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private string _lastPath = "";
    private string _lastBody = "";

    public ReadarrClientTests()
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
    public async Task GetAuthorsAsync_DeserializesAuthorList()
    {
        var client = new ReadarrClient(_baseUrl.TrimEnd('/'), "test-key");
        var authors = await client.GetAuthorsAsync();

        authors.Should().ContainSingle();
        authors[0].Id.Should().Be(1);
        authors[0].AuthorName.Should().Be("Ada");
        _lastPath.Should().StartWith("/api/v1/author");
    }

    [Fact]
    public async Task GetBooksAsync_DeserializesBookList()
    {
        var client = new ReadarrClient(_baseUrl.TrimEnd('/'), "test-key");
        var books = await client.GetBooksAsync(authorId: 1);

        books.Should().ContainSingle();
        books[0].Id.Should().Be(10);
        books[0].Title.Should().Be("Dune");
        _lastPath.Should().Contain("/api/v1/book");
        _lastPath.Should().Contain("authorId=1");
    }

    [Fact]
    public async Task SearchBookAsync_PostsBookSearchCommand()
    {
        var client = new ReadarrClient(_baseUrl.TrimEnd('/'), "test-key");
        var ok = await client.SearchBookAsync([10, 11]);

        ok.Should().BeTrue();
        _lastPath.Should().StartWith("/api/v1/command");
        _lastBody.Should().Contain("BookSearch");
        _lastBody.Should().Contain("10");
    }

    [Fact]
    public async Task UpdateAuthorQualityProfileAsync_PreservesUnmappedAuthorFields()
    {
        var client = new ReadarrClient(_baseUrl.TrimEnd('/'), "test-key");
        var ok = await client.UpdateAuthorQualityProfileAsync(1, 9);

        ok.Should().BeTrue();
        _lastPath.Should().StartWith("/api/v1/author/1");
        _lastBody.Should().Contain("\"qualityProfileId\":9");
        _lastBody.Should().Contain("\"metadataProfileId\":7");
        _lastBody.Should().Contain("\"tags\":[3,8]");
        _lastBody.Should().Contain("\"monitorNewItems\":\"all\"");
    }

    [Fact]
    public void ApplyAuthorQualityProfileId_OnlyChangesQualityProfile()
    {
        var author = Newtonsoft.Json.Linq.JObject.Parse(
            """{"id":1,"qualityProfileId":1,"metadataProfileId":7,"tags":[3]}""");
        ReadarrClient.ApplyAuthorQualityProfileId(author, 4);
        author.Value<int>("qualityProfileId").Should().Be(4);
        author.Value<int>("metadataProfileId").Should().Be(7);
        author["tags"]!.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetSystemInfoAsync_ReturnsVersion()
    {
        var client = new ReadarrClient(_baseUrl.TrimEnd('/'), "test-key");
        var info = await client.GetSystemInfoAsync();
        info.Version.Should().Be("10.0.0.1");
    }

    [Fact]
    public async Task GetSystemInfoAsync_ThrowsOnHttpError()
    {
        var port = GetFreePort();
        var baseUrl = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(baseUrl);
        listener.Start();
        var loop = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 401;
            ctx.Response.Close();
        });

        try
        {
            var client = new ReadarrClient(baseUrl.TrimEnd('/'), "bad-key");
            var act = async () => await client.GetSystemInfoAsync();
            await act.Should().ThrowAsync<ArrApiException>();
        }
        finally
        {
            listener.Close();
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* listener stopped */ }
        }
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
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                _lastBody = await reader.ReadToEndAsync();

            var json = "[]";
            if (_lastPath.StartsWith("/api/v1/system/status", StringComparison.Ordinal))
                json = """{"version":"10.0.0.1"}""";
            else if (_lastPath.StartsWith("/api/v1/qualityprofile", StringComparison.Ordinal))
                json = """[{"id":1,"name":"Standard"}]""";
            else if (_lastPath.StartsWith("/api/v1/author/", StringComparison.Ordinal))
            {
                if (string.Equals(ctx.Request.HttpMethod, "PUT", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(_lastBody))
                    json = _lastBody;
                else
                    json = """{"id":1,"authorName":"Ada","monitored":true,"qualityProfileId":1,"metadataProfileId":7,"tags":[3,8],"monitorNewItems":"all"}""";
            }
            else if (_lastPath.StartsWith("/api/v1/author", StringComparison.Ordinal))
                json = """[{"id":1,"authorName":"Ada","monitored":true}]""";
            else if (_lastPath.StartsWith("/api/v1/book", StringComparison.Ordinal))
                json = """[{"id":10,"title":"Dune","authorId":1,"monitored":true}]""";
            else if (_lastPath.StartsWith("/api/v1/command", StringComparison.Ordinal))
                json = """{"id":99}""";

            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
