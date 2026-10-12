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

    private static async Task Request(int status, string body, string operation, Func<Func<Task>, Task> verify)
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
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
                    request.Response.StatusCode = status;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    request.Response.ContentLength64 = bytes.Length;
                    await request.Response.OutputStream.WriteAsync(bytes);
                    request.Response.Close();
                }
            }
            catch (OperationCanceledException) { }
        });
        var client = new QBittorrentClient("127.0.0.1", port, "u", "p");
        Func<Task> action = operation switch
        {
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
        requests.Should().Be(status == 503 ? 4 : 1);
    }
}
