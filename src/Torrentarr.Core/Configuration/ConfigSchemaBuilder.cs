using System.Text.Json.Serialization;

namespace Torrentarr.Core.Configuration;

/// <summary>
/// qBitrr 5.14.6 configuration registry used by the schema API and WebUI code generation.
/// Torrentarr keeps the same schema contract while applying its +1 major version policy.
/// </summary>
public static class ConfigSchemaBuilder
{
    public static object Build() => new
    {
        version = 1,
        sections = Sections
    };

    public static IReadOnlyDictionary<string, IReadOnlyList<ConfigSchemaField>> Sections { get; } =
        new Dictionary<string, IReadOnlyList<ConfigSchemaField>>
        {
            ["Settings"] = Qbitrr5146ConfigSchema.Settings,
            ["WebUI"] = Qbitrr5146ConfigSchema.WebUI,
            ["qBit"] = Qbitrr5146ConfigSchema.qBit,
            ["TorrentClient"] = TorrentClientFields(),
            ["Arr"] = Qbitrr5146ConfigSchema.Arr
        };

    private static IReadOnlyList<ConfigSchemaField> TorrentClientFields() =>
    [
        .. Qbitrr5146ConfigSchema.qBit
            .Where(field => field.key is not ("Disabled" or "Host" or "Port" or "UserName" or "Password"))
            .Select(field => field with { section = "TorrentClient" }),
        Field("Type", "text", "Client Type", "qbittorrent"),
        Field("Disabled", "checkbox", "Disabled", false),
        Field("Host", "text", "Host", "CHANGE_ME"),
        Field("Port", "number", "Port", 8080, minimum: 1, maximum: 65535),
        Field("UserName", "text", "Username", "CHANGE_ME"),
        Field("Password", "password", "Password", "CHANGE_ME", secure: true),
        Field("Maintenance.Enabled", "checkbox", "Maintenance Enabled", false),
        Field("Maintenance.Scope", "select", "Maintenance Scope", "managed",
            options: ["managed", "all", "explicit"]),
        Field("Maintenance.Schedule", "text", "Maintenance Schedule", "*/15 * * * *"),
        Field("Maintenance.PlanTtlMinutes", "number", "Preview TTL (minutes)", 30, minimum: 1),
        Collection("PathMappings", "Path mappings", "Translate torrent client paths into paths visible to Torrentarr. This does not mount folders or move files. The longest matching client prefix wins. Saved mappings apply at the next worker processing cycle.", [
            Item("ClientPath", "Torrent client path", "Folder as seen by the torrent client.", "/downloads"),
            Item("LocalPath", "Torrentarr path", "The same folder as seen by Torrentarr, including inside its Docker container.", "/data/downloads")
        ]),
    ];

    private static ConfigSchemaField Collection(string name, string label, string description, IReadOnlyList<ConfigSchemaField> items)
        => Field("Maintenance." + name, "collection", label, Array.Empty<object>()) with
        { description = description, itemFields = items, applyLive = true, requiresRestart = false };

    private static ConfigSchemaField Item(string key, string label, string description, string example)
        => Field(key, "text", label, "") with
        { description = description, required = true, placeholder = example, applyLive = null, requiresRestart = null };

    private static ConfigSchemaField Field(
        string key,
        string kind,
        string label,
        object? defaultValue,
        bool secure = false,
        IReadOnlyList<string>? options = null,
        double? minimum = null,
        double? maximum = null) => new(
            "TorrentClient",
            key.Split('.'),
            key,
            label,
            kind,
            defaultValue,
            "Torrentarr client-neutral torrent maintenance setting.",
            required: false,
            secure,
            uiExpose: true,
            applyLive: false,
            requiresRestart: true,
            options: options,
            minimum: minimum,
            maximum: maximum);
}

public sealed record ConfigSchemaField(
    string section,
    IReadOnlyList<string> path,
    string key,
    string label,
    string kind,
    object? @default,
    object comments,
    bool required,
    bool secure,
    bool uiExpose,
    bool? applyLive,
    bool? requiresRestart,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? options = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? description = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? placeholder = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? nativeUnit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool allowNegative = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? minimum = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? maximum = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? arrKinds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ConfigSchemaField>? itemFields = null);
