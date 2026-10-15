using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

public static class InstanceRenamePersistence
{
    internal sealed record StateMove(string Table, string Column, string Source, string Destination, List<long> Rows);
    internal sealed record DirectoryMove(string Source, string Destination);
    internal sealed record Journal(string OldDigest, string NewDigest, List<StateMove> State, List<DirectoryMove> Directories, string? DatabasePath = null);
    private static readonly (string Table, string Column)[] ArrStateColumns =
        [.. new[] { "torrentlibrary", "moviesfilesmodel", "episodefilesmodel", "seriesfilesmodel",
        "albumfilesmodel", "trackfilesmodel", "artistfilesmodel", "bookfilesmodel", "authorfilesmodel",
        "moviequeuemodel", "episodequeuemodel", "albumqueuemodel", "bookqueuemodel", "filesqueued" }
            .Select(table => (table, "arrinstance")), ("searchactivity", "category")];
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static void Validate(TorrentarrConfig current, TorrentarrConfig updated, JObject? renames)
    {
        if (renames == null) return;
        var sources = renames.Properties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rename in renames.Properties())
        {
            if (rename.Value.Type != JTokenType.String) throw new ArgumentException("Invalid configuration rename.");
            var destination = rename.Value.Value<string>()!;
            if (string.IsNullOrWhiteSpace(destination) || destination.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
                throw new ArgumentException("Rename destinations must contain only letters, numbers, underscores or hyphens.");
            if (sources.Contains(destination) || !destinations.Add(destination))
                throw new ArgumentException("Rename chains, cycles and case-only renames are not supported.");
            var clients = current.GetAllTorrentClients();
            var newClients = updated.GetAllTorrentClients();
            if (clients.ContainsKey(rename.Name))
            {
                if (clients.ContainsKey(destination) || newClients.ContainsKey(rename.Name) || !newClients.ContainsKey(destination))
                    throw new ArgumentException("A client rename must remove its source and create an unused destination.");
            }
            else if (current.ArrInstances.ContainsKey(rename.Name))
            {
                if (current.ArrInstances.ContainsKey(destination) || updated.ArrInstances.ContainsKey(rename.Name)
                    || !updated.ArrInstances.ContainsKey(destination)
                    || ArrSectionHelper.ArrTypeFromSectionName(rename.Name) != ArrSectionHelper.ArrTypeFromSectionName(destination))
                    throw new ArgumentException("Invalid Arr rename.");
            }
            else throw new ArgumentException("Rename source does not exist.");
        }
    }

    public static async Task SaveAsync(TorrentarrConfig current, TorrentarrConfig updated, JObject? renames,
        ConfigurationLoader loader, TorrentarrDbContext db, string? configPath = null,
        Func<Task>? suspend = null, CancellationToken ct = default)
    {
        var path = Path.GetFullPath(configPath ?? loader.ConfigPath);
        using var ownership = suspend == null && !ConfigMutationGuard.OwnsMutation(path)
            ? HostConfigOwnership.Acquire(path) : null;
        using var mutation = ConfigMutationGuard.EnterMutation(path);
        Validate(current, updated, renames);
        if (Newtonsoft.Json.JsonConvert.SerializeObject(current.GetAllTorrentClients())
            != Newtonsoft.Json.JsonConvert.SerializeObject(updated.GetAllTorrentClients()))
            foreach (var cfg in updated.GetAllTorrentClients().Values) cfg.Maintenance.Armed = false;
        if (renames == null || !renames.HasValues) { loader.SaveConfig(updated, path); return; }
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        var journalPath = path + ".rename.json";
        Journal? journal = null;
        var published = false;
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var state = new List<StateMove>();
            var directories = new List<DirectoryMove>();
            foreach (var rename in renames.Properties())
            {
                var destination = rename.Value.Value<string>()!;
                var client = current.GetTorrentClient(rename.Name);
                foreach (var (table, column) in client != null ? [("torrentlibrary", "qbitinstance")] : ArrStateColumns)
                {
                    if (!await TableExistsAsync(connection, transaction, table, ct)) continue;
                    if ((await RowsAsync(connection, transaction, table, column, destination, ct)).Count != 0)
                        throw new ArgumentException($"Stored state already exists for rename destination '{destination}'.");
                    state.Add(new(table, column, rename.Name, destination,
                        await RowsAsync(connection, transaction, table, column, rename.Name, ct)));
                }
                if (client != null)
                {
                    var sourceDirectory = SafeDeletionService.ClientRecycleDirectory(client, rename.Name);
                    var destinationDirectory = SafeDeletionService.ClientRecycleDirectory(client, destination);
                    if (sourceDirectory != null && destinationDirectory != null && Directory.Exists(sourceDirectory)
                        && !sourceDirectory.Equals(destinationDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        if (!string.Equals(SafeDeletionService.ClientRecycleDirectory(updated.GetTorrentClient(destination)!, destination), destinationDirectory,
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                            throw new ArgumentException("Rename the client before changing its recycle directory.");
                        ValidateDirectory(sourceDirectory);
                        if (File.Exists(destinationDirectory) || Directory.Exists(destinationDirectory))
                            throw new ArgumentException("The renamed client's recycle directory already exists.");
                        directories.Add(new(sourceDirectory, destinationDirectory));
                    }
                }
            }
            if (suspend != null) await suspend();
            ct.ThrowIfCancellationRequested();
            journal = new(Digest(File.ReadAllBytes(path)), Digest(Encoding.UTF8.GetBytes(loader.SerializeConfig(updated))), state, directories, string.IsNullOrEmpty(connection.DataSource) || connection.DataSource == ":memory:" ? null : Path.GetFullPath(connection.DataSource));
            ConfigMutationGuard.WriteAtomic(journalPath, JsonSerializer.Serialize(journal));
            foreach (var move in directories) Directory.Move(move.Source, move.Destination);
            await ApplyStateAsync(connection, transaction, journal, ct);
            loader.SaveConfig(updated, path);
            published = true;
            // Once config is published, cancellation must not interrupt the matching DB commit.
            await transaction.CommitAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            File.Delete(journalPath);
        }
        catch
        {
            if (!published && journal != null)
            {
                RestoreDirectories(journal);
                File.Delete(journalPath);
            }
            // Published config is recovered at startup before any worker can run.
            throw;
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    public static async Task RecoverAsync(string configPath, string databasePath)
    {
        configPath = Path.GetFullPath(configPath);
        var journalPath = configPath + ".rename.json";
        if (!File.Exists(journalPath)) return;
        using var mutation = ConfigMutationGuard.EnterMutation(configPath, recovery: true);
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath))
            ?? throw new InvalidOperationException("Invalid rename recovery record.");
        if (journal.DatabasePath == null || !Path.GetFullPath(databasePath).Equals(journal.DatabasePath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Rename recovery database path does not match the configured database.");
        var digest = Digest(File.ReadAllBytes(configPath));
        if (digest == journal.OldDigest) RestoreDirectories(journal);
        else if (digest == journal.NewDigest)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var move in journal.Directories)
            {
                if (Directory.Exists(move.Source) && !Directory.Exists(move.Destination))
                {
                    ValidateDirectory(move.Source);
                    Directory.Move(move.Source, move.Destination);
                }
                else if (Directory.Exists(move.Source) || !Directory.Exists(move.Destination))
                    throw new InvalidOperationException("Conflicting recycle directories during rename recovery.");
                ValidateDirectory(move.Destination);
            }
            await ApplyStateAsync(connection, transaction, journal, CancellationToken.None);
            await transaction.CommitAsync();
        }
        else throw new InvalidOperationException("Config changed during an interrupted rename. Restore the expected config before starting Torrentarr.");
        File.Delete(journalPath);
    }

    private static void ValidateDirectory(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing a recycle directory with a symlink or reparse-point ancestor.");
    }

    private static void RestoreDirectories(Journal journal)
    {
        foreach (var move in journal.Directories.AsEnumerable().Reverse())
        {
            if (Directory.Exists(move.Destination) && !Directory.Exists(move.Source))
            {
                ValidateDirectory(move.Destination);
                Directory.Move(move.Destination, move.Source);
            }
            else if (Directory.Exists(move.Destination) || !Directory.Exists(move.Source))
                throw new InvalidOperationException("Conflicting recycle directories during rename rollback.");
        }
    }

    private static async Task<bool> TableExistsAsync(DbConnection connection, DbTransaction transaction, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        Add(command, "$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0;
    }

    private static async Task<List<long>> RowsAsync(DbConnection connection, DbTransaction transaction,
        string table, string column, string instance, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT rowid FROM {table} WHERE {column}=$instance COLLATE NOCASE";
        Add(command, "$instance", instance);
        var rows = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(reader.GetInt64(0));
        return rows;
    }

    private static async Task ApplyStateAsync(DbConnection connection, DbTransaction transaction, Journal journal, CancellationToken ct)
    {
        foreach (var move in journal.State)
        {
            if (!(ArrStateColumns.Contains((move.Table, move.Column))
                || move.Column == "qbitinstance" && move.Table == "torrentlibrary"))
                throw new InvalidOperationException("Invalid rename recovery table.");
            foreach (var row in move.Rows)
            {
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = $"UPDATE {move.Table} SET {move.Column}=$destination WHERE rowid=$row AND ({move.Column}=$source COLLATE NOCASE OR {move.Column}=$destination COLLATE NOCASE)";
                Add(command, "$source", move.Source); Add(command, "$destination", move.Destination); Add(command, "$row", row);
                if (await command.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException("Stored state changed during rename recovery.");
            }
        }
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
