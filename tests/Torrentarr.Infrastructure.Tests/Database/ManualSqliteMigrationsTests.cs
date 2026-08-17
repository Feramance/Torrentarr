using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Infrastructure.Database;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Database;

public class ManualSqliteMigrationsTests
{
    [Fact]
    public void Apply_CreatesReadarrTablesOnPreExistingSqliteFile()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE moviesfilesmodel (entryid INTEGER PRIMARY KEY, arrinstance TEXT);
                CREATE TABLE episodefilesmodel (entryid INTEGER PRIMARY KEY, arrinstance TEXT);
                CREATE TABLE seriesfilesmodel (entryid INTEGER PRIMARY KEY, arrinstance TEXT);
                CREATE TABLE albumfilesmodel (entryid INTEGER PRIMARY KEY, arrinstance TEXT);
                CREATE TABLE artistfilesmodel (entryid INTEGER PRIMARY KEY, arrinstance TEXT);
                CREATE TABLE trackfilesmodel (entryid INTEGER PRIMARY KEY, arrinstance TEXT);
                CREATE TABLE moviequeuemodel (entryid INTEGER PRIMARY KEY, arrinstance TEXT);
                CREATE TABLE episodequeuemodel (entryid INTEGER PRIMARY KEY, arrinstance TEXT);
                CREATE TABLE albumqueuemodel (entryid INTEGER PRIMARY KEY, arrinstance TEXT);
                CREATE TABLE filesqueued (entryid INTEGER PRIMARY KEY, arrinstance TEXT);
                CREATE TABLE torrentlibrary (entryid INTEGER PRIMARY KEY);
                """;
            cmd.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseSqlite(connection)
            .Options;
        using var ctx = new TorrentarrDbContext(options);
        ctx.Database.EnsureCreated();
        ManualSqliteMigrations.Apply(ctx);

        var tables = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }

        tables.Should().Contain("bookfilesmodel");
        tables.Should().Contain("authorfilesmodel");
        tables.Should().Contain("bookqueuemodel");
    }
}
