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
    [InlineData(200, "2.14.1", true, null)]
    [InlineData(200, "2.13.1", false, "support was not confirmed")]
    [InlineData(401, "secret-response", false, "rejected the API key")]
    [InlineData(403, "secret-response", false, "denied API-key access")]
    public async Task ApiKeyAuthentication_UsesBearerWithoutCookieLogin(int status, string response, bool accepted, string? reason)
    {
        const string key = "qbt_ABCDEFGHIJKLMNOPQRSTUVWXYZ01";
        using var listener = LoopbackHttpListener.Start(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task ReplyAsync(string path, int code, string content)
        {
            var request = await listener.GetContextAsync().WaitAsync(timeout.Token);
            request.Request.Url!.AbsolutePath.Should().Be(path);
            request.Request.Headers["Authorization"].Should().Be("Bearer " + key);
            request.Request.Headers["Cookie"].Should().BeNull();
            request.Response.StatusCode = code;
            var bytes = Encoding.UTF8.GetBytes(content);
            request.Response.ContentLength64 = bytes.Length;
            await request.Response.OutputStream.WriteAsync(bytes, timeout.Token);
            request.Response.Close();
        }
        var server = Task.Run(async () =>
        {
            await ReplyAsync("/api/v2/app/webapiVersion", status, response);
            if (!accepted) return;
            await ReplyAsync("/api/v2/torrents/info", 200, "[]");
            await ReplyAsync("/api/v2/torrents/delete", 200, "");
            await ReplyAsync("/api/v2/app/webapiVersion", status, response);
        });
        using var client = new QBittorrentClient("127.0.0.1", port, "user", "password", apiKey: key);
        (await client.LoginAsync(timeout.Token)).Should().Be(accepted);
        if (accepted)
        {
            (await client.GetTorrentsAsync(cancellationToken: timeout.Token)).Should().BeEmpty();
            (await client.DeleteTorrentsAsync(["fixture-hash"], deleteFiles: false, timeout.Token)).Should().BeTrue();
            (await client.LoginAsync(timeout.Token)).Should().BeTrue();
        }
        else
        {
            client.LastConnectionFailure.Should().Contain(reason!).And.NotContain(key).And.NotContain(response);
        }
        await server;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLogin_PreservesTransportDiagnosisWithoutCredentials(bool useKey)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen();
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Reset accepted connections; bound non-listening sockets can time out on macOS and Windows.
        var server = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var connection = await socket.AcceptAsync(timeout.Token);
                    connection.LingerState = new LingerOption(true, 0);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        });
        const string key = "qbt_ABCDEFGHIJKLMNOPQRSTUVWXYZ01";
        using var client = new QBittorrentClient("127.0.0.1", port, "private-user", "private-password", apiKey: useKey ? key : null);
        try
        {
            (await client.LoginAsync(timeout.Token)).Should().BeFalse();
            if (useKey)
                client.LastConnectionFailure.Should().Contain("Unable to reach qBittorrent").And.NotContain(key);
            else
                client.LastConnectionFailure.Should().Contain("transport=").And.MatchRegex(@"(?:Socket error=|Network failure: )\w+\.");
            client.LastConnectionFailure.Should().NotContain("private-user").And.NotContain("private-password");
        }
        finally
        {
            await timeout.CancelAsync();
            await server;
        }
    }

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
    [InlineData(200, "1")]
    [InlineData(200, "0")]
    [InlineData(200, "unknown")]
    [InlineData(404, "")]
    public async Task AlternateSpeedLimits_RequireAnExplicitQBitState(int status, string body)
        => await Request(status, body, "alternate-speed", action => action());

    [Theory]
    [InlineData(200, "v5.2.0")]
    [InlineData(200, "<html>login</html>")]
    [InlineData(403, "Forbidden")]
    public async Task BlankCredentials_VerifyClientConfiguredBypass(int status, string body)
        => await Request(status, body, "bypass", action => action());

    [Theory]
    [InlineData("torrents")]
    [InlineData("files")]
    [InlineData("trackers")]
    [InlineData("version")]
    public async Task ExpiredSession_ReauthenticatesAndRetriesInventoryOnce(string operation)
    {
        using var listener = LoopbackHttpListener.Start(out var port);
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

    private static async Task Request(int status, string body, string operation, Func<Func<Task>, Task> verify)
    {
        using var listener = LoopbackHttpListener.Start(out var port);
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
                    if (operation == "alternate-speed")
                        request.Request.Url!.AbsolutePath.Should().Be("/api/v2/transfer/speedLimitsMode");
                    if (operation == "bypass")
                    {
                        request.Request.Url!.AbsolutePath.Should().Be("/api/v2/app/version");
                        request.Request.Headers["Authorization"].Should().BeNull();
                    }
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
        using var client = operation == "bypass"
            ? new QBittorrentClient("127.0.0.1", port, "", "")
            : new QBittorrentClient("127.0.0.1", port, "u", "p");
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
            "alternate-speed" => async () =>
            {
                bool? expected = status == 200 ? body switch { "0" => false, "1" => true, _ => null } : null;
                (await client.GetAlternateSpeedLimitsStateAsync()).Should().Be(expected);
            }
            ,
            "bypass" => async () => { (await client.LoginAsync()).Should().Be(status == 200 && body == "v5.2.0"); }
            ,
            _ => async () => { (await client.GetTorrentsAsync()).Should().BeEmpty(); }
        };
        try { await verify(() => action().WaitAsync(TimeSpan.FromSeconds(10))); }
        finally
        {
            cancellation.Cancel();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        requests.Should().Be(operation is "delete" or "bypass" ? 1 : status == 503 ? 4 : status == 403 ? 2 : 1);
    }
}
