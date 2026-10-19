using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class LiveWebConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListenerChangesInPlaceAndOccupiedPortRetainsWorkingAddress(bool occupied)
    {
        var directory = Path.Combine(Path.GetTempPath(), "torrentarr-web-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var initial = new TorrentarrConfig { WebUI = new() { Host = "127.0.0.1", Port = FreePort() } };
            var loader = new ConfigurationLoader(Path.Combine(directory, "config.toml"));
            using var reloader = new ConfigReloader(NullLogger<ConfigReloader>.Instance, initial, loader);
            reloader.Apply(initial).Should().BeTrue();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(initial);
            builder.Services.AddSingleton(reloader);
            builder.Services.AddSingleton<IConfigReloader>(reloader);
            var liveWeb = new LiveWebConfiguration(builder.WebHost, initial.WebUI);
            await using var app = builder.Build();
            liveWeb.Attach(app, initial);
            app.MapGet("/health", () => "ok");
            await app.StartAsync();
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
                (await client.GetStringAsync($"http://127.0.0.1:{initial.WebUI.Port}/health")).Should().Be("ok");
                var originalPort = initial.WebUI.Port;
                using var unavailable = new TcpListener(IPAddress.Loopback, 0);
                if (occupied) unavailable.Start();
                var nextPort = occupied ? ((IPEndPoint)unavailable.LocalEndpoint).Port : FreePort();
                var candidate = new TorrentarrConfig { WebUI = new() { Host = "127.0.0.1", Port = nextPort, UrlBase = "/newbase" } };
                reloader.Apply(candidate).Should().Be(!occupied);
                var address = occupied ? $"http://127.0.0.1:{originalPort}/health" : $"http://127.0.0.1:{nextPort}/newbase/health";
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                while (true)
                {
                    if (timeout.IsCancellationRequested) throw new Exception("Listener did not update; active addresses: " + string.Join(",", app.Urls));
                    try { (await client.GetStringAsync(address, timeout.Token)).Should().Be("ok"); break; }
                    catch (HttpRequestException) { await Task.Delay(50); }
                }
                initial.WebUI.Port.Should().Be(occupied ? originalPort : nextPort);
                while (!occupied && reloader.GetStatus().State == "pending") await Task.Delay(20, timeout.Token);
                reloader.GetStatus().State.Should().Be(occupied ? "error" : "applied");
            }
            finally { await app.StopAsync(); }
        }
        finally { Directory.Delete(directory, true); }
    }
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
