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
        Field("Maintenance.PathMappings", "tags", "Path Mappings", Array.Empty<string>()),
        Field("Maintenance.SharePolicies", "tags", "Share Policies", Array.Empty<string>()),
        Field("Maintenance.Notifications", "tags", "Notifications", Array.Empty<string>()),
    ];

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? arrKinds = null);
