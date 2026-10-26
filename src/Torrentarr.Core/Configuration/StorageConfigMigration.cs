using Tomlyn;
using Tomlyn.Model;

namespace Torrentarr.Core.Configuration;

public static class StorageConfigMigration
{
    private static readonly string[] LegacyKeys = ["CompletedDownloadFolder", "FreeSpace", "FreeSpaceFolder", "AutoPauseResume", "FreeSpaceThresholdGB"];
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static bool Contains(string root, string path)
    {
        root = root.Replace('\\', '/').TrimEnd('/');
        path = path.Replace('\\', '/').TrimEnd('/');
        var comparison = root.StartsWith("//", StringComparison.Ordinal) || root.Length > 1 && root[1] == ':' ? StringComparison.OrdinalIgnoreCase : Comparison;
        return path.Equals(root, comparison) || path.StartsWith(root + "/", comparison);
    }

    public static string? LocalDownloadPath(TorrentClientInstanceConfig client)
        => Map(client.DownloadPath, client.Maintenance.PathMappings, reverse: false);

    private static string? Map(string? path, List<PathMappingConfig> mappings, bool reverse)
    {
        if (!Usable(path)) return null;
        if (mappings.Count == 0) return path;
        var matches = mappings.Where(m => Contains(reverse ? m.LocalPath : m.ClientPath, path!))
            .OrderByDescending(m => (reverse ? m.LocalPath : m.ClientPath).Length).ToList();
        if (matches.Count == 0 || reverse && matches.Count > 1) return null;
        var mapping = matches[0];
        var prefix = (reverse ? mapping.LocalPath : mapping.ClientPath).Replace('\\', '/').TrimEnd('/');
        var target = (reverse ? mapping.ClientPath : mapping.LocalPath).Replace('\\', '/').TrimEnd('/');
        var mapped = target + path!.Replace('\\', '/')[prefix.Length..];
        return mapped.Length == 0 ? "/" : mapped;
    }

    private static object? Value(TomlTable table, string key) => table.TryGetValue(key, out var value) ? value : null;
    private static bool Usable(string? path) => !string.IsNullOrWhiteSpace(path) && path != "CHANGE_ME";
    public static TomlTable ReadLegacy(TomlTable root)
    {
        var result = new TomlTable();
        if (root.TryGetValue("Settings", out var value) && value is TomlTable settings)
            foreach (var key in LegacyKeys)
                if (settings.TryGetValue(key, out var field)) result[key] = field;
        return result;
    }

    private static IEnumerable<(string Id, TomlTable Table)> Clients(TomlTable root)
    {
        foreach (var (id, value) in root)
        {
            if ((id.Equals("qBit", StringComparison.OrdinalIgnoreCase) || id.StartsWith("qBit-", StringComparison.OrdinalIgnoreCase)) && value is TomlTable legacy)
                yield return (id, legacy);
            if (id == "TorrentClient" && value is TomlTable clients)
                foreach (var entry in clients)
                    if (entry.Value is TomlTable client) yield return (entry.Key, client);
        }
    }

    public static bool Migrate(TomlTable root)
    {
        var settings = ReadLegacy(root);
        if (settings.Count == 0) return false;
        ConvertClients(root, settings);
        if (root["Settings"] is TomlTable table)
            foreach (var key in LegacyKeys) table.Remove(key);
        return true;
    }

    private static void ConvertClients(TomlTable root, TomlTable legacy)
    {
        var clients = Clients(root).ToList();
        foreach (var (id, table) in clients)
        {
            var client = TomlSerializer.Deserialize<TorrentClientInstanceConfig>(TomlSerializer.Serialize(table))!;
            var completed = Value(legacy, "CompletedDownloadFolder")?.ToString();
            if (clients.Count == 1 && !Usable(client.DownloadPath) && Usable(completed))
            {
                var download = Map(completed, client.Maintenance.PathMappings, reverse: true);
                if (download != null) { table["DownloadPath"] = download; client.DownloadPath = download; }
                else Warn(id, "could not translate the old completed folder to a client download path");
            }
            if (table.ContainsKey("FreeSpace")) continue;
            var path = LocalDownloadPath(client);
            var ambiguous = clients.Count > 1 && !Usable(path);
            if (clients.Count == 1 || ambiguous)
                path = new[] { Value(legacy, "FreeSpaceFolder")?.ToString(), completed, path }.FirstOrDefault(Usable);
            var validPath = Usable(path) && Path.IsPathFullyQualified(path!);
            var limit = Value(legacy, "FreeSpace")?.ToString() ?? "-1";
            if (!FreeSpaceThreshold.TryParse(limit, out var bytes)) throw new FormatException("Invalid legacy FreeSpace threshold.");
            var enabled = validPath && !ambiguous && bytes > 0 && (!legacy.TryGetValue("AutoPauseResume", out var auto) || Convert.ToBoolean(auto));
            if (bytes < 0)
            {
                var gb = legacy.TryGetValue("FreeSpaceThresholdGB", out var oldGb) ? Convert.ToDouble(oldGb) : 10;
                limit = checked((long)(gb * 1024 * 1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            table["FreeSpace"] = validPath
                ? new TomlTableArray { new TomlTable { ["Path"] = path!, ["Enabled"] = enabled, ["Limit"] = limit } }
                : new TomlTableArray();
            if (ambiguous && validPath) Warn(id, "legacy monitor ownership is ambiguous; the inherited path is disabled for review");
            if (!validPath) Warn(id, "no unambiguous local monitor path; free-space protection remains disabled until configured");
        }
    }

    public static void ApplyEnvironment(TomlTable root, TomlTable legacy, IEnumerable<string> overrides)
    {
        var keys = overrides.ToHashSet();
        var clients = Clients(root).ToList();
        foreach (var (id, table) in clients)
        {
            var client = TomlSerializer.Deserialize<TorrentClientInstanceConfig>(TomlSerializer.Serialize(table))!;
            if (clients.Count == 1 && keys.Contains("CompletedDownloadFolder"))
            {
                var rootPath = Map(legacy["CompletedDownloadFolder"].ToString(), client.Maintenance.PathMappings, reverse: true);
                if (rootPath != null) { table["DownloadPath"] = rootPath; client.DownloadPath = rootPath; }
                else Warn(id, "legacy environment download folder cannot be reverse-mapped");
            }
            if (client.FreeSpace.Count == 0) table.Remove("FreeSpace");
            else
            {
                foreach (var monitor in client.FreeSpace)
                {
                    if (keys.Contains("FreeSpace"))
                    {
                        var bytes = FreeSpaceThreshold.Parse(legacy["FreeSpace"]?.ToString());
                        if (bytes >= 0) monitor.Limit = legacy["FreeSpace"].ToString()!;
                        monitor.Enabled = bytes > 0 && (!legacy.TryGetValue("AutoPauseResume", out var enabled) || Convert.ToBoolean(enabled));
                    }
                    if (keys.Contains("AutoPauseResume")) monitor.Enabled = Convert.ToBoolean(legacy["AutoPauseResume"]) && FreeSpaceThreshold.Parse(Value(legacy, "FreeSpace")?.ToString() ?? monitor.Limit) > 0;
                    if (clients.Count == 1 && keys.Contains("FreeSpaceFolder")) monitor.Path = legacy["FreeSpaceFolder"].ToString()!;
                    else if (clients.Count == 1 && keys.Contains("CompletedDownloadFolder") && !Usable(Value(legacy, "FreeSpaceFolder")?.ToString()))
                        monitor.Path = legacy["CompletedDownloadFolder"].ToString()!;
                }
                table["FreeSpace"] = TomlSerializer.Deserialize<TomlTable>(TomlSerializer.Serialize(client))!["FreeSpace"];
            }
        }
        ConvertClients(root, legacy);
    }

    public static string? Validate(TorrentClientInstanceConfig client)
    {
        if (client.FreeSpace == null) return "FreeSpace must be a list.";
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var row in client.FreeSpace)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Path) || !Path.IsPathFullyQualified(row.Path)
                || row.Path.Replace('\\', '/').Split('/').Any(p => p is "." or ".."))
                return "FreeSpace paths must be absolute local paths without traversal segments.";
            if (!paths.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(row.Path)))) return "FreeSpace contains a duplicate path.";
            if (!FreeSpaceThreshold.TryParse(row.Limit, out var limit) || limit < 0) return "FreeSpace limits must be nonnegative integers with an optional K, M, G, or T suffix.";
        }
        return null;
    }

    private static void Warn(string id, string message) => Console.Error.WriteLine($"Storage migration [{id}]: {message}.");
}
