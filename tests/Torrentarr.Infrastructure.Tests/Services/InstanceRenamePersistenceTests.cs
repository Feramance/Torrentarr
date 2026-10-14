using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class InstanceRenamePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rename-" + Guid.NewGuid().ToString("N"));
    private readonly TorrentarrDbContext _db;
    private readonly ConfigurationLoader _loader;
    private readonly TorrentarrConfig _before = new();
    private readonly TorrentarrConfig _after = new();
    private readonly JObject _renames = new() { ["qBit-before"] = "qBit-after" };
    private string ConfigPath => Path.Combine(_root, "config.toml");
    private string DbPath => Path.Combine(_root, "torrentarr.db");

    public InstanceRenamePersistenceTests()
    {
        Directory.CreateDirectory(_root);
        _db = new(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite($"Data Source={DbPath};Pooling=False").Options);
        _db.Database.EnsureCreated();
        _before.QBitInstances["qBit-before"] = new() { Host = "fixture" };
        _after.QBitInstances["qBit-after"] = new() { Host = "fixture" };
        _loader = new(ConfigPath);
        _loader.SaveConfig(_before);
        _db.TorrentLibrary.Add(new()
        {
            Id = 1,
            Hash = "abc",
            QbitInstance = "qBit-before",
            Imported = true,
            AllowedSeeding = true,
            AllowedStalled = true,
            FreeSpacePaused = true,
            Category = "movies",
            ArrInstance = "Radarr"
        });
        _db.TorrentLibrary.Add(new() { Id = 2, Hash = "abc", QbitInstance = "other", Category = "movies" });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Rename_PreservesEveryFieldAndOtherClients()
    {
        await InstanceRenamePersistence.SaveAsync(_before, _after, _renames, _loader, _db);
        var row = await _db.TorrentLibrary.AsNoTracking().SingleAsync(t => t.Id == 1);
        row.QbitInstance.Should().Be("qBit-after");
        row.Imported.Should().BeTrue(); row.AllowedSeeding.Should().BeTrue();
        row.AllowedStalled.Should().BeTrue(); row.FreeSpacePaused.Should().BeTrue();
        row.ArrInstance.Should().Be("Radarr"); row.Category.Should().Be("movies");
        (await _db.TorrentLibrary.AsNoTracking().SingleAsync(t => t.Id == 2)).QbitInstance.Should().Be("other");
        _loader.Load().QBitInstances.Should().ContainKey("qBit-after").And.NotContainKey("qBit-before");
        File.Exists(ConfigPath + ".rename.json").Should().BeFalse();
    }

    [Fact]
    public async Task ArrRename_ReachesEveryMappedTable()
    {
        _before.ArrInstances["Radarr-before"] = new();
        _after.ArrInstances["Radarr-after"] = new();
        foreach (var type in _db.Model.GetEntityTypes().Where(t => t.FindProperty("ArrInstance") != null))
        {
            var entity = Activator.CreateInstance(type.ClrType)!;
            type.ClrType.GetProperty("ArrInstance")!.SetValue(entity, "Radarr-before");
            _db.Add(entity);
        }
        await _db.SaveChangesAsync();
        var renames = new JObject { ["Radarr-before"] = "Radarr-after" };
        await InstanceRenamePersistence.SaveAsync(_before, _after, renames, _loader, _db);
        await _db.Database.OpenConnectionAsync();
        foreach (var type in _db.Model.GetEntityTypes().Where(t => t.FindProperty("ArrInstance") != null))
        {
            using var command = _db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {type.GetTableName()} WHERE arrinstance='Radarr-after'";
            Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(1, type.GetTableName());
        }
    }

    [Theory]
    [InlineData("qBit-after")]
    [InlineData("QBIT-AFTER")]
    public async Task DestinationState_RejectsWithoutChangingConfigOrRows(string destination)
    {
        _db.TorrentLibrary.Add(new() { Hash = "different", QbitInstance = destination });
        await _db.SaveChangesAsync();
        var original = File.ReadAllText(ConfigPath);
        await FluentActions.Awaiting(() => InstanceRenamePersistence.SaveAsync(_before, _after, _renames, _loader, _db))
            .Should().ThrowAsync<ArgumentException>();
        File.ReadAllText(ConfigPath).Should().Be(original);
        (await _db.TorrentLibrary.SingleAsync(t => t.Id == 1)).QbitInstance.Should().Be("qBit-before");
    }

    [Fact]
    public void CanonicalDestinationCollision_IsRejected()
    {
        _before.TorrentClients["QBIT-AFTER"] = new();
        FluentActions.Invoking(() => InstanceRenamePersistence.Validate(_before, _after, _renames)).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("case")]
    [InlineData("cycle")]
    [InlineData("duplicate")]
    [InlineData("retained")]
    public void InvalidRenameMap_IsRejected(string scenario)
    {
        var map = scenario switch
        {
            "case" => new JObject { ["qBit-before"] = "QBIT-BEFORE" },
            "cycle" => new JObject { ["qBit-before"] = "qBit-after", ["qBit-after"] = "qBit-before" },
            "duplicate" => new JObject { ["qBit-before"] = "qBit-after", ["qBit-other"] = "QBIT-AFTER" },
            _ => _renames
        };
        if (scenario == "retained") _after.QBitInstances["qBit-before"] = new();
        FluentActions.Invoking(() => InstanceRenamePersistence.Validate(_before, _after, map)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task MultipleIndependentRenames_MigrateTogether()
    {
        _before.QBitInstances["qBit-second"] = new() { Host = "second" };
        _after.QBitInstances["qBit-new"] = new() { Host = "second" };
        _renames["qBit-second"] = "qBit-new";
        _db.TorrentLibrary.Add(new() { Hash = "second", QbitInstance = "qBit-second" });
        await _db.SaveChangesAsync();
        await InstanceRenamePersistence.SaveAsync(_before, _after, _renames, _loader, _db);
        (await _db.TorrentLibrary.AsNoTracking().SingleAsync(t => t.Hash == "second")).QbitInstance.Should().Be("qBit-new");
    }

    [Fact]
    public async Task DatabaseFailure_RollsBackDirectoriesAndConfig()
    {
        var recycle = Path.Combine(_root, "recycle");
        _before.QBitInstances["qBit-before"].Maintenance.RecycleBin.Path = recycle;
        _after.QBitInstances["qBit-after"].Maintenance.RecycleBin.Path = recycle;
        Directory.CreateDirectory(Path.Combine(recycle, "qBit-before"));
        File.WriteAllText(Path.Combine(recycle, "qBit-before", "retained"), "data");
        await _db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_rename BEFORE UPDATE ON torrentlibrary BEGIN SELECT RAISE(ABORT, 'rejected'); END;");
        var original = File.ReadAllText(ConfigPath);
        await FluentActions.Awaiting(() => InstanceRenamePersistence.SaveAsync(_before, _after, _renames, _loader, _db))
            .Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
        File.ReadAllText(ConfigPath).Should().Be(original);
        File.ReadAllText(Path.Combine(recycle, "qBit-before", "retained")).Should().Be("data");
        Directory.Exists(Path.Combine(recycle, "qBit-after")).Should().BeFalse();
        File.Exists(ConfigPath + ".rename.json").Should().BeFalse();
    }

    [Fact]
    public async Task SuspensionFailure_PrecedesAllPersistenceChanges()
    {
        var original = File.ReadAllText(ConfigPath);
        await FluentActions.Awaiting(() => InstanceRenamePersistence.SaveAsync(_before, _after, _renames, _loader, _db,
            suspend: () => throw new TimeoutException("worker did not stop"))).Should().ThrowAsync<TimeoutException>();
        File.ReadAllText(ConfigPath).Should().Be(original);
        File.Exists(ConfigPath + ".rename.json").Should().BeFalse();
        _db.TorrentLibrary.Single(t => t.Id == 1).QbitInstance.Should().Be("qBit-before");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InterruptedRename_RecoversIdempotently(bool published, bool committed)
    {
        var oldDigest = Digest();
        _loader.SaveConfig(_after);
        var newDigest = Digest();
        if (!published) _loader.SaveConfig(_before);
        if (committed) await _db.Database.ExecuteSqlRawAsync("UPDATE torrentlibrary SET qbitinstance='qBit-after' WHERE id=1");
        var oldDirectory = Path.Combine(_root, "old"); var newDirectory = Path.Combine(_root, "new");
        Directory.CreateDirectory(newDirectory); File.WriteAllText(Path.Combine(newDirectory, "file"), "data");
        WriteJournal(new(oldDigest, newDigest, [new("torrentlibrary", "qbitinstance", "qBit-before", "qBit-after", [1])],
            [new(oldDirectory, newDirectory)]));
        await InstanceRenamePersistence.RecoverAsync(ConfigPath, DbPath);
        await InstanceRenamePersistence.RecoverAsync(ConfigPath, DbPath);
        (await _db.TorrentLibrary.AsNoTracking().SingleAsync(t => t.Id == 1)).QbitInstance.Should().Be(published ? "qBit-after" : "qBit-before");
        File.ReadAllText(Path.Combine(published ? newDirectory : oldDirectory, "file")).Should().Be("data");
        File.Exists(ConfigPath + ".rename.json").Should().BeFalse();
    }

    [Fact]
    public async Task UnexpectedConfig_BlocksRecoveryAndFurtherWrites()
    {
        var digest = Digest();
        WriteJournal(new(digest, "new", [], []));
        File.AppendAllText(ConfigPath, "\n# external change\n");
        await FluentActions.Awaiting(() => InstanceRenamePersistence.RecoverAsync(ConfigPath, DbPath))
            .Should().ThrowAsync<InvalidOperationException>();
        FluentActions.Invoking(() => _loader.SaveConfig(_after)).Should().Throw<ConfigurationBusyException>();
        File.Exists(ConfigPath + ".rename.json").Should().BeTrue();
    }

    [Fact]
    public void ConcurrentRead_PreventsMutation_AndNestedMutationIsSafe()
    {
        using (ConfigMutationGuard.EnterProcessing(ConfigPath))
            FluentActions.Invoking(() => ConfigMutationGuard.EnterMutation(ConfigPath)).Should().Throw<ConfigurationBusyException>();
        using (ConfigMutationGuard.EnterMutation(ConfigPath))
        {
            _loader.SaveConfig(_before);
            using var read = ConfigMutationGuard.EnterProcessing(ConfigPath);
        }
        _loader.SaveConfig(_after);
    }

    [Fact]
    public void ExternalProcessingLease_PreventsConfigurationWrite()
    {
        using var external = new FileStream(ConfigPath + ".write-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        FluentActions.Invoking(() => _loader.SaveConfig(_after)).Should().Throw<ConfigurationBusyException>();
    }

    [Fact]
    public void ActiveWriter_PreventsProcessingInAnotherExecutionContext()
    {
        ConfigMutationGuard.EnableProcessCoordination(ConfigPath);
        using var external = new FileStream(ConfigPath + ".write-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        FluentActions.Invoking(() => ConfigMutationGuard.EnterProcessing(ConfigPath)).Should().Throw<ConfigurationBusyException>();
    }

    [Theory]
    [InlineData("qBit-bad/path")]
    [InlineData("qBit-bad\n[Settings]")]
    public void UnsafeRenameDestination_IsRejected(string destination)
    {
        var map = new JObject { ["qBit-before"] = destination };
        FluentActions.Invoking(() => InstanceRenamePersistence.Validate(_before, _after, map)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task RunningHost_BlocksStandaloneRenameAndDuplicateHost()
    {
        using var ownership = HostConfigOwnership.Acquire(ConfigPath);
        FluentActions.Invoking(() => HostConfigOwnership.Acquire(ConfigPath)).Should().Throw<ConfigurationBusyException>();
        FluentActions.Invoking(() => HostConfigOwnership.Acquire(Path.Combine(_root, "other.toml"))).Should().Throw<ConfigurationBusyException>();
        await FluentActions.Awaiting(() => InstanceRenamePersistence.SaveAsync(_before, _after, _renames, _loader, _db))
            .Should().ThrowAsync<ConfigurationBusyException>();
        _db.TorrentLibrary.Single(t => t.Id == 1).QbitInstance.Should().Be("qBit-before");
    }

    [Fact]
    public async Task Recovery_RejectsADifferentDatabasePath()
    {
        WriteJournal(new(Digest(), "new", [], []));
        await FluentActions.Awaiting(() => InstanceRenamePersistence.RecoverAsync(ConfigPath, Path.Combine(_root, "different.db")))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*database path*");
        File.Exists(ConfigPath + ".rename.json").Should().BeTrue();
    }

    [SkippableFact]
    public async Task ConfigWriteFailure_LeavesOldConfigAndDatabaseIntact()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip.If(true, "Requires Linux filesystem permissions.");
            return;
        }
        Skip.If(Environment.UserName == "root", "Requires Linux filesystem permissions.");
        var original = File.ReadAllText(ConfigPath);
        var mode = File.GetUnixFileMode(_root);
        try
        {
            await FluentActions.Awaiting(() => InstanceRenamePersistence.SaveAsync(_before, _after, _renames, _loader, _db,
                suspend: () =>
                {
                    if (OperatingSystem.IsLinux())
                        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    return Task.CompletedTask;
                }))
                .Should().ThrowAsync<UnauthorizedAccessException>();
        }
        finally { File.SetUnixFileMode(_root, mode); }
        File.ReadAllText(ConfigPath).Should().Be(original);
        (await _db.TorrentLibrary.AsNoTracking().SingleAsync(t => t.Id == 1)).QbitInstance.Should().Be("qBit-before");
        File.Exists(ConfigPath + ".rename.json").Should().BeFalse();
    }

    private string Digest() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ConfigPath)));
    private void WriteJournal(InstanceRenamePersistence.Journal journal)
        => File.WriteAllText(ConfigPath + ".rename.json", JsonSerializer.Serialize(journal with { DatabasePath = DbPath }));
    public void Dispose() { _db.Dispose(); Directory.Delete(_root, true); }
}
