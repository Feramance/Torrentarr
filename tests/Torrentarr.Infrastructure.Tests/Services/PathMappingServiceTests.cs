using FluentAssertions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class PathMappingServiceTests
{
    [Fact]
    public void MapToLocal_UsesLongestMatchingClientPrefix()
    {
        var root = Path.GetTempPath();
        var nested = Path.Combine(root, "specific");
        var mappings = new List<PathMappingConfig>
        {
            new() { ClientPath = "/downloads", LocalPath = root },
            new() { ClientPath = "/downloads/movies", LocalPath = nested }
        };
        var mapped = new PathMappingService().MapToLocal("/downloads/movies/title/file.mkv", mappings);
        mapped.Should().Be(Path.GetFullPath(Path.Combine(nested, "title", "file.mkv")));
    }

    [Fact]
    public void MapToLocal_RejectsTraversalOutsideRoot()
    {
        var mappings = new List<PathMappingConfig> { new() { ClientPath = "/downloads", LocalPath = Path.GetTempPath() } };
        new PathMappingService().MapToLocal("/downloads/../../escape", mappings).Should().BeNull();
    }
}
