using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Text.Json;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Endpoints;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Endpoints;

public class ArrCatalogDbSafeTests
{
    [Theory]
    [InlineData("/web/radarr/movies/movies", true)]
    [InlineData("/api/sonarr/tv/series", true)]
    [InlineData("/web/readarr/books/authors", true)]
    [InlineData("/web/config", false)]
    public void IsCatalogPath_MatchesArrRoutes(string path, bool expected)
    {
        ArrCatalogDbSafe.IsCatalogPath(new PathString(path)).Should().Be(expected);
    }

    [Fact]
    public void IsSqliteCorruption_DetectsMalformedDiskImage()
    {
        DatabaseRetryExtensions.IsSqliteCorruption(new InvalidOperationException("database disk image is malformed"))
            .Should().BeTrue();
        DatabaseRetryExtensions.IsSqliteCorruption(new InvalidOperationException("locked"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task CatalogCorruption_ReturnsOfflineGuidanceWithoutDatabaseMaintenance()
    {
        var health = new Mock<IDatabaseHealthService>(MockBehavior.Strict);
        using var services = new ServiceCollection().AddSingleton(health.Object).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseArrCatalogDbSafe();
        app.Run(_ => throw new InvalidOperationException("database disk image is malformed"));
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/api/sonarr/tv/series";
        context.Response.Body = body;

        await app.Build()(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body);
        json.RootElement.GetProperty("error").GetString().Should().Contain("offline");
        health.VerifyNoOtherCalls();
    }
}
