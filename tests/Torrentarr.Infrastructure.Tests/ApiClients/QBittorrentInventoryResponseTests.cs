using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

public sealed class QBittorrentInventoryResponseTests
{
    [Theory]
    [InlineData(403, "", "version")]
    [InlineData(503, "secret-response", "version")]
    [InlineData(403, "", "torrents")]
    [InlineData(403, "", "files")]
    [InlineData(503, "secret-response", "files")]
    [InlineData(403, "", "trackers")]
    [InlineData(200, "null", "torrents")]
    [InlineData(200, "{}", "files")]
    [InlineData(200, "invalid", "trackers")]
    [InlineData(200, "", "torrents")]
    public async Task FailedOrInvalidInventory_ThrowsWithoutResponseSecrets(int status, string body, string operation)
    {
        await Request(status, body, operation, async action =>
            (await FluentActions.Awaiting(action).Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().NotContain(body == "" ? "secret" : body));
    }

    [Theory]
    [InlineData("torrents")]
    [InlineData("files")]
    [InlineData("trackers")]
    public async Task SuccessfulEmptyArray_IsAccepted(string operation)
        => await Request(200, "[]", operation, async action => await action());

    [Theory]
    [InlineData("torrents")]
    [InlineData("files")]
    [InlineData("trackers")]
    [InlineData("version")]
    public async Task ExpiredSession_ReauthenticatesAndRetriesInventoryOnce(string operation)
    {
        using var listener = StartListenerOnAvailablePort(out var port);
        var paths = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                var request = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
                paths.Add(request.Request.Url!.AbsolutePath);
                request.Response.StatusCode = i == 0 ? 403 : 200;
                if (i == 1) request.Response.Headers.Add("Set-Cookie", "SID=renewed; Path=/");
                if (i == 2) request.Request.Headers["Cookie"].Should().Contain("SID=renewed");
                var bytes = Encoding.UTF8.GetBytes(i == 1 ? "Ok." : operation == "version" ? "v5.1.0" : "[]");
                request.Response.ContentLength64 = bytes.Length;
                await request.Response.OutputStream.WriteAsync(bytes);
                request.Response.Close();
            }
        });
        using var client = new QBittorrentClient("127.0.0.1", port, "u", "p");
        if (operation == "version") (await client.GetVersionAsync()).Should().Be("v5.1.0");
        else if (operation == "files") await client.GetTorrentFilesAsync("abc");
        else if (operation == "trackers") await client.GetTorrentTrackersAsync("abc");
        else await client.GetTorrentsAsync();
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        paths[1].Should().Be("/api/v2/auth/login");
        paths[2].Should().Be(paths[0]);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(-1)]
    public async Task Delete_DistinguishesRejectionFromUncertainOutcomeWithoutRetry(int status)
    {
        await Request(status, "", "delete", async action =>
        {
            if (status is 200 or 400 or 401 or 403) await action();
            else (await FluentActions.Awaiting(action).Should().ThrowAsync<HttpRequestException>())
                .Which.Message.Should().Contain("outcome is uncertain");
        });
    }

    private static HttpListener StartListenerOnAvailablePort(out int port)
    {
        // Reserving an ephemeral socket and immediately releasing it cannot guarantee that
        // another parallel test will not claim the port before HttpListener.Start().
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException ex) when (ex.NativeErrorCode is 98 or 10048 or 183 && attempt < 19)
            {
                listener.Close();
            }
            catch
            {
                listener.Close();
                throw;
            }
        }
        throw new InvalidOperationException("Unable to start HTTP test listener after retrying port collisions.");
    }

    private static async Task Request(int status, string body, string operation, Func<Func<Task>, Task> verify)
    {
        using var listener = StartListenerOnAvailablePort(out var port);
        using var cancellation = new CancellationTokenSource();
        var requests = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var request = await listener.GetContextAsync().WaitAsync(cancellation.Token);
                    Interlocked.Increment(ref requests);
                    if (status == -1)
                    {
                        request.Response.ContentLength64 = 100;
                        await request.Response.OutputStream.WriteAsync(new byte[] { 1 });
                        await request.Response.OutputStream.FlushAsync();
                        request.Response.Abort();
                        continue;
                    }
                    request.Response.StatusCode = status;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    request.Response.ContentLength64 = bytes.Length;
                    await request.Response.OutputStream.WriteAsync(bytes);
                    request.Response.Close();
                }
            }
            catch (OperationCanceledException) { }
        });
        using var client = new QBittorrentClient("127.0.0.1", port, "u", "p");
        Func<Task> action = operation switch
        {
            "delete" => async () => { (await client.DeleteTorrentsAsync(["abc"])).Should().Be(status == 200); }
            ,
            "version" => async () => { await client.GetVersionAsync(); }
            ,
            "files" => async () => { (await client.GetTorrentFilesAsync("abc")).Should().BeEmpty(); }
            ,
            "trackers" => async () => { (await client.GetTorrentTrackersAsync("abc")).Should().BeEmpty(); }
            ,
            _ => async () => { (await client.GetTorrentsAsync()).Should().BeEmpty(); }
        };
        try { await verify(() => action().WaitAsync(TimeSpan.FromSeconds(10))); }
        finally
        {
            cancellation.Cancel();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        requests.Should().Be(operation == "delete" ? 1 : status == 503 ? 4 : status == 403 ? 2 : 1);
    }
}
