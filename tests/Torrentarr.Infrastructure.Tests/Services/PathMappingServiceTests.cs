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
        var root = TestPaths.TempPath;
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
        var mappings = new List<PathMappingConfig> { new() { ClientPath = "/downloads", LocalPath = TestPaths.TempPath } };
        new PathMappingService().MapToLocal("/downloads/../../escape", mappings).Should().BeNull();
    }

    [Theory]
    [InlineData("/downloads/movie/file.mkv", "/downloads", "movie/file.mkv")]
    [InlineData("/movie.mkv", "/", "movie.mkv")]
    [InlineData("C:/movie.mkv", "C:/", "movie.mkv")]
    [InlineData(@"C:\Downloads\Movie\file.mkv", "c:/downloads", "Movie/file.mkv")]
    [InlineData(@"/downloads\Movie/file.mkv", "/downloads", "Movie/file.mkv")]
    public void Mapping_NormalizesSeparatorsAndPreservesRoots(string client, string prefix, string relative)
    {
        var root = TestPaths.TempPath;
        var mapped = new PathMappingService().Resolve(client, [new() { ClientPath = prefix, LocalPath = root }]);
        mapped.Code.Should().BeNull();
        mapped.LocalPath.Should().Be(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Theory]
    [InlineData("/downloads-other/file.mkv", "unmatched-prefix")]
    [InlineData("/downloads/../file.mkv", "unsafe-path")]
    [InlineData("relative/file.mkv", "unsafe-path")]
    public void Mapping_ExplainsRejectedPaths(string client, string code)
    {
        var result = new PathMappingService().Resolve(client, [new() { ClientPath = "/downloads", LocalPath = TestPaths.TempPath }]);
        result.LocalPath.Should().BeNull();
        result.Code.Should().Be(code);
    }

    [Fact]
    public void Mapping_RequiresExplicitMappingsEvenForExistingSamePath()
    {
        var paths = new PathMappingService();
        var root = TestPaths.TempPath;
        paths.Resolve(root, []).Code.Should().Be("missing-mapping");
        paths.Resolve(root, [new() { ClientPath = root, LocalPath = root }]).LocalPath.Should().Be(Path.GetFullPath(root.TrimEnd(Path.DirectorySeparatorChar)));
        paths.Resolve("/downloads/file", [new() { ClientPath = "/downloads", LocalPath = "relative" }]).Code.Should().Be("invalid-mapping");
    }

    [Fact]
    public void Mapping_RejectsAmbiguousPrefixes()
    {
        new PathMappingService().Resolve("/downloads/file", [
            new() { ClientPath = "/downloads", LocalPath = TestPaths.TempPath },
            new() { ClientPath = "/downloads/", LocalPath = TestPaths.TempPath }
        ]).Code.Should().Be("invalid-mapping");
    }

    [Fact]
    public void LocalValidation_SeparatesMissingMountFromMissingFile()
    {
        var paths = new PathMappingService();
        var root = Path.Combine(TestPaths.TempPath, Guid.NewGuid().ToString("N"));
        paths.ValidateLocalPath(root, root).Should().Be("inaccessible-local-root");
        paths.ValidateLocalPath(Path.Combine(TestPaths.TempPath, Guid.NewGuid().ToString("N")), TestPaths.TempPath, true).Should().Be("missing-file");
        paths.IsSafeDescendant(Path.Combine(Path.GetPathRoot(root)!, "child"), Path.GetPathRoot(root)!).Should().BeTrue();
    }

    [Fact]
    public void LocalValidation_RejectsSymlinkAncestors()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(TestPaths.TempPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "target"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "target"));
            var paths = new PathMappingService();
            paths.ValidateLocalPath(Path.Combine(root, "link", "file"), root).Should().Be("unsafe-path");
            paths.ValidateLocalPath(Path.Combine(root, "link"), Path.Combine(root, "link")).Should().Be("unsafe-path");
        }
        finally { Directory.Delete(root, true); }
    }

}
