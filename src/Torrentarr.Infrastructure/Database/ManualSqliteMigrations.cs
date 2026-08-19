using Microsoft.EntityFrameworkCore;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Database;

/// <summary>
/// SQLite column/table patches that <c>EnsureCreated</c> will not apply on an existing database.
/// Called from Host and WebUI so Readarr tables exist after upgrading an existing <c>torrentarr.db</c>.
/// </summary>
public static class ManualSqliteMigrations
{
    public static void Apply(TorrentarrDbContext db)
    {
        AddColumnIfMissing(db, "seriesfilesmodel", "tvdbid", "INTEGER NOT NULL DEFAULT 0");

        AddColumnIfMissing(db, "moviesfilesmodel", "InCinemas", "TEXT");
        AddColumnIfMissing(db, "moviesfilesmodel", "DigitalRelease", "TEXT");
        AddColumnIfMissing(db, "moviesfilesmodel", "PhysicalRelease", "TEXT");
        AddColumnIfMissing(db, "moviesfilesmodel", "MinimumAvailability", "TEXT");

        AddColumnIfMissing(db, "episodefilesmodel", "InCinemas", "TEXT");
        AddColumnIfMissing(db, "episodefilesmodel", "DigitalRelease", "TEXT");
        AddColumnIfMissing(db, "episodefilesmodel", "PhysicalRelease", "TEXT");
        AddColumnIfMissing(db, "episodefilesmodel", "MinimumAvailability", "TEXT");

        AddColumnIfMissing(db, "albumfilesmodel", "InCinemas", "TEXT");
        AddColumnIfMissing(db, "albumfilesmodel", "DigitalRelease", "TEXT");
        AddColumnIfMissing(db, "albumfilesmodel", "PhysicalRelease", "TEXT");
        AddColumnIfMissing(db, "albumfilesmodel", "MinimumAvailability", "TEXT");

        CreateTableIfMissing(db, "searchactivity", "CREATE TABLE IF NOT EXISTS searchactivity ( category TEXT NOT NULL PRIMARY KEY, summary TEXT, timestamp TEXT );");

        CreateTableIfMissing(db, "bookfilesmodel", """
            CREATE TABLE IF NOT EXISTS bookfilesmodel (
                entryid INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL DEFAULT '',
                monitored INTEGER NOT NULL DEFAULT 0,
                foreignbookid TEXT NOT NULL DEFAULT '',
                releasedate TEXT,
                arrinstance TEXT NOT NULL DEFAULT '',
                searched INTEGER NOT NULL DEFAULT 0,
                bookfileid INTEGER NOT NULL DEFAULT 0,
                isrequest INTEGER NOT NULL DEFAULT 0,
                qualitymet INTEGER NOT NULL DEFAULT 0,
                upgrade INTEGER NOT NULL DEFAULT 0,
                customformatscore INTEGER,
                mincustomformatscore INTEGER,
                customformatmet INTEGER NOT NULL DEFAULT 0,
                reason TEXT,
                authorid INTEGER NOT NULL DEFAULT 0,
                authortitle TEXT,
                qualityprofileid INTEGER,
                qualityprofilename TEXT,
                lastprofileswitchtime TEXT,
                currentprofileid INTEGER,
                originalprofileid INTEGER,
                arrid INTEGER NOT NULL DEFAULT 0,
                hasfile INTEGER NOT NULL DEFAULT 0,
                arrauthorid INTEGER NOT NULL DEFAULT 0,
                incinemas TEXT,
                digitalrelease TEXT,
                physicalrelease TEXT,
                minimumavailability TEXT
            );
            """);

        CreateTableIfMissing(db, "authorfilesmodel", """
            CREATE TABLE IF NOT EXISTS authorfilesmodel (
                entryid INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                title TEXT,
                monitored INTEGER,
                arrinstance TEXT NOT NULL DEFAULT '',
                searched INTEGER NOT NULL DEFAULT 0,
                upgrade INTEGER NOT NULL DEFAULT 0,
                bookcount INTEGER NOT NULL DEFAULT 0,
                mincustomformatscore INTEGER,
                qualityprofileid INTEGER,
                qualityprofilename TEXT,
                lastprofileswitchtime TEXT,
                currentprofileid INTEGER,
                originalprofileid INTEGER,
                arrid INTEGER NOT NULL DEFAULT 0
            );
            """);

        CreateTableIfMissing(db, "bookqueuemodel", """
            CREATE TABLE IF NOT EXISTS bookqueuemodel (
                entryid INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                completed INTEGER NOT NULL DEFAULT 0,
                arrinstance TEXT NOT NULL DEFAULT '',
                queueid INTEGER,
                bookid INTEGER,
                authorid INTEGER,
                downloadid TEXT,
                title TEXT,
                authortitle TEXT,
                status TEXT,
                trackeddownloadstatus TEXT,
                trackeddownloadstate TEXT,
                customformatscore INTEGER,
                quality TEXT,
                size INTEGER,
                timeleft TEXT,
                estimatedcompletiontime TEXT,
                added TEXT,
                torrentname TEXT,
                torrenthash TEXT,
                torrentcategory TEXT,
                torrentstate TEXT,
                torrentprogress REAL,
                torrentcontentpath TEXT,
                torrentdownloadpath TEXT
            );
            """);

        CreateTableIfMissing(
            db,
            "torrentarr_manual_migrations",
            "CREATE TABLE IF NOT EXISTS torrentarr_manual_migrations ( name TEXT NOT NULL PRIMARY KEY );");
        const string emptyArrInstanceCleanup = "empty_arrinstance_row_cleanup_v1";
        if (!IsManualMigrationApplied(db, emptyArrInstanceCleanup))
        {
            DeleteRowsWithEmptyArrInstance(db, "moviesfilesmodel");
            DeleteRowsWithEmptyArrInstance(db, "episodefilesmodel");
            DeleteRowsWithEmptyArrInstance(db, "seriesfilesmodel");
            DeleteRowsWithEmptyArrInstance(db, "albumfilesmodel");
            DeleteRowsWithEmptyArrInstance(db, "artistfilesmodel");
            DeleteRowsWithEmptyArrInstance(db, "trackfilesmodel");
            DeleteRowsWithEmptyArrInstance(db, "bookfilesmodel");
            DeleteRowsWithEmptyArrInstance(db, "authorfilesmodel");
            DeleteRowsWithEmptyArrInstance(db, "moviequeuemodel");
            DeleteRowsWithEmptyArrInstance(db, "episodequeuemodel");
            DeleteRowsWithEmptyArrInstance(db, "albumqueuemodel");
            DeleteRowsWithEmptyArrInstance(db, "bookqueuemodel");
            DeleteRowsWithEmptyArrInstance(db, "filesqueued");
            MarkManualMigrationApplied(db, emptyArrInstanceCleanup);
        }

        CreateIndexIfMissing(db, "idx_arrinstance_movies", "moviesfilesmodel", "arrinstance");
        CreateIndexIfMissing(db, "idx_arrinstance_episodes", "episodefilesmodel", "arrinstance");
        CreateIndexIfMissing(db, "idx_arrinstance_series", "seriesfilesmodel", "arrinstance");
        CreateIndexIfMissing(db, "idx_arrinstance_albums", "albumfilesmodel", "arrinstance");
        CreateIndexIfMissing(db, "idx_arrinstance_artists", "artistfilesmodel", "arrinstance");
        CreateIndexIfMissing(db, "idx_arrinstance_tracks", "trackfilesmodel", "arrinstance");
        CreateIndexIfMissing(db, "idx_arrinstance_books", "bookfilesmodel", "arrinstance");
        CreateIndexIfMissing(db, "idx_arrinstance_authors", "authorfilesmodel", "arrinstance");
        CreateIndexIfMissing(db, "idx_arrinstance_moviequeue", "moviequeuemodel", "arrinstance");
        CreateIndexIfMissing(db, "idx_arrinstance_episodequeue", "episodequeuemodel", "arrinstance");
        CreateIndexIfMissing(db, "idx_arrinstance_albumqueue", "albumqueuemodel", "arrinstance");
        CreateIndexIfMissing(db, "idx_arrinstance_bookqueue", "bookqueuemodel", "arrinstance");
        CreateIndexIfMissing(db, "idx_arrinstance_filesqueued", "filesqueued", "arrinstance");
    }

    internal static void CreateTableIfMissing(TorrentarrDbContext db, string tableName, string createSql)
    {
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name;";
            var p = cmd.CreateParameter();
            p.ParameterName = "@name";
            p.Value = tableName;
            cmd.Parameters.Add(p);
            var exists = cmd.ExecuteScalar() != null;
            if (!exists)
            {
                using var create = conn.CreateCommand();
                create.CommandText = createSql;
                create.ExecuteNonQuery();
            }
        }
        finally
        {
            if (!wasOpen) conn.Close();
        }
    }

    internal static void AddColumnIfMissing(TorrentarrDbContext db, string table, string column, string columnDef)
    {
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = cmd.ExecuteReader();
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
                columns.Add(reader.GetString(1));
            reader.Close();

            if (!columns.Contains(column))
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDef};";
                alter.ExecuteNonQuery();
            }
        }
        finally
        {
            if (!wasOpen) conn.Close();
        }
    }

    internal static void DeleteRowsWithEmptyArrInstance(TorrentarrDbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table} WHERE arrinstance IS NULL OR TRIM(arrinstance)='';";
            cmd.ExecuteNonQuery();
        }
        finally
        {
            if (!wasOpen) conn.Close();
        }
    }

    internal static bool IsManualMigrationApplied(TorrentarrDbContext db, string name)
    {
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM torrentarr_manual_migrations WHERE name = @name LIMIT 1;";
            var p = cmd.CreateParameter();
            p.ParameterName = "@name";
            p.Value = name;
            cmd.Parameters.Add(p);
            return cmd.ExecuteScalar() != null;
        }
        finally
        {
            if (!wasOpen) conn.Close();
        }
    }

    internal static void MarkManualMigrationApplied(TorrentarrDbContext db, string name)
    {
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO torrentarr_manual_migrations (name) VALUES (@name);";
            var p = cmd.CreateParameter();
            p.ParameterName = "@name";
            p.Value = name;
            cmd.Parameters.Add(p);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            if (!wasOpen) conn.Close();
        }
    }

    internal static void CreateIndexIfMissing(TorrentarrDbContext db, string indexName, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name=@name;";
            var p = cmd.CreateParameter();
            p.ParameterName = "@name";
            p.Value = indexName;
            cmd.Parameters.Add(p);
            var exists = cmd.ExecuteScalar() != null;
            if (!exists)
            {
                using var create = conn.CreateCommand();
                create.CommandText = $"CREATE INDEX {indexName} ON {table}({column});";
                create.ExecuteNonQuery();
            }
        }
        finally
        {
            if (!wasOpen) conn.Close();
        }
    }
}
