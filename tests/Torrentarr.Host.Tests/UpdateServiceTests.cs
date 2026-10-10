using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Host;
using Xunit;

namespace Torrentarr.Host.Tests;

public class UpdateServiceTests
{
    [Fact]
    public void AssetPatterns_MatchPublishedNames()
    {
        UpdateService.GetAssetPattern(System.Runtime.InteropServices.OSPlatform.Windows, System.Runtime.InteropServices.Architecture.X64).Should().Be("windows-x64");
        UpdateService.GetAssetPattern(System.Runtime.InteropServices.OSPlatform.OSX, System.Runtime.InteropServices.Architecture.Arm64).Should().Be("macos-arm64");
        UpdateService.GetAssetPattern(System.Runtime.InteropServices.OSPlatform.Linux, System.Runtime.InteropServices.Architecture.X64).Should().Be("linux-x64");
    }

    [Fact]
    public void WindowsUpdate_UsesInstalledHostNameAndWaitsForPid()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"torrentarr-windows-update-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "torrentarr-windows-x64.exe"), "host");
            File.WriteAllText(Path.Combine(dir, "torrentarr-workers-windows-x64.exe"), "worker");
            var script = UpdateService.PrepareWindowsUpdateScript(Path.Combine(dir, "custom.exe"), dir, dir, 12345);
            File.ReadAllText(Path.Combine(dir, "custom.exe")).Should().Be("host");
            File.Exists(Path.Combine(dir, "torrentarr-workers-windows-x64.exe")).Should().BeTrue();
            script.Should().Contain("PID eq 12345").And.Contain("findstr /c:\"12345\"");
            script.Should().Contain("|| exit /b 1");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task UnixUpdate_ReplacesRunningWorkerByRename()
    {
        if (!OperatingSystem.IsLinux()) return;
        var dir = Path.Combine(Path.GetTempPath(), $"torrentarr-update-test-{Guid.NewGuid():N}");
        var extracted = Path.Combine(dir, "extracted");
        Directory.CreateDirectory(extracted);
        var worker = Path.Combine(dir, "torrentarr-workers-linux-x64");
        File.Copy("/bin/sleep", worker);
        File.SetUnixFileMode(worker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var process = System.Diagnostics.Process.Start(worker, "60")!;
        try
        {
            var host = Path.Combine(dir, "torrentarr");
            await File.WriteAllTextAsync(host, "old host");
            await File.WriteAllTextAsync(Path.Combine(extracted, "torrentarr-linux-x64"), "new host");
            await File.WriteAllTextAsync(Path.Combine(extracted, Path.GetFileName(worker)), "new worker");
            await UpdateService.ApplyUnixUpdateAsync(host, dir, extracted);
            (await File.ReadAllTextAsync(worker)).Should().Be("new worker");
            (await File.ReadAllTextAsync(host)).Should().Be("new host");
            process.HasExited.Should().BeFalse();
            File.GetUnixFileMode(worker).Should().HaveFlag(UnixFileMode.UserExecute);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
            Directory.Delete(dir, true);
        }
    }

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
    [InlineData("6.14.3-9", true)]
    [InlineData("6.14.3-1", false)]
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
    [InlineData("stable", false, "6.14.3-1", false)]
    [InlineData("stable", false, "v6.14.3", false)]
    [InlineData("stable", true, "v6.14.3-1", true)]
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
