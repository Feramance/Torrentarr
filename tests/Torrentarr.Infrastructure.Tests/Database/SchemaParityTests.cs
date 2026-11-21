using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Infrastructure.Database;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Database;

/// <summary>
/// Harness: table names in the EF model must match the qBitrr-compatible lowercase names
/// in <see cref="TorrentarrDbContext" /> so drift is caught in CI.
/// </summary>
public class SchemaParityTests
{
    [Fact]
    public void ManualMigrationAddsTorrentIdentityWithoutDiscardingExistingImportState()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("ALTER TABLE torrentlibrary DROP COLUMN torrentaddedon");
        db.Database.ExecuteSqlRaw("""
            INSERT INTO torrentlibrary (hash, category, qbitinstance, arrinstance, imported, allowedseeding, allowedstalled, freespacepaused)
            VALUES ('hash', 'movies', 'qBit', 'Radarr-Movies', 1, 0, 0, 0)
            """);

        db.Database.ExecuteSqlRaw("INSERT INTO torrentarr_maintenance_state (Key, Data) VALUES ('broken|import-rescue', 'not-json')");
        ManualSqliteMigrations.Apply(db);
        ManualSqliteMigrations.Apply(db);

        db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND name = 'idx_pending_import_rescue'")
            .Single().Should().Be("idx_pending_import_rescue");
        var entry = db.TorrentLibrary.Single();
        entry.Imported.Should().BeTrue();
        entry.TorrentAddedOn.Should().BeNull();
    }

    [Fact]
    public void SqliteModel_CreatesExpectedQBitrrCompatibleTables()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseSqlite(connection)
            .Options;
        using var ctx = new TorrentarrDbContext(options);
        ctx.Database.EnsureCreated();

        var tableNames = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                ORDER BY name
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                tableNames.Add(r.GetString(0));
            }
        }

        // Must stay aligned with OnModelCreating table names in TorrentarrDbContext (qBitrr schema).
        var expected = new[]
        {
            "albumfilesmodel", "albumqueuemodel", "artistfilesmodel", "authorfilesmodel", "bookfilesmodel", "bookqueuemodel",
            "episodefilesmodel", "episodequeuemodel",
            "filesqueued", "moviequeuemodel", "moviesfilesmodel", "searchactivity", "seriesfilesmodel", "torrentarr_arr_operations", "torrentarr_arr_samples", "torrentarr_issue_alert_events", "torrentarr_maintenance_state", "torrentarr_operational_issues", "torrentarr_retirement_upload", "torrentarr_search_decisions", "torrentarr_sonarr_search_retries", "torrentarr_transfer_samples", "torrentlibrary",
            "trackfilesmodel"
        };
        tableNames.Should().Equal(expected);
    }
}
