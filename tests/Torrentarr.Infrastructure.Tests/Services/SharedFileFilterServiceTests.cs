using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class SharedFileFilterServiceTests
{
    [Fact]
    public async Task RefreshActivatesWholeListForBothInstancesAndRetainsLastValidOnFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "reviewed.txt");
            var config = new TorrentarrConfig();
            config.Settings.SharedFileFilters = [new() { Name = "reviewed", Source = "reviewed.txt" }];
            var first = new TorrentConfig { SharedFileFilters = ["reviewed"], FileNameExclusionRegex = ["local-first"] };
            var second = new TorrentConfig { SharedFileFilters = ["reviewed"], FileNameExclusionRegex = ["local-second"] };
            config.ArrInstances["Sonarr-TV"] = new() { Torrent = first };
            config.ArrInstances["Radarr-Movies"] = new() { Torrent = second };
            var issues = new Mock<IOperationalIssueReporter>();
            using var service = new SharedFileFilterService(config, issues.Object, NullLogger<SharedFileFilterService>.Instance);
            service.Available(first).Should().BeFalse();
            await service.RefreshAsync(default, directory);
            service.Available(first).Should().BeFalse();
            issues.Verify(reporter => reporter.ReportAsync(It.IsAny<OperationalIssueReport>(), default), Times.Once);

            await File.WriteAllTextAsync(path, "\\.exe$");
            await service.RefreshAsync(default, directory);
            foreach (var torrent in new[] { first, second })
            {
                service.Available(torrent).Should().BeTrue();
                service.Match("Movie/extra.EXE", torrent).Should().Contain("reviewed");
                service.Match("Movie/video.mkv", torrent).Should().BeNull();
            }
            FileFilterMatcher.LocalReason("Movie/local-first.mkv", first, 0).Should().NotBeNull();
            FileFilterMatcher.LocalReason("Movie/local-second.mkv", second, 0).Should().NotBeNull();
            service.Status.Single().Error.Should().BeNull();
            var loaded = service.Status.Single().LoadedAt;

            await File.WriteAllTextAsync(path, "\\.jpg$\n[");
            await service.RefreshAsync(default, directory);
            service.Status.Single().LoadedAt.Should().Be(loaded);
            service.Status.Single().Error.Should().NotBeNull();
            service.Match("Movie/extra.exe", first).Should().NotBeNull();
            service.Match("Movie/extra.jpg", second).Should().BeNull();

            await File.WriteAllTextAsync(path, "\\.jpg$");
            await service.RefreshAsync(default, directory);
            service.Match("Movie/extra.exe", first).Should().BeNull();
            service.Match("Movie/extra.jpg", second).Should().NotBeNull();
            service.Status.Single().Error.Should().BeNull();

            config.Settings.SharedFileFilters[0].Source = "another.txt";
            service.Available(first).Should().BeFalse();
            await service.RefreshAsync(default, directory);
            service.Available(first).Should().BeFalse();
            service.Status.Single().LoadedAt.Should().BeNull();
        }
        finally { Directory.Delete(directory, true); }
    }
}
