using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;

namespace Torrentarr.Infrastructure.Services;

internal sealed record SearchLibraryEvidence(string[]? Tags, bool? Available);

internal sealed class SearchLibraryGates(IReadOnlyDictionary<int, SearchLibraryEvidence>? entries)
{
    internal static bool IsTagExclusion(string? reason) =>
        reason is "Owning-library tag excluded" or "No included owning-library tag";

    internal string? Hold(int itemId, int? ownerId, ArrInstanceConfig arr)
    {
        var settings = arr.Search;
        var tagsEnabled = settings.IncludeTags.Count > 0 || settings.ExcludeTags.Count > 0;
        if (!tagsEnabled && !settings.RequireRadarrAvailability) return null;
        var id = arr.Type.Equals("radarr", StringComparison.OrdinalIgnoreCase) ? itemId : ownerId ?? 0;
        var entry = entries?.GetValueOrDefault(id);
        if (tagsEnabled)
        {
            if (entry?.Tags == null) return "Owning-library tag evidence unavailable";
            if (entry.Tags.Intersect(settings.ExcludeTags, StringComparer.OrdinalIgnoreCase).Any())
                return "Owning-library tag excluded";
            if (settings.IncludeTags.Count > 0 && !entry.Tags.Intersect(settings.IncludeTags, StringComparer.OrdinalIgnoreCase).Any())
                return "No included owning-library tag";
        }
        if (settings.RequireRadarrAvailability)
        {
            if (!arr.Type.Equals("radarr", StringComparison.OrdinalIgnoreCase) || entry?.Available == null)
                return "Radarr availability evidence unavailable";
            if (entry.Available != true) return "Radarr reports movie unavailable";
        }
        return null;
    }

    internal static bool Enabled(SearchConfig settings) => settings.IncludeTags.Count > 0
        || settings.ExcludeTags.Count > 0 || settings.RequireRadarrAvailability;

    internal static async Task<SearchLibraryGates> ObserveAsync(ArrInstanceConfig arr, CancellationToken ct,
        Func<ArrInstanceConfig, string, CancellationToken, Task<JArray>>? read = null)
    {
        if (!Enabled(arr.Search)) return new(null);
        read ??= ArrActivityCollector.ReadAsync;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var resource = arr.Type.ToLowerInvariant() switch
            { "radarr" => "movie", "sonarr" => "series", "lidarr" => "artist", "readarr" => "author", _ => null };
            if (resource == null) return new(null);
            var tagsNeeded = arr.Search.IncludeTags.Count > 0 || arr.Search.ExcludeTags.Count > 0;
            var tags = tagsNeeded ? await read(arr, "tag", timeout.Token) : [];
            var entries = await read(arr, resource, timeout.Token);
            return Parse(entries, tags, tagsNeeded);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(null); }
    }

    internal static SearchLibraryGates Parse(JArray entries, JArray tags, bool tagsNeeded)
    {
        if (entries.Count > 200000 || tags.Count > 10000) return new(null);
        var labels = new Dictionary<int, string>();
        foreach (var token in tags)
        {
            if (token is not JObject tag || tag["id"]?.Type != JTokenType.Integer
                || tag["id"]!.Value<int>() <= 0 || tag["label"]?.Type != JTokenType.String
                || string.IsNullOrWhiteSpace(tag["label"]!.Value<string>())
                || !labels.TryAdd(tag["id"]!.Value<int>(), tag["label"]!.Value<string>()!)) return new(null);
        }
        var result = new Dictionary<int, SearchLibraryEvidence>();
        foreach (var token in entries)
        {
            if (token is not JObject entry || entry["id"]?.Type != JTokenType.Integer
                || entry["id"]!.Value<int>() <= 0) return new(null);
            string[]? names = null;
            if (tagsNeeded && entry["tags"] is JArray values
                && values.All(value => value.Type == JTokenType.Integer && labels.ContainsKey(value.Value<int>())))
                names = values.Select(value => labels[value.Value<int>()]).ToArray();
            var available = entry["isAvailable"]?.Type == JTokenType.Boolean ? entry["isAvailable"]!.Value<bool>() : (bool?)null;
            if (!result.TryAdd(entry["id"]!.Value<int>(), new(names, available))) return new(null);
        }
        return new(result);
    }
}
