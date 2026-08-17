using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

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
            if (_lastPath.StartsWith("/api/v1/author", StringComparison.Ordinal))
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
