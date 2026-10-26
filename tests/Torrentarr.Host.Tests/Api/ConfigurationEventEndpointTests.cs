using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Services;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public class ConfigurationEventEndpointTests(TorrentarrWebApplicationFactory factory)
{
    [Fact]
    public async Task Events_SendInitialSnapshotAndAcknowledgments_WithoutHoldingConfigurationLease()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = factory.CreateClientWithApiToken();
        using var response = await client.GetAsync("/web/config/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var initial = await reader.ReadLineAsync(timeout.Token);
        initial.Should().StartWith("data: ");
        var json = JsonDocument.Parse(initial![6..]).RootElement;
        json.GetProperty("webui").TryGetProperty("token", out _).Should().BeFalse();
        var reloader = factory.Services.GetRequiredService<IConfigReloader>();
        using (Torrentarr.Core.Configuration.ConfigMutationGuard.EnterPublication(factory.TempConfigPath))
            reloader.Acknowledge("stream-test", reloader.Revision);
        string? line;
        do { line = await reader.ReadLineAsync(timeout.Token); }
        while (line == null || !line.Contains("stream-test"));
        line.Should().Contain("appliedRevision");
    }
}

[Collection("HostWebAuth")]
public class ConfigurationEventAuthenticationTests(AuthEnabledWebApplicationFactory factory)
{
    [Fact]
    public async Task Events_RequireAuthentication()
    {
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await client.GetAsync("/web/config/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
