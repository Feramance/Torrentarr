using System.Diagnostics;
using FluentAssertions;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Host.Tests;

[Collection("HostWeb")]
public class InformationalCommandTests
{
    [Theory]
    [InlineData("--help", null, "Usage:")]
    [InlineData("-h", null, "Usage:")]
    [InlineData("--version", null, "Torrentarr")]
    [InlineData("-v", null, "Torrentarr")]
    [InlineData("--source", null, "https://github.com/Feramance/Torrentarr")]
    [InlineData("-s", null, "https://github.com/Feramance/Torrentarr")]
    [InlineData("--license", null, "MIT License")]
    [InlineData("-l", null, "MIT License")]
    [InlineData("--help", "unexpected", null)]
    [InlineData("--version", "unexpected", null)]
    [InlineData("--source", "unexpected", null)]
    [InlineData("--license", "unexpected", null)]
    public async Task InformationalCommands_ExitWithoutInitializingHost(string option, string? extra, string? expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "host-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "config.toml");
        File.WriteAllText(config, "invalid TOML [");
        File.WriteAllText(config + ".rename.json", "pending recovery");
        try
        {
            using var ownership = HostConfigOwnership.Acquire(config);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(ProcessOrchestratorService).Assembly.Location);
            start.ArgumentList.Add(option);
            if (extra != null) start.ArgumentList.Add(extra);
            start.Environment["TORRENTARR_CONFIG"] = config;
            start.Environment["TORRENTARR_OVERRIDES_DATA_PATH"] = root;
            using var process = Process.Start(start)!;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(timeout.Token);
                process.ExitCode.Should().Be(extra == null ? 0 : 1);
                if (expected != null) (await output).Should().Contain(expected);
                else (await error).Should().Contain("do not accept additional arguments");
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            File.ReadAllText(config).Should().Be("invalid TOML [");
            File.ReadAllText(config + ".rename.json").Should().Be("pending recovery");
            Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Should()
                .BeEquivalentTo(["config.toml", "config.toml.rename.json", ".torrentarr-host.lock"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
