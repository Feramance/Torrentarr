using System.Reflection;
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

public sealed class TorrentProcessorImportAllowlistTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TorrentarrDbContext _db;

    public TorrentProcessorImportAllowlistTests()
    {
        _connection = new SqliteConnection($"Data Source=allowlist-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        _connection.Open();
        _db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task PriorityFailure_RemainsPendingForRetry()
    {
        var client = new FileClient([TorrentFile(0, "Movie/movie.mkv"), TorrentFile(1, "Movie/sample.txt")])
        {
            PriorityResult = false
        };

        var result = await ApplyAsync(CreateProcessor(), Torrent("/downloads/Movie"), Config(autoDelete: false), client, completed: false);

        Ready(result).Should().BeFalse();
        client.PriorityCalls.Should().Be(1);
    }

    [Fact]
    public async Task PriorityException_RemainsPendingForRetry()
    {
        var client = new FileClient([TorrentFile(0, "Movie/movie.mkv"), TorrentFile(1, "Movie/sample.txt")])
        {
            PriorityException = new IOException("qBit unavailable")
        };

        var result = await ApplyAsync(CreateProcessor(), Torrent("/downloads/Movie"), Config(autoDelete: false), client, completed: false);

        Ready(result).Should().BeFalse();
    }

    [Fact]
    public async Task CompletedTorrent_AutoDelete_RemovesOnlyOwnedDisallowedFiles()
    {
        var root = Directory.CreateTempSubdirectory("torrentarr-allowlist-");
        try
        {
            var content = Directory.CreateDirectory(Path.Combine(root.FullName, "Movie"));
            var allowed = Path.Combine(content.FullName, "movie.mkv");
            var disallowed = Path.Combine(content.FullName, "sample.txt");
            var sibling = Path.Combine(root.FullName, "unrelated.txt");
            await File.WriteAllTextAsync(allowed, "video");
            await File.WriteAllTextAsync(disallowed, "sample");
            await File.WriteAllTextAsync(sibling, "keep");
            var client = new FileClient([TorrentFile(0, "Movie/movie.mkv"), TorrentFile(1, "Movie/sample.txt")]);

            var result = await ApplyAsync(CreateProcessor(), Torrent(content.FullName), Config(autoDelete: true), client, completed: true);

            Ready(result).Should().BeTrue();
            File.Exists(allowed).Should().BeTrue();
            File.Exists(disallowed).Should().BeFalse();
            File.Exists(sibling).Should().BeTrue();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CompletedTorrent_WithoutAutoDelete_BlocksImportWhileDisallowedFileRemains()
    {
        var root = Directory.CreateTempSubdirectory("torrentarr-no-delete-");
        try
        {
            var content = Directory.CreateDirectory(Path.Combine(root.FullName, "Movie"));
            await File.WriteAllTextAsync(Path.Combine(content.FullName, "sample.txt"), "sample");
            var client = new FileClient([TorrentFile(0, "Movie/movie.mkv"), TorrentFile(1, "Movie/sample.txt")]);

            var result = await ApplyAsync(CreateProcessor(), Torrent(content.FullName), Config(autoDelete: false), client, completed: true);

            Ready(result).Should().BeFalse();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CompletedTorrent_WithoutAutoDelete_IsReadyWhenDisallowedFileWasNeverWritten()
    {
        var root = Directory.CreateTempSubdirectory("torrentarr-no-file-");
        try
        {
            var content = Directory.CreateDirectory(Path.Combine(root.FullName, "Movie"));
            var client = new FileClient([TorrentFile(0, "Movie/movie.mkv"), TorrentFile(1, "Movie/sample.txt")]);

            var result = await ApplyAsync(CreateProcessor(), Torrent(content.FullName), Config(autoDelete: false), client, completed: true);

            Ready(result).Should().BeTrue();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AuxiliaryFiles_DoNotBlockAnOtherwiseAllowedTorrent()
    {
        var client = new FileClient([
            TorrentFile(0, "Movie/movie.mkv"),
            TorrentFile(1, "Movie/.DS_Store"),
            TorrentFile(2, "Movie/desktop.ini"),
            TorrentFile(3, "Movie/.parts")]);

        var result = await ApplyAsync(CreateProcessor(), Torrent("/downloads/Movie"), Config(autoDelete: false), client, completed: true);

        Ready(result).Should().BeTrue();
        client.PriorityCalls.Should().Be(0);
    }

    [Fact]
    public async Task AllFilesExcluded_WhenHnrBlocksDeletion_RemainsPending()
    {
        var torrent = Torrent("/downloads/Movie");
        var seeding = new Mock<ISeedingService>();
        seeding.Setup(s => s.HnrAllowsDeleteAsync(torrent, "all files excluded by import allowlist", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var client = new FileClient([TorrentFile(0, "Movie/sample.txt")]);

        var result = await ApplyAsync(CreateProcessor(seeding.Object), torrent, Config(autoDelete: true), client, completed: true);

        Ready(result).Should().BeFalse();
        Deleted(result).Should().BeFalse();
        seeding.VerifyAll();
    }

    [Fact]
    public async Task AllowlistWarning_IsDeduplicatedForFiveMinutesThenExpires()
    {
        var torrent = Torrent("/downloads/Movie");
        var seeding = new Mock<ISeedingService>();
        seeding.Setup(s => s.HnrAllowsDeleteAsync(torrent, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var cache = new TorrentCacheService(NullLogger<TorrentCacheService>.Instance);
        var firstProcessor = CreateProcessor(seeding.Object, cache);
        var secondProcessor = CreateProcessor(seeding.Object, cache);
        var client = new FileClient([TorrentFile(0, "Movie/sample.txt")]);
        var started = DateTimeOffset.UtcNow;

        await ApplyAsync(firstProcessor, torrent, Config(autoDelete: false), client, completed: true);
        cache.ShouldLogAllowlistWarning("qBit", torrent.Hash, started.AddMinutes(1), TimeSpan.FromMinutes(5))
            .Should().BeFalse();

        await ApplyAsync(secondProcessor, torrent, Config(autoDelete: false), client, completed: true);
        cache.ShouldLogAllowlistWarning("qBit", torrent.Hash, started.AddMinutes(6), TimeSpan.FromMinutes(5))
            .Should().BeTrue();
    }

    [Fact]
    public void SingleFileResolution_DoesNotSelectSiblingWithQbitFileName()
    {
        var root = Directory.CreateTempSubdirectory("torrentarr-single-");
        try
        {
            var content = Path.Combine(root.FullName, "actual-download.mkv");
            var sibling = Path.Combine(root.FullName, "name-from-qbit.txt");
            File.WriteAllText(content, "content");
            File.WriteAllText(sibling, "sibling");
            var torrent = Torrent(content);
            var method = typeof(TorrentProcessor).GetMethod("ResolveTorrentOwnedPath", BindingFlags.NonPublic | BindingFlags.Static)!;

            var resolved = (string)method.Invoke(null, [torrent, "name-from-qbit.txt"])!;

            resolved.Should().Be(Path.GetFullPath(content));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AutoDelete_PathTraversal_RemainsBlockedAndDoesNotDeleteOutsideContentRoot()
    {
        var root = Directory.CreateTempSubdirectory("torrentarr-traversal-");
        try
        {
            var content = Directory.CreateDirectory(Path.Combine(root.FullName, "Movie"));
            var allowed = Path.Combine(content.FullName, "movie.mkv");
            var outside = Path.Combine(root.FullName, "outside.txt");
            await File.WriteAllTextAsync(allowed, "video");
            await File.WriteAllTextAsync(outside, "keep");
            var client = new FileClient([
                TorrentFile(0, "Movie/movie.mkv"),
                TorrentFile(1, "../outside.txt")]);

            var result = await ApplyAsync(CreateProcessor(), Torrent(content.FullName), Config(autoDelete: true), client, completed: true);

            Ready(result).Should().BeFalse();
            File.Exists(outside).Should().BeTrue();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AutoDelete_IntermediateSymlink_RemainsBlockedAndDoesNotDeleteTarget()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Directory.CreateTempSubdirectory("torrentarr-symlink-");
        try
        {
            var content = Directory.CreateDirectory(Path.Combine(root.FullName, "Movie"));
            var outside = Directory.CreateDirectory(Path.Combine(root.FullName, "outside"));
            var target = Path.Combine(outside.FullName, "sample.txt");
            await File.WriteAllTextAsync(target, "keep");
            Directory.CreateSymbolicLink(Path.Combine(content.FullName, "linked"), outside.FullName);
            var client = new FileClient([
                TorrentFile(0, "Movie/movie.mkv"),
                TorrentFile(1, "Movie/linked/sample.txt")]);

            var result = await ApplyAsync(CreateProcessor(), Torrent(content.FullName), Config(autoDelete: true), client, completed: true);

            Ready(result).Should().BeFalse();
            File.Exists(target).Should().BeTrue();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private TorrentProcessor CreateProcessor(
        ISeedingService? seeding = null,
        ITorrentCacheService? cache = null) => new(
        NullLogger<TorrentProcessor>.Instance,
        new QBittorrentConnectionManager(NullLogger<QBittorrentConnectionManager>.Instance),
        _db,
        new TorrentarrConfig(),
        cache ?? new TorrentCacheService(NullLogger<TorrentCacheService>.Instance),
        new DatabaseRestartCoordinator(),
        seedingService: seeding);

    private static ArrInstanceConfig Config(bool autoDelete) => new()
    {
        Torrent =
        {
            FolderExclusionRegex = [],
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = [".mkv"],
            AutoDelete = autoDelete
        }
    };

    private static TorrentInfo Torrent(string contentPath) => new()
    {
        Hash = "allowlist-hash",
        Name = "Movie",
        Category = "radarr",
        ContentPath = contentPath,
        SavePath = Path.GetDirectoryName(contentPath) ?? contentPath,
        QBitInstanceName = "qBit"
    };

    private static TorrentFile TorrentFile(int index, string name) => new() { Index = index, Name = name };

    private static async Task<object> ApplyAsync(
        TorrentProcessor processor,
        TorrentInfo torrent,
        ArrInstanceConfig config,
        QBittorrentClient client,
        bool completed)
    {
        var method = typeof(TorrentProcessor).GetMethod("ApplyFileFilterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(processor, [torrent, config, client, completed, CancellationToken.None])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static bool Ready(object result) => (bool)result.GetType().GetProperty("Ready")!.GetValue(result)!;
    private static bool Deleted(object result) => (bool)result.GetType().GetProperty("Deleted")!.GetValue(result)!;

    private sealed class FileClient(List<TorrentFile> files) : QBittorrentClient("127.0.0.1", 1, "u", "p")
    {
        public bool PriorityResult { get; init; } = true;
        public Exception? PriorityException { get; init; }
        public int PriorityCalls { get; private set; }

        public override Task<List<TorrentFile>> GetTorrentFilesAsync(string hash, CancellationToken ct = default) =>
            Task.FromResult(files);

        public override Task<bool> SetFilePriorityAsync(string hash, int[] fileIds, int priority, CancellationToken ct = default)
        {
            PriorityCalls++;
            if (PriorityException != null)
                throw PriorityException;
            return Task.FromResult(PriorityResult);
        }
    }
}
