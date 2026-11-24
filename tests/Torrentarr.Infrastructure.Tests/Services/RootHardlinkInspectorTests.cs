using System.Runtime.InteropServices;
using FluentAssertions;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class RootHardlinkInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "root-links-" + Guid.NewGuid().ToString("N"));
    public RootHardlinkInspectorTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void LibraryLinkIsDistinguishedFromCrossSeedOnlyLinkAndNoLink()
    {
        if (!OperatingSystem.IsLinux()) return;
        var downloads = MakeDirectory("downloads");
        var library = MakeDirectory("library");
        var crossSeed = MakeDirectory("cross-seed");
        var imported = MakeFile(downloads, "imported.mkv");
        var crossSeedOnly = MakeFile(downloads, "cross-seed.mkv");
        var unlinked = MakeFile(downloads, "unlinked.mkv");
        Link(imported, Path.Combine(library, "imported.mkv")).Should().Be(0);
        Link(crossSeedOnly, Path.Combine(crossSeed, "cross-seed.mkv")).Should().Be(0);

        var alias = Path.Combine(downloads, "..", "downloads", "imported.mkv");
        var result = RootHardlinkInspector.Inspect([imported, crossSeedOnly, unlinked, alias], [library], [crossSeed], 100, default);

        result.LibraryLinks[imported].Should().BeTrue(result.Evidence);
        result.LibraryLinks[alias].Should().BeTrue(result.Evidence);
        result.LibraryLinks[crossSeedOnly].Should().BeFalse(result.Evidence);
        result.LibraryLinks[unlinked].Should().BeFalse(result.Evidence);
        result.InspectedEntries.Should().Be(1);
    }

    [Fact]
    public void MissingRootOrBudgetExhaustionPreservesUnknownEvenWhenALinkWasSeen()
    {
        if (!OperatingSystem.IsLinux()) return;
        var downloads = MakeDirectory("downloads");
        var library = MakeDirectory("library");
        var file = MakeFile(downloads, "movie.mkv");
        Link(file, Path.Combine(library, "movie.mkv")).Should().Be(0);
        MakeFile(library, "other.mkv");
        var missing = RootHardlinkInspector.Inspect([file], [library, Path.Combine(_root, "missing")], [], 100, default);
        missing.LibraryLinks[file].Should().BeNull();
        var limited = RootHardlinkInspector.Inspect([file], [library], [], 1, default);
        limited.LibraryLinks[file].Should().BeNull();
        limited.Evidence.Should().Contain("budget");
        limited.InspectedEntries.Should().Be(1);
    }

    [Fact]
    public void NestedRootsAreScannedOnceAndIgnoredSubtreeDoesNotSupplyLibraryEvidence()
    {
        if (!OperatingSystem.IsLinux()) return;
        var downloads = MakeDirectory("downloads");
        var library = MakeDirectory("library");
        var nested = MakeDirectory("library/nested");
        var ignored = MakeDirectory("library/cross-seed");
        var file = MakeFile(downloads, "movie.mkv");
        Link(file, Path.Combine(ignored, "movie.mkv")).Should().Be(0);
        var result = RootHardlinkInspector.Inspect([file], [library, nested, library + Path.DirectorySeparatorChar], [ignored], 10, default);
        result.LibraryLinks[file].Should().BeFalse(result.Evidence);
        result.InspectedEntries.Should().Be(2);
        var invalid = RootHardlinkInspector.Inspect([file], [nested], [library], 10, default);
        invalid.LibraryLinks[file].Should().BeNull();
    }

    [Fact]
    public void SymlinksCannotGrantCoverageOrEscapeScope()
    {
        if (!OperatingSystem.IsLinux()) return;
        var library = MakeDirectory("library");
        var elsewhere = MakeDirectory("elsewhere");
        var file = MakeFile(elsewhere, "movie.mkv");
        Directory.CreateSymbolicLink(Path.Combine(library, "shortcut"), elsewhere);
        var result = RootHardlinkInspector.Inspect([file], [library], [], 10, default);
        result.LibraryLinks[file].Should().BeNull();
        result.Evidence.Should().Contain("link");
        var linkedRoot = Path.Combine(_root, "linked-root");
        Directory.CreateSymbolicLink(linkedRoot, elsewhere);
        RootHardlinkInspector.Inspect([file], [linkedRoot], [], 10, default).LibraryLinks[file].Should().BeNull();
    }

    [Fact]
    public void CancellationStopsInspection()
    {
        if (!OperatingSystem.IsLinux()) return;
        var library = MakeDirectory("library");
        var file = MakeFile(library, "movie.mkv");
        var act = () => RootHardlinkInspector.Inspect([file], [library], [], 10, new CancellationToken(true));
        act.Should().Throw<OperationCanceledException>();
    }

    private string MakeDirectory(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        return path;
    }
    private static string MakeFile(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "media");
        return path;
    }
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string source, string destination);
}
