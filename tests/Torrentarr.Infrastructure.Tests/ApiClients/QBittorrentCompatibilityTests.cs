using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Newtonsoft.Json;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

public sealed class QBittorrentCompatibilityTests
{
    public static IEnumerable<object[]> Versions()
    {
        foreach (var version in new[] { "v4.1.0", "4.3.9", "v4.5.5", "v4.6.0", "v5.0.0", "v5.1.0", "v5.2.0", "v5.3.0", "v5.3.0rc1", "v5.3.0-beta1" })
            foreach (var inactive in new[] { false, true }) yield return [version, inactive];
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task Requests_MatchEachVersionAndPreserveOmittedSettings(string version, bool explicitInactive)
    {
        await using var server = new QBitServer(version);
        using var client = server.Client();
        (await client.PauseTorrentsAsync(["abc", "def"])).Should().BeTrue();
        (await client.ResumeTorrentAsync("abc")).Should().BeTrue();
        (await (explicitInactive ? client.SetShareLimitsAsync("abc", -1, -1, -1) : client.SetShareLimitsAsync("abc", -1, -1))).Should().BeTrue();
        var modern = version.Contains("5.3");
        var inactiveSupported = version.Contains("4.6") || version.TrimStart('v').StartsWith("5.");
        var requests = server.Requests.ToArray();
        requests.Count(r => r.Path == "/api/v2/app/version").Should().Be(1);
        requests[1].Path.Should().Be(version.TrimStart('v').StartsWith("5.") ? "/api/v2/torrents/stop" : "/api/v2/torrents/pause");
        requests[1].Form["hashes"].Should().Be("abc|def");
        requests[2].Path.Should().Be(version.TrimStart('v').StartsWith("5.") ? "/api/v2/torrents/start" : "/api/v2/torrents/resume");
        var form = requests.Last().Form;
        var expected = new Dictionary<string, string> { ["hashes"] = "abc", ["ratioLimit"] = "-1", ["seedingTimeLimit"] = "-1" };
        if (inactiveSupported) expected["inactiveSeedingTimeLimit"] = explicitInactive ? "-1" : "45";
        if (modern) { expected["shareLimitAction"] = "RemoveWithContent"; expected["shareLimitsMode"] = "MatchAll"; }
        form.Should().BeEquivalentTo(expected);
        var reads = requests.Where(r => r.Path == "/api/v2/torrents/info").ToList();
        reads.Count.Should().Be(modern || (inactiveSupported && !explicitInactive) ? 1 : 0);
        reads.Should().OnlyContain(r => r.Query == "?hashes=abc");
    }

    [Fact]
    public async Task MixedClients_DoNotShareVersionDetection()
    {
        await using var oldServer = new QBitServer("v4.1.0");
        await using var newServer = new QBitServer("v5.3.0");
        using var oldClient = oldServer.Client();
        using var newClient = newServer.Client();
        await Task.WhenAll(oldClient.PauseTorrentAsync("abc"), newClient.PauseTorrentAsync("abc"));
        oldServer.Requests.Last().Path.Should().EndWith("/pause");
        newServer.Requests.Last().Path.Should().EndWith("/stop");
    }

    [Theory]
    [InlineData("unrecognized", 200)]
    [InlineData("v5.3.0", 400)]
    public async Task DetectionFailure_UsesLegacyRequestsWithoutInventingSettings(string version, int status)
    {
        await using var server = new QBitServer(version) { VersionStatus = status };
        using var client = server.Client();
        (await client.PauseTorrentAsync("abc")).Should().BeTrue();
        (await client.SetShareLimitsAsync("abc", 2, 60)).Should().BeTrue();
        server.Requests.ElementAt(1).Path.Should().EndWith("/pause");
        server.Requests.Last().Form.Keys.Should().BeEquivalentTo("hashes", "ratioLimit", "seedingTimeLimit");
    }

    [Fact]
    public async Task FailedDetection_IsRetriedAfterRecovery()
    {
        await using var server = new QBitServer("v5.3.0") { VersionStatus = 400 };
        using var client = server.Client();
        await client.PauseTorrentAsync("abc");
        server.Requests.Last().Path.Should().EndWith("/pause");
        server.VersionStatus = 200;
        await client.PauseTorrentAsync("abc");
        server.Requests.Last().Path.Should().EndWith("/stop");
        await client.ResumeTorrentAsync("abc");
        server.Requests.Count(r => r.Path.EndsWith("/version")).Should().Be(2);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"hash\":\"abc\",\"inactive_seeding_time_limit\":45}]")]
    [InlineData("[{\"hash\":\"other\",\"inactive_seeding_time_limit\":45,\"share_limit_action\":\"Stop\",\"share_limits_mode\":\"Default\"}]")]
    public async Task MissingModernSettings_DoesNotMutate(string inventory)
    {
        await using var server = new QBitServer("v5.3.0") { Inventory = inventory };
        using var client = server.Client();
        (await client.SetShareLimitsAsync("abc", -1, -1)).Should().BeFalse();
        server.Requests.Should().NotContain(r => r.Path.EndsWith("/setShareLimits"));
    }

    [Theory]
    [InlineData(400, "[]")]
    [InlineData(200, "invalid")]
    public async Task CurrentSettingsReadFailure_ReportsRejectionWithoutMutating(int status, string inventory)
    {
        await using var server = new QBitServer("v5.3.0") { InventoryStatus = status, Inventory = inventory };
        using var client = server.Client();
        (await client.SetShareLimitsAsync("abc", -1, -1)).Should().BeFalse();
        server.Requests.Should().NotContain(r => r.Path.EndsWith("/setShareLimits"));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("limits")]
    public async Task ExpiredMutationSession_RenewsOnceAndUsesNewCookie(string operation)
    {
        await using var server = new QBitServer("v5.3.0") { ExpireMutation = true };
        using var client = server.Client();
        var accepted = operation switch
        {
            "pause" => await client.PauseTorrentAsync("abc"),
            "resume" => await client.ResumeTorrentAsync("abc"),
            _ => await client.SetShareLimitsAsync("abc", -1, -1, -1)
        };
        accepted.Should().BeTrue();
        server.Requests.Count(r => r.Path.EndsWith("/login")).Should().Be(1);
        server.Requests.Last().Cookie.Should().Contain("QBT_SID_8080=renewed");
    }

    [Theory]
    [InlineData("pause", 400)]
    [InlineData("resume", 400)]
    [InlineData("limits", 400)]
    [InlineData("pause", 403)]
    [InlineData("resume", 403)]
    [InlineData("limits", 403)]
    public async Task RejectedMutation_RemainsFalse(string operation, int status)
    {
        await using var server = new QBitServer("v5.3.0") { MutationStatus = status };
        using var client = server.Client();
        var accepted = operation switch
        {
            "pause" => await client.PauseTorrentAsync("abc"),
            "resume" => await client.ResumeTorrentAsync("abc"),
            _ => await client.SetShareLimitsAsync("abc", -1, -1, -1)
        };
        accepted.Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_DoesNotSendMutations()
    {
        await using var server = new QBitServer("v5.3.0");
        using var client = server.Client();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await FluentActions.Awaiting(() => client.PauseTorrentAsync("abc", canceled.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => client.SetShareLimitsAsync("abc", -1, -1, canceled.Token)).Should().ThrowAsync<OperationCanceledException>();
        server.Requests.Should().BeEmpty();
    }

    [Fact]
    public void OldInventory_LeavesNewFieldsAbsentAndOutOfPublicJson()
    {
        var torrent = JsonConvert.DeserializeObject<TorrentInfo>("{\"hash\":\"abc\"}")!;
        torrent.InactiveSeedingTimeLimit.Should().BeNull();
        torrent.ShareLimitAction.Should().BeNull();
        torrent.ShareLimitsMode.Should().BeNull();
        torrent.ShareLimitAction = "Stop"; torrent.ShareLimitsMode = "MatchAny";
        System.Text.Json.JsonSerializer.Serialize(torrent).Should().NotContain("ShareLimitAction").And.NotContain("ShareLimitsMode");
    }

    private sealed record Request(string Path, string Query, Dictionary<string, string> Form, string Cookie);

    private sealed class QBitServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly int _port;
        public ConcurrentQueue<Request> Requests { get; } = new();
        public int VersionStatus { get; set; } = 200;
        public int InventoryStatus { get; init; } = 200;
        public int MutationStatus { get; init; } = 200;
        public bool ExpireMutation { get; set; }
        public string Inventory { get; init; } = "[{\"hash\":\"abc\",\"inactive_seeding_time_limit\":45,\"share_limit_action\":\"RemoveWithContent\",\"share_limits_mode\":\"MatchAll\"}]";

        public QBitServer(string version)
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start(); _port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/"); _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                        var path = context.Request.Url!.AbsolutePath;
                        var body = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
                        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.Length > 1 ? p[1] : ""));
                        Requests.Enqueue(new(path, context.Request.Url.Query, form, context.Request.Headers["Cookie"] ?? ""));
                        var content = "";
                        if (path.EndsWith("/version")) { context.Response.StatusCode = VersionStatus; content = version; }
                        else if (path.EndsWith("/info")) { context.Response.StatusCode = InventoryStatus; content = Inventory; }
                        else if (path.EndsWith("/login"))
                        {
                            context.Response.StatusCode = 204;
                            context.Response.Headers.Add("Set-Cookie", "QBT_SID_8080=renewed; Path=/");
                        }
                        else if (ExpireMutation) { context.Response.StatusCode = 403; ExpireMutation = false; }
                        else context.Response.StatusCode = MutationStatus;
                        var bytes = Encoding.UTF8.GetBytes(content);
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes);
                        context.Response.Close();
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            });
        }

        public QBittorrentClient Client() => new("127.0.0.1", _port, "user", "password");
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _loop.WaitAsync(TimeSpan.FromSeconds(5));
            _listener.Close(); _stop.Dispose();
        }
    }
}
