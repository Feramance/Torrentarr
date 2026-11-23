using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public sealed class IssueAlertEndpointTests(TorrentarrWebApplicationFactory factory) : IClassFixture<TorrentarrWebApplicationFactory>
{
    [Fact]
    public async Task SendTestUsesSavedDestinationAndCreatesNoIssueTransition()
    {
        using var anonymous = factory.CreateClient();
        using var client = factory.CreateClientWithApiToken();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/issues/alerts/test", null)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        var count = await db.IssueAlertEvents.CountAsync();
        var settings = scope.ServiceProvider.GetRequiredService<TorrentarrConfig>().Settings;
        var original = settings.IssueAlertsSecretUrl;

        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        const string secret = "test-destination-secret";
        try
        {
            settings.IssueAlertsSecretUrl = $"http://127.0.0.1:{port}/test?token={secret}";
            var send = client.PostAsync("/api/issues/alerts/test", null, timeout.Token);
            var incoming = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using var body = new StreamReader(incoming.Request.InputStream);
            var payload = await body.ReadToEndAsync(timeout.Token);
            Assert.Equal(secret, incoming.Request.QueryString["token"]);
            Assert.DoesNotContain(secret, payload);
            Assert.Contains("\"state\":\"test\"", payload);
            incoming.Response.StatusCode = 204;
            incoming.Response.Close();
            var result = await send;
            result.EnsureSuccessStatusCode();
            Assert.DoesNotContain(secret, await result.Content.ReadAsStringAsync(timeout.Token));
            Assert.Equal(count, await db.IssueAlertEvents.CountAsync(timeout.Token));
        }
        finally { settings.IssueAlertsSecretUrl = original; }
    }
}
