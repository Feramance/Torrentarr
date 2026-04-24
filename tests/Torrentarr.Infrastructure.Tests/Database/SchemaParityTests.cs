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
            "albumfilesmodel", "albumqueuemodel", "artistfilesmodel", "episodefilesmodel", "episodequeuemodel",
            "filesqueued", "moviequeuemodel", "moviesfilesmodel", "searchactivity", "seriesfilesmodel", "torrentlibrary",
            "trackfilesmodel"
        };
        tableNames.Should().Equal(expected);
    }
}
