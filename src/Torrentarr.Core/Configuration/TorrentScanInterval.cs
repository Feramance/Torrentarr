using Tomlyn.Model;

namespace Torrentarr.Core.Configuration;

public static class TorrentScanInterval
{
    public static IReadOnlyList<int> AllowedSeconds { get; } = Array.AsReadOnly(new[] { 5, 15, 30, 60, 90, 120 });
    public static string? Validate(int seconds) => AllowedSeconds.Contains(seconds)
        ? null : "TorrentScanIntervalSeconds must be 5, 15, 30, 60, 90, or 120.";

    public static int FromLegacy(int seconds) => AllowedSeconds.OrderBy(value => Math.Abs((long)value - seconds))
        .ThenByDescending(value => value).First();

    public static bool Migrate(TomlTable root)
    {
        var seconds = root.TryGetValue("Settings", out var value) && value is TomlTable settings
            && settings.TryGetValue("LoopSleepTimer", out var timer) ? DurationParser.ParseToSeconds(timer, 5) : 5;
        var defaultInterval = FromLegacy(seconds);
        if (value is TomlTable current && current.TryGetValue("ConfigVersion", out var version)
            && Version.TryParse(version?.ToString(), out var schema) && schema >= new Version(6, 20, 3))
            defaultInterval = 30;
        var clients = root.Where(pair => pair.Key.Equals("qBit", StringComparison.OrdinalIgnoreCase)
                || pair.Key.StartsWith("qBit-", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value).OfType<TomlTable>().ToList();
        if (root.TryGetValue("TorrentClient", out var canonical) && canonical is TomlTable table)
            clients.AddRange(table.Values.OfType<TomlTable>());
        // Reject explicit invalid values before the loader persists any migration.
        foreach (var client in clients)
            if (client.TryGetValue("TorrentScanIntervalSeconds", out var interval)
                && (interval is not long number || number > int.MaxValue || number < int.MinValue || Validate((int)number) != null))
                throw new FormatException("TorrentScanIntervalSeconds must be an integer: 5, 15, 30, 60, 90, or 120.");
        var changed = false;
        foreach (var client in clients)
            if (!client.ContainsKey("TorrentScanIntervalSeconds"))
            {
                client["TorrentScanIntervalSeconds"] = (long)defaultInterval;
                changed = true;
            }
        return changed;
    }
}

public sealed class TorrentScanIntervalJsonConverter : Newtonsoft.Json.JsonConverter<int>
{
    public override int ReadJson(Newtonsoft.Json.JsonReader reader, Type objectType, int existingValue,
        bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (reader.TokenType != Newtonsoft.Json.JsonToken.Integer)
            throw new Newtonsoft.Json.JsonSerializationException("TorrentScanIntervalSeconds requires an integer.");
        var value = Convert.ToInt64(reader.Value, System.Globalization.CultureInfo.InvariantCulture);
        if (value < int.MinValue || value > int.MaxValue || TorrentScanInterval.Validate((int)value) != null)
            throw new Newtonsoft.Json.JsonSerializationException("TorrentScanIntervalSeconds must be 5, 15, 30, 60, 90, or 120.");
        return (int)value;
    }

    public override void WriteJson(Newtonsoft.Json.JsonWriter writer, int value, Newtonsoft.Json.JsonSerializer serializer)
        => writer.WriteValue(value);
}
