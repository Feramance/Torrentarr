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
        CreateTableIfMissing(db, "torrentarr_worker_history", """
            CREATE TABLE IF NOT EXISTS torrentarr_worker_history (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, Timestamp INTEGER NOT NULL, RunId TEXT NOT NULL DEFAULT '',
                Instance TEXT NOT NULL, Loop TEXT NOT NULL, Type TEXT NOT NULL, Source TEXT NOT NULL,
                SubjectType TEXT NOT NULL, SubjectId TEXT NOT NULL, Summary TEXT NOT NULL,
                Outcome TEXT NOT NULL, Details TEXT NOT NULL, ClientInstance TEXT NOT NULL,
                Server TEXT NOT NULL, CommandId INTEGER, SubmittedAt INTEGER, CompletedAt INTEGER
            );
            """);
        AddColumnIfMissing(db, "torrentarr_worker_history", "RunId", "TEXT NOT NULL DEFAULT ''");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_history_instance ON torrentarr_worker_history (Instance, Id)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_history_loop ON torrentarr_worker_history (Instance, Loop, Id)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_history_type ON torrentarr_worker_history (Type, Id)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_history_timestamp ON torrentarr_worker_history (Timestamp)");
        CreateTableIfMissing(db, "torrentarr_operational_issues", """
            CREATE TABLE IF NOT EXISTS torrentarr_operational_issues (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, Code TEXT NOT NULL, Category TEXT NOT NULL,
                ArrInstance TEXT COLLATE NOCASE NOT NULL DEFAULT '', ClientInstance TEXT COLLATE NOCASE NOT NULL DEFAULT '',
                SubjectType TEXT NOT NULL, SubjectId TEXT NOT NULL DEFAULT '', Severity TEXT NOT NULL,
                Title TEXT NOT NULL, Impact TEXT NOT NULL, Remediation TEXT NOT NULL, Target TEXT NOT NULL,
                FirstObserved TEXT NOT NULL, LastObserved TEXT NOT NULL, ResolvedAt TEXT, Resolution TEXT,
                Occurrences INTEGER NOT NULL DEFAULT 1,
                UNIQUE(Code, ArrInstance, ClientInstance, SubjectType, SubjectId)
            );
            """);
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS idx_operational_issues_state ON torrentarr_operational_issues (ResolvedAt, Category, LastObserved)");
        AddColumnIfMissing(db, "torrentarr_operational_issues", "EvidenceJson", "TEXT");
        db.Database.ExecuteSqlRaw("UPDATE torrentarr_operational_issues SET Category = 'system' WHERE Code = 'shared-file-filter' AND Category = 'configuration'");
        ApplyIssueAlertTriggers(db);
        CreateTableIfMissing(db, "torrentarr_retirement_upload", """
            CREATE TABLE IF NOT EXISTS torrentarr_retirement_upload (
                Client TEXT NOT NULL, Hash TEXT NOT NULL, Identity TEXT NOT NULL, Category TEXT NOT NULL,
                AddedOn INTEGER NOT NULL, Uploaded INTEGER NOT NULL, Seen INTEGER NOT NULL, Since INTEGER NOT NULL,
                PRIMARY KEY (Client, Hash)
            );
            """);
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

        CreateTableIfMissing(db, "torrentarr_arr_samples", "CREATE TABLE IF NOT EXISTS torrentarr_arr_samples (Instance TEXT NOT NULL, Minute INTEGER NOT NULL, Type TEXT NOT NULL, QueueDepth INTEGER, CompletionObserved INTEGER NOT NULL, PRIMARY KEY (Instance, Minute));");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_torrentarr_arr_samples_Minute ON torrentarr_arr_samples (Minute)");
        MigrateArrActivityOperations(db);
        CreateTableIfMissing(db, "torrentarr_search_decisions", """
            CREATE TABLE IF NOT EXISTS torrentarr_search_decisions (
                Instance TEXT NOT NULL, Key TEXT NOT NULL, ObservedAt INTEGER NOT NULL,
                Occurrences INTEGER NOT NULL, GrabbedAt INTEGER, Data TEXT NOT NULL, PRIMARY KEY (Instance, Key)
            );
            """);
        AddColumnIfMissing(db, "torrentarr_search_decisions", "GrabbedAt", "INTEGER");
        AddColumnIfMissing(db, "torrentarr_search_decisions", "NoGrabAt", "INTEGER");
        AddColumnIfMissing(db, "torrentlibrary", "autodeletependingaddedon", "INTEGER");
        AddColumnIfMissing(db, "torrentlibrary", "importsubmittedat", "INTEGER");
        AddColumnIfMissing(db, "torrentlibrary", "torrentaddedon", "INTEGER");
        CreateTableIfMissing(db, "torrentarr_transfer_samples", "CREATE TABLE IF NOT EXISTS torrentarr_transfer_samples (ClientInstance TEXT NOT NULL, Minute INTEGER NOT NULL, DownloadRate INTEGER NOT NULL, UploadRate INTEGER NOT NULL, PRIMARY KEY (ClientInstance, Minute));");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_torrentarr_transfer_samples_Minute ON torrentarr_transfer_samples (Minute)");
        CreateTableIfMissing(db, "torrentarr_maintenance_state", "CREATE TABLE IF NOT EXISTS torrentarr_maintenance_state (Key TEXT NOT NULL PRIMARY KEY, Data TEXT NOT NULL DEFAULT '{}');");

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS idx_pending_import_rescue ON torrentarr_maintenance_state
                (CASE WHEN json_valid(Data) THEN json_extract(Data, '$.Server') END, Key)
            WHERE substr(Key, -14) = '|import-rescue'
                AND CASE WHEN json_valid(Data) THEN json_extract(Data, '$.Stage') END = 'pending'
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS idx_import_plans ON torrentarr_maintenance_state
                (CASE WHEN json_valid(Data) THEN json_extract(Data, '$.Instance') END,
                 CASE WHEN json_valid(Data) THEN json_extract(Data, '$.Server') END,
                 CASE WHEN json_valid(Data) THEN json_extract(Data, '$.State') END, Key)
            WHERE substr(Key, -12) = '|import-plan'
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS idx_pending_failed_parking ON torrentarr_maintenance_state
                (CASE WHEN json_valid(Data) THEN json_extract(Data, '$.Server') END, Key)
            WHERE substr(Key, -15) = '|failed-parking'
                AND CASE WHEN json_valid(Data) THEN json_extract(Data, '$.Stage') END IN ('moving', 'queue')
            """);

        CreateTableIfMissing(db, "searchactivity", "CREATE TABLE IF NOT EXISTS searchactivity ( category TEXT NOT NULL PRIMARY KEY, summary TEXT, timestamp TEXT );");
        CreateTableIfMissing(db, "torrentarr_sonarr_search_retries", "CREATE TABLE IF NOT EXISTS torrentarr_sonarr_search_retries ( ArrInstance TEXT NOT NULL, DownloadId TEXT NOT NULL, CandidatesJson TEXT NOT NULL DEFAULT '[]', PRIMARY KEY (ArrInstance, DownloadId) );");

        AddColumnIfMissing(db, "torrentarr_sonarr_search_retries", "BlocklistPending", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "torrentarr_sonarr_search_retries", "ConsecutiveFailures", "INTEGER NOT NULL DEFAULT 0");

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

        // QueueDepth was sampled from queue tables that were never populated; clear the false zeros once.
        const string resetArrQueueDepth = "arr_activity_queue_depth_reset_v1";
        if (!IsManualMigrationApplied(db, resetArrQueueDepth))
        {
            db.Database.ExecuteSqlRaw("UPDATE torrentarr_arr_samples SET QueueDepth = NULL");
            MarkManualMigrationApplied(db, resetArrQueueDepth);
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

    internal static void ApplyIssueAlertTriggers(TorrentarrDbContext db)
    {
        CreateTableIfMissing(db, "torrentarr_issue_alert_events", """
            CREATE TABLE IF NOT EXISTS torrentarr_issue_alert_events (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, IssueId INTEGER NOT NULL, OpeningId INTEGER, OpenedAt TEXT NOT NULL,
                ObservedAt TEXT NOT NULL, State TEXT NOT NULL, Code TEXT NOT NULL,
                Severity TEXT NOT NULL, Instance TEXT NOT NULL
            );
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_torrentarr_issue_alert_events_IssueId_OpenedAt_State
            ON torrentarr_issue_alert_events (IssueId, OpenedAt, State);
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS torrentarr_issue_alert_insert
            AFTER INSERT ON torrentarr_operational_issues
            WHEN NEW.Code IN ('worker.lifecycle', 'automation.protection', 'import.scan', 'import.ownership', 'import.history')
            BEGIN
                INSERT INTO torrentarr_issue_alert_events (IssueId, OpenedAt, ObservedAt, State, Code, Severity, Instance)
                VALUES (NEW.Id, NEW.FirstObserved, NEW.FirstObserved, 'opened', NEW.Code, NEW.Severity,
                    COALESCE(NULLIF(NEW.ArrInstance, ''), NEW.ClientInstance));
                INSERT INTO torrentarr_issue_alert_events (IssueId, OpeningId, OpenedAt, ObservedAt, State, Code, Severity, Instance)
                SELECT NEW.Id, last_insert_rowid(), NEW.FirstObserved, NEW.ResolvedAt, 'resolved', NEW.Code, NEW.Severity,
                    COALESCE(NULLIF(NEW.ArrInstance, ''), NEW.ClientInstance) WHERE NEW.ResolvedAt IS NOT NULL;
            END;
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS torrentarr_issue_alert_reopen
            AFTER UPDATE ON torrentarr_operational_issues
            WHEN OLD.ResolvedAt IS NOT NULL AND NEW.ResolvedAt IS NULL
                AND NEW.Code IN ('worker.lifecycle', 'automation.protection', 'import.scan', 'import.ownership', 'import.history')
            BEGIN
                INSERT OR IGNORE INTO torrentarr_issue_alert_events (IssueId, OpenedAt, ObservedAt, State, Code, Severity, Instance)
                VALUES (NEW.Id, NEW.FirstObserved, NEW.FirstObserved, 'opened', NEW.Code, NEW.Severity,
                    COALESCE(NULLIF(NEW.ArrInstance, ''), NEW.ClientInstance));
            END;
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS torrentarr_issue_alert_resolve
            AFTER UPDATE ON torrentarr_operational_issues
            WHEN OLD.ResolvedAt IS NULL AND NEW.ResolvedAt IS NOT NULL
                AND EXISTS (SELECT 1 FROM torrentarr_issue_alert_events
                    WHERE IssueId = NEW.Id AND OpenedAt = NEW.FirstObserved AND State = 'opened')
            BEGIN
                INSERT OR IGNORE INTO torrentarr_issue_alert_events (IssueId, OpeningId, OpenedAt, ObservedAt, State, Code, Severity, Instance)
                SELECT NEW.Id, Id, NEW.FirstObserved, NEW.ResolvedAt, 'resolved', NEW.Code, NEW.Severity,
                    COALESCE(NULLIF(NEW.ArrInstance, ''), NEW.ClientInstance) FROM torrentarr_issue_alert_events
                    WHERE IssueId = NEW.Id AND OpenedAt = NEW.FirstObserved AND State = 'opened';
            END;
            """);
    }

    private static void MigrateArrActivityOperations(TorrentarrDbContext db)
    {
        using var transaction = db.Database.BeginTransaction();
        const string createTable = """
            CREATE TABLE IF NOT EXISTS torrentarr_arr_operations (Instance TEXT NOT NULL, CommandId INTEGER NOT NULL, Type TEXT NOT NULL, Kind TEXT NOT NULL, Server TEXT NOT NULL, ClientInstance TEXT NOT NULL, DownloadId TEXT NOT NULL, SubmittedAt INTEGER NOT NULL, ConfirmedAt INTEGER, Outcome TEXT NOT NULL DEFAULT 'pending', EvidenceId TEXT, PRIMARY KEY (Instance, Server, CommandId));
            """;
        db.Database.ExecuteSqlRaw(createTable);
        var hasServerKey = db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('torrentarr_arr_operations') WHERE name = 'Server' AND pk > 0").Single() > 0;
        if (!hasServerKey)
        {
            db.Database.ExecuteSqlRaw("""
                ALTER TABLE torrentarr_arr_operations RENAME TO torrentarr_arr_operations_legacy;
                """);
            db.Database.ExecuteSqlRaw(createTable);
            db.Database.ExecuteSqlRaw("""
                INSERT INTO torrentarr_arr_operations (Instance, CommandId, Type, Kind, Server, ClientInstance, DownloadId, SubmittedAt, ConfirmedAt, Outcome, EvidenceId)
                    SELECT Instance, CommandId, Type, Kind, Server, ClientInstance, DownloadId, SubmittedAt, ConfirmedAt, Outcome, EvidenceId
                    FROM torrentarr_arr_operations_legacy;
                DROP TABLE torrentarr_arr_operations_legacy;
                """);
        }
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_torrentarr_arr_operations_Instance_Server_EvidenceId ON torrentarr_arr_operations (Instance, Server, EvidenceId)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_torrentarr_arr_operations_SubmittedAt ON torrentarr_arr_operations (SubmittedAt)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_torrentarr_arr_operations_ConfirmedAt ON torrentarr_arr_operations (ConfirmedAt)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_torrentarr_arr_operations_Instance_DownloadId ON torrentarr_arr_operations (Instance, DownloadId)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_torrentarr_arr_operations_Instance_Outcome ON torrentarr_arr_operations (Instance, Outcome)");
        transaction.Commit();
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
