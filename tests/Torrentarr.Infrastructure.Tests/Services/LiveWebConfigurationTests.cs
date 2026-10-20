using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class LiveWebConfigurationTests
{
    [Fact]
    public async Task ListenerChange_IsSentBeforeOldListenerCloses_AndNewListenerServes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var config = new TorrentarrConfig { WebUI = new() { Host = "127.0.0.1", Port = 0 } };
        var reloader = new Mock<IConfigReloader>();
        reloader.Setup(r => r.GetStatus()).Returns(new ConfigurationApplicationStatus(1, "applied", new(), null));
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(reloader.Object);
        var live = new LiveWebConfiguration(builder.WebHost, config.WebUI);
        await using var app = builder.Build();
        live.Attach(app, config);
        app.MapGet("/health", () => "ok");
        await app.StartAsync(timeout.Token);
        try
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync(app.Urls.Single() + "/web/config/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            (await reader.ReadLineAsync(timeout.Token)).Should().StartWith("data: ");
            using (var socket = new TcpListener(IPAddress.Loopback, 0))
            {
                socket.Start();
                config.WebUI.Port = ((IPEndPoint)socket.LocalEndpoint).Port;
            }
            config.WebUI.Theme = "Light";
            reloader.Raise(r => r.ConfigReloaded += null, new ConfigReloadedEventArgs { Success = true, Revision = 2 });
            string? message;
            do { message = await reader.ReadLineAsync(timeout.Token); } while (string.IsNullOrEmpty(message));
            var data = JsonDocument.Parse(message![6..]).RootElement;
            data.GetProperty("webui").GetProperty("port").GetInt32().Should().Be(config.WebUI.Port);
            data.GetProperty("webui").GetProperty("theme").GetString().Should().Be("Light");
            response.Dispose();
            while (true)
            {
                try
                {
                    (await client.GetStringAsync($"http://127.0.0.1:{config.WebUI.Port}/health", timeout.Token)).Should().Be("ok");
                    break;
                }
                catch (HttpRequestException) { await Task.Delay(50, timeout.Token); }
            }
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }
}
