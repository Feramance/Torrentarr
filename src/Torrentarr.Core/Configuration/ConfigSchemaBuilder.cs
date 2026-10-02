using System.Text.Json.Serialization;

namespace Torrentarr.Core.Configuration;

/// <summary>
/// qBitrr 5.14.5 configuration registry used by the schema API and WebUI code generation.
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
            ["Settings"] = Qbitrr5145ConfigSchema.Settings,
            ["WebUI"] = Qbitrr5145ConfigSchema.WebUI,
            ["qBit"] = Qbitrr5145ConfigSchema.qBit,
            ["Arr"] = Qbitrr5145ConfigSchema.Arr
        };
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
