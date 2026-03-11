using System.Net;
using FluentAssertions;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// When TORRENTARR_EXPORT_OPENAPI is set, exports the OpenAPI spec to docs/assets/openapi.json
/// so the MkDocs site can embed Swagger UI. Run from repo root: set TORRENTARR_EXPORT_OPENAPI=1 and
/// run this test (e.g. dotnet test --filter "FullyQualifiedName~ExportOpenApiSpec").
/// </summary>
[Collection("HostWeb")]
public class ExportOpenApiSpecTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public ExportOpenApiSpecTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SwaggerJson_IsServed()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"openapi\"");
        json.Should().Contain("\"paths\"");

        // If export env var is set, write to docs/assets/openapi.json (run from repo root)
        var exportPath = Environment.GetEnvironmentVariable("TORRENTARR_EXPORT_OPENAPI");
        if (string.IsNullOrEmpty(exportPath))
            return;

        var repoRoot = FindRepoRoot();
        if (repoRoot == null)
            return;

        var outPath = Path.Combine(repoRoot, "docs", "assets", "openapi.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, json);
        // Optional: write to the path specified by env var if it's a path
        if (exportPath != "1" && exportPath != "true")
        {
            var dir = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(exportPath, json);
        }
    }

    private static string? FindRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "docs")) && Directory.Exists(Path.Combine(dir, "src")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
