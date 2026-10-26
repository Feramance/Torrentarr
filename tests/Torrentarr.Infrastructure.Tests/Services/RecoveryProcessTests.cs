using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Infrastructure.Database;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class RecoveryProcessTests
{
    [Fact]
    public async Task IndependentProcessesClaimOneRecheck_AndRestartCannotRepeatIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "torrentarr-claim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "claim.db");
        var gate = Path.Combine(root, "gate");
        using (var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options)) db.Database.EnsureCreated();
        Process Start(string id)
        {
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { typeof(RecoveryProcessTests).Assembly.Location, "--recovery-claim", database, gate, id }) info.ArgumentList.Add(argument);
            return Process.Start(info)!;
        }
        var children = new List<Process>();
        try
        {
            children.Add(Start("one")); children.Add(Start("two"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(gate + "one.ready") || !File.Exists(gate + "two.ready"))
            {
                foreach (var child in children.Where(c => c.HasExited)) child.ExitCode.Should().Be(0, await child.StandardError.ReadToEndAsync(timeout.Token));
                await Task.Delay(20, timeout.Token);
            }
            File.WriteAllText(gate + ".start", "start");
            await Task.WhenAll(children.Select(c => c.WaitForExitAsync(timeout.Token)));
            foreach (var child in children) child.ExitCode.Should().Be(0, await child.StandardError.ReadToEndAsync(timeout.Token));
            var outcomes = await Task.WhenAll(children.Select(c => c.StandardOutput.ReadToEndAsync(timeout.Token)));
            outcomes.Count(s => s.Trim() == "accepted").Should().Be(1);
            outcomes.Count(s => s.Trim() == "blocked").Should().Be(1);
            var restarted = Start("restart"); children.Add(restarted);
            await restarted.WaitForExitAsync(timeout.Token);
            restarted.ExitCode.Should().Be(0, await restarted.StandardError.ReadToEndAsync(timeout.Token));
            (await restarted.StandardOutput.ReadToEndAsync(timeout.Token)).Trim().Should().Be("blocked");
        }
        finally
        {
            foreach (var child in children) { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } child.Dispose(); }
            Directory.Delete(root, true);
        }
    }
}
