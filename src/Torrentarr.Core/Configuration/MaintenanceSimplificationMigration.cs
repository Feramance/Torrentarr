using Tomlyn.Model;

namespace Torrentarr.Core.Configuration;

/// <summary>Reads the interim layout without discarding the original maintenance configuration.</summary>
public static class MaintenanceSimplificationMigration
{
    public static bool Migrate(TomlTable root)
    {
        var changed = false;
        foreach (var (name, value) in root.ToList())
        {
            if (value is not TomlTable table) continue;
            if (name.Equals("qBit", StringComparison.OrdinalIgnoreCase) || name.StartsWith("qBit-", StringComparison.OrdinalIgnoreCase))
                changed |= ConvertAndSimplifyModes(table);
            else if (name == "TorrentClient")
                foreach (var client in table.Values.OfType<TomlTable>()) changed |= ConvertAndSimplifyModes(client);
        }
        return changed;
    }

    private static bool ConvertAndSimplifyModes(TomlTable client)
    {
        var changed = ConvertClient(client);
        if (client.TryGetValue("Maintenance", out var value) && value is TomlTable maintenance
            && maintenance.TryGetValue("Operations", out var operations) && operations is TomlTable policies)
            foreach (var policy in policies.Values.OfType<TomlTable>())
                if (policy.TryGetValue("ExecutionMode", out var mode) && mode is string text && text == "session")
                {
                    policy["ExecutionMode"] = "manual";
                    changed = true;
                }
        return changed;
    }

    private static bool ConvertClient(TomlTable client)
    {
        if (!client.ContainsKey("Storage") && !client.ContainsKey("Recovery")) return false;
        var hadMaintenance = client.ContainsKey("Maintenance");
        var maintenance = Table(client, "Maintenance");
        if (client.TryGetValue("Storage", out var storageValue))
        {
            if (storageValue is not TomlTable storage) throw new InvalidDataException("Storage must be a table.");
            foreach (var key in new[] { "PathMappings", "Orphans", "RecycleBin" })
                if (storage.TryGetValue(key, out var value)) MergeValue(maintenance, key, value);
            if (storage.TryGetValue("AutomaticCleanup", out var automaticValue))
            {
                if (automaticValue is not bool automatic) throw new InvalidDataException("AutomaticCleanup must be a boolean.");
                if (automatic && !maintenance.ContainsKey("Enabled")) maintenance["Enabled"] = true;
                foreach (var name in new[] { "OrphanScan", "RecycleRetention" })
                {
                    var policy = Table(Table(maintenance, "Operations"), name);
                    if (!policy.ContainsKey("ExecutionMode")) policy["ExecutionMode"] = automatic ? "automatic" : "manual";
                    if (automatic && !policy.ContainsKey("Schedule")) policy["Schedule"] = "15 3 * * *";
                }
            }
        }
        if (client.TryGetValue("Recovery", out var recoveryValue))
        {
            if (recoveryValue is not TomlTable recovery) throw new InvalidDataException("Recovery must be a table.");
            var target = Table(maintenance, "ErrorRecovery");
            foreach (var (key, value) in recovery)
            {
                if (key == "MinimumTorrentAgeMinutes") MergeValue(maintenance, key, value);
                else if (key is "IncludedCategories" or "ExcludedCategories" or "ExcludedTags")
                    MergeValue(Table(Table(maintenance, "Operations"), "ErrorRecovery"), key, value);
                else MergeValue(target, key, value);
            }
            if (recovery.TryGetValue("Enabled", out var enabledValue))
            {
                if (enabledValue is not bool enabled) throw new InvalidDataException("Recovery.Enabled must be a boolean.");
                enabled = Flag(target, "Enabled");
                if (enabled && !maintenance.ContainsKey("Enabled")) maintenance["Enabled"] = true;
                var policy = Table(Table(maintenance, "Operations"), "ErrorRecovery");
                if (!policy.ContainsKey("ExecutionMode")) policy["ExecutionMode"] = enabled ? "automatic" : "manual";
                if (!policy.ContainsKey("Enabled")) policy["Enabled"] = enabled;
                if (enabled && !policy.ContainsKey("Schedule")) policy["Schedule"] = "*/5 * * * *";
            }
        }
        if (!hadMaintenance)
        {
            var policies = Table(maintenance, "Operations");
            foreach (var operation in Enum.GetValues<Torrentarr.Core.Models.MaintenanceOperation>())
            {
                var policy = Table(policies, operation.ToString());
                if (policy.ContainsKey("Enabled")) continue;
                var enabled = operation switch
                {
                    Torrentarr.Core.Models.MaintenanceOperation.OrphanScan => maintenance.TryGetValue("Orphans", out var orphans) && orphans is TomlTable orphan && Flag(orphan, "Enabled"),
                    Torrentarr.Core.Models.MaintenanceOperation.RecycleRetention => maintenance.TryGetValue("RecycleBin", out var recycleValue) && recycleValue is TomlTable recycle
                        ? Flag(recycle, "Enabled", true) && Flag(recycle, "RetentionEnabled", true)
                        : client.TryGetValue("Storage", out var interim) && interim is TomlTable storage && Flag(storage, "AutomaticCleanup"),
                    _ => false
                };
                policy["Enabled"] = enabled;
                if (enabled && !maintenance.ContainsKey("Enabled")) maintenance["Enabled"] = true;
            }
        }
        client.Remove("Storage");
        client.Remove("Recovery");
        return true;
    }

    private static bool Flag(TomlTable table, string key, bool fallback = false)
        => !table.TryGetValue(key, out var value) ? fallback : value is bool flag ? flag : throw new InvalidDataException($"{key} must be a boolean.");

    private static TomlTable Table(TomlTable parent, string key)
    {
        if (!parent.TryGetValue(key, out var value)) { var table = new TomlTable(); parent[key] = table; return table; }
        return value as TomlTable ?? throw new InvalidDataException($"{key} must be a table.");
    }

    private static void MergeValue(TomlTable target, string key, object value)
    {
        if (!target.TryGetValue(key, out var existing)) target[key] = value;
        else if (existing is TomlTable destination && value is TomlTable source)
            foreach (var (child, childValue) in source) MergeValue(destination, child, childValue);
    }
}
