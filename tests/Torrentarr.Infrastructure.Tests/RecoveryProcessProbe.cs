using Microsoft.EntityFrameworkCore;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Infrastructure.Tests;

internal static class RecoveryProcessProbe
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--recovery-claim") return 2;
        try
        {
            using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite($"Data Source={args[1]};Pooling=False").Options);
            var client = new Mock<ITorrentClient>();
            client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
            client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            client.Setup(c => c.RecheckTorrentsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            File.WriteAllText(args[2] + args[3] + ".ready", "ready");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(args[2] + ".start")) await Task.Delay(20, timeout.Token);
            var accepted = await MaintenanceRecovery.RequestAsync(db, client.Object,
                new() { ClientInstanceId = "one", Hash = "same", Name = "probe", AddedOn = 1 }, "ErrorRecovery", new RecoveryConfig(), timeout.Token);
            Console.WriteLine(accepted ? "accepted" : "blocked");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
