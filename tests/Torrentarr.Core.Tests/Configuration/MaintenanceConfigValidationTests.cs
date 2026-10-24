using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class MaintenanceConfigValidationTests
{
    [Fact]
    public void NullMaintenance_ReturnsValidationError()
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["one"] = new() { Maintenance = null! };
        ConfigValidationHelper.ValidateTorrentClients(config).Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("relative", false)]
    [InlineData("/downloads/../etc", false)]
    [InlineData("/downloads", true)]
    [InlineData("C:\\Downloads", true)]
    public void Mapping_RequiresAbsolutePathsWithoutTraversal(string clientPath, bool valid)
    {
        var config = new MaintenanceConfig { PathMappings = [new() { ClientPath = clientPath, LocalPath = Path.GetTempPath() }] };
        (MaintenanceConfigValidation.Validate(config) == null).Should().Be(valid);
    }

    [Fact]
    public void Mappings_RejectNormalizedDuplicates()
    {
        var config = new MaintenanceConfig { PathMappings = [new() { ClientPath = "C:\\Downloads", LocalPath = Path.GetTempPath() }, new() { ClientPath = "c:/downloads/", LocalPath = Path.GetTempPath() }] };
        MaintenanceConfigValidation.Validate(config).Should().Contain("duplicates");
    }

}
