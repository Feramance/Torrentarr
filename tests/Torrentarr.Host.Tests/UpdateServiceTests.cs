using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Host;
using Xunit;

namespace Torrentarr.Host.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("latest", "latest")]
    [InlineData("stable", "stable")]
    [InlineData("nightly", "nightly")]
    [InlineData("beta", "latest")]
    public void AutoUpdateChannel_NormalizesConfiguredValue(string raw, string expected)
    {
        var config = new TorrentarrConfig();
        config.Settings.AutoUpdateChannel = raw;
        var svc = new UpdateService(NullLogger<UpdateService>.Instance, config);

        svc.AutoUpdateChannel.Should().Be(expected);
    }

    [Fact]
    public async Task ApplyUpdateAsync_SourceBuild_SetsErrorWithoutApplying()
    {
        var prev = Environment.GetEnvironmentVariable("TORRENTARR_SOURCE_BUILD");
        try
        {
            Environment.SetEnvironmentVariable("TORRENTARR_SOURCE_BUILD", "true");
            var config = new TorrentarrConfig();
            var svc = new UpdateService(NullLogger<UpdateService>.Instance, config);

            UpdateService.IsSourceBuild().Should().BeTrue();
            await svc.ApplyUpdateAsync(lifetime: null!, CancellationToken.None);

            svc.ApplyState.LastResult.Should().Be("error");
            svc.ApplyState.LastError.Should().Contain("source");
            svc.CanApplyBinaries.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TORRENTARR_SOURCE_BUILD", prev);
        }
    }

    [Fact]
    public async Task ApplyUpdateAsync_NightlyChannel_RefusesWhenNotSourceBuildEnv()
    {
        // This workspace typically has a .git directory, so source-build detection wins first.
        // Assert the nightly message when TORRENTARR_SOURCE_BUILD is unset only if IsSourceBuild is false.
        var config = new TorrentarrConfig();
        config.Settings.AutoUpdateChannel = "nightly";
        var svc = new UpdateService(NullLogger<UpdateService>.Instance, config);
        svc.AutoUpdateChannel.Should().Be("nightly");

        if (UpdateService.IsSourceBuild())
        {
            svc.CanApplyBinaries.Should().BeFalse();
            return;
        }

        await svc.ApplyUpdateAsync(lifetime: null!, CancellationToken.None);
        svc.ApplyState.LastResult.Should().Be("error");
        svc.ApplyState.LastError.Should().Contain("Nightly");
    }

    [Theory]
    [InlineData("v6.14.3-2", true)]
    [InlineData("6.14.3-1", true)]
    [InlineData("6.14.3", false)]
    [InlineData("v6.14.3", false)]
    [InlineData("6.14.3-rc.1", false)]
    [InlineData("", false)]
    public void IsWeeklyBuildTag_DetectsNumericBuildSuffix(string tag, bool expected)
    {
        UpdateService.IsWeeklyBuildTag(tag).Should().Be(expected);
    }

    [Theory]
    [InlineData("latest", false, "6.14.3-2", false)]
    [InlineData("stable", false, "6.14.3-2", true)]
    [InlineData("stable", false, "v6.14.3", false)]
    [InlineData("stable", true, "v6.14.3", true)]
    [InlineData("nightly", true, "6.14.3-2", false)]
    public void SkipReleaseForChannel_StableSkipsPrereleaseAndWeeklyBuilds(
        string channel, bool prerelease, string tag, bool skip)
    {
        UpdateService.SkipReleaseForChannel(channel, prerelease, tag).Should().Be(skip);
    }

    [Theory]
    [InlineData("6.14.3-2", "6.14.3-1", true)]
    [InlineData("6.14.3-1", "6.14.3", true)]
    [InlineData("6.14.4-1", "6.14.3-9", true)]
    [InlineData("6.14.3-1", "6.14.3-1", false)]
    [InlineData("6.14.3-1", "6.14.3-2", false)]
    [InlineData("v6.14.3-1", "6.14.3-1", false)]
    public void IsNewerVersion_UnderstandsBuildChannelTags(string latest, string current, bool expected)
    {
        UpdateService.IsNewerVersion(latest, current).Should().Be(expected);
    }
}
