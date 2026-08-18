using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class TorrentProcessorAutoDeleteTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly TorrentarrDbContext _db;

    public TorrentProcessorAutoDeleteTests()
    {
        var cs = $"Data Source=tproc-ad-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(cs);
        _keepAlive.Open();
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseSqlite(cs).Options;
        _db = new TorrentarrDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _keepAlive.Dispose();
    }

    private TorrentProcessor CreateProcessor(
        IArrImportService? import = null,
        IMediaValidationService? media = null)
    {
        return new TorrentProcessor(
            NullLogger<TorrentProcessor>.Instance,
            new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance),
            _db,
            new TorrentarrConfig(),
            new TorrentCacheService(NullLogger<TorrentCacheService>.Instance),
            new DatabaseRestartCoordinator(),
            import,
            mediaValidation: media);
    }

    [Fact]
    public async Task AutoDeleteOff_DoesNotProbeOrBlocklist()
    {
        var media = new Mock<IMediaValidationService>(MockBehavior.Strict);
        var import = new Mock<IArrImportService>(MockBehavior.Strict);
        var processor = CreateProcessor(import.Object, media.Object);
        var dir = Path.Combine(Path.GetTempPath(), "torrentarr-ad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            await processor.RunPostImportAutoDeleteCleanupAsync(
                new TorrentInfo { Hash = "abc", Name = "movie", Category = "movies", ContentPath = dir },
                new ArrInstanceConfig { Category = "movies", Torrent = { AutoDelete = false } },
                dir,
                CancellationToken.None);

            Directory.Exists(dir).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AutoDeleteOn_ZeroValidMedia_BlocklistsAndDeletesFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "torrentarr-ad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "junk.txt"), "x");

        var media = new Mock<IMediaValidationService>();
        media.Setup(m => m.ValidateDirectoryAsync(
                dir,
                It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyCollection<string>?>()))
            .ReturnsAsync(new DirectoryValidationResult { DirectoryPath = dir, ValidFiles = 0 });

        var import = new Mock<IArrImportService>();
        import.Setup(i => i.BlocklistAndReSearchAsync("abc", "movies", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var processor = CreateProcessor(import.Object, media.Object);

        await processor.RunPostImportAutoDeleteCleanupAsync(
            new TorrentInfo { Hash = "abc", Name = "movie", Category = "movies", ContentPath = dir },
            new ArrInstanceConfig { Category = "movies", Torrent = { AutoDelete = true } },
            dir,
            CancellationToken.None);

        import.Verify(i => i.BlocklistAndReSearchAsync("abc", "movies", It.IsAny<CancellationToken>()), Times.Once);
        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public async Task AutoDeleteOn_ValidMedia_LeavesFilesAndSkipsBlocklist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "torrentarr-ad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "movie.mkv"), "x");

        var media = new Mock<IMediaValidationService>();
        media.Setup(m => m.ValidateDirectoryAsync(
                dir,
                It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyCollection<string>?>()))
            .ReturnsAsync(new DirectoryValidationResult { DirectoryPath = dir, ValidFiles = 1 });

        var import = new Mock<IArrImportService>(MockBehavior.Strict);
        var processor = CreateProcessor(import.Object, media.Object);

        try
        {
            await processor.RunPostImportAutoDeleteCleanupAsync(
                new TorrentInfo { Hash = "abc", Name = "movie", Category = "movies", ContentPath = dir },
                new ArrInstanceConfig { Category = "movies", Torrent = { AutoDelete = true } },
                dir,
                CancellationToken.None);

            Directory.Exists(dir).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
