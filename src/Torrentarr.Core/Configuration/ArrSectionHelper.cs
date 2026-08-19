namespace Torrentarr.Core.Configuration;

/// <summary>
/// Identifies Arr config sections and type-specific defaults (Radarr/Sonarr/Lidarr/Readarr).
/// </summary>
public static class ArrSectionHelper
{
    public static readonly string[] ArrTypes = ["radarr", "sonarr", "lidarr", "readarr"];

    /// <summary>True when <paramref name="sectionName"/> is an Arr instance section (e.g. <c>Readarr-Books</c>).</summary>
    public static bool IsArrSection(string? sectionName) =>
        ArrTypeFromSectionName(sectionName) != null;

    /// <summary>
    /// Returns <c>radarr</c>/<c>sonarr</c>/<c>lidarr</c>/<c>readarr</c> for matching section names,
    /// including bare names (<c>Radarr</c>) and prefixed names (<c>Radarr-4K</c>).
    /// </summary>
    public static string? ArrTypeFromSectionName(string? sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
            return null;

        var lower = sectionName.Trim().ToLowerInvariant();
        foreach (var type in ArrTypes)
        {
            if (lower == type || lower.StartsWith(type + "-", StringComparison.Ordinal))
                return type;
        }

        return null;
    }

    /// <summary>Ombi/Overseerr request integration is Radarr/Sonarr only.</summary>
    public static bool SupportsRequestIntegration(string? arrType) =>
        arrType is "radarr" or "sonarr";

    /// <summary>SearchByYear applies to all Arr types except Lidarr.</summary>
    public static bool SupportsSearchByYear(string? arrType) =>
        !string.Equals(arrType, "lidarr", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> DefaultFileExtensionAllowlist(string? arrType) =>
        arrType?.ToLowerInvariant() switch
        {
            "lidarr" => LidarrAllowlist,
            "readarr" => ReadarrAllowlist,
            _ => VideoAllowlist
        };

    /// <summary>qBitrr original unmodified ebook-only Readarr default (pre-audiobook expansion).</summary>
    public static readonly string[] ReadarrEbookOnlyAllowlist =
    [
        ".epub", ".kepub", ".mobi", ".azw", ".azw3", ".pdf", ".cbz", ".cbr", ".!qB", ".parts"
    ];

    public static readonly string[] ReadarrAllowlist =
    [
        ".epub", ".kepub", ".mobi", ".azw", ".azw3", ".pdf", ".cbz", ".cbr",
        ".flac", ".ape", ".wavpack", ".wav", ".alac",
        ".mp2", ".mp3", ".wma", ".m4a", ".m4p", ".m4b", ".aac", ".mp4a",
        ".ogg", ".oga", ".vorbis",
        ".!qB", ".parts"
    ];

    public static readonly string[] LidarrAllowlist =
    [
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".ape", ".wma",
        ".!qB", ".parts"
    ];

    public static readonly string[] VideoAllowlist =
    [
        ".mp4", ".mkv", ".sub", ".ass", ".srt", ".!qB", ".parts"
    ];

    public static readonly string[] EbookComicExtensions =
    [
        ".epub", ".kepub", ".mobi", ".azw", ".azw3", ".pdf", ".cbz", ".cbr"
    ];

    public static bool IsEbookOrComicExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return false;
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return EbookComicExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> DefaultFolderExclusionRegex(string? arrType, string? category = null)
    {
        var haystack = $"{arrType} {category}".ToLowerInvariant();
        if (haystack.Contains("anime"))
        {
            return
            [
                @"\bextras?\b",
                @"\bfeaturettes?\b",
                @"\bsamples?\b",
                @"\bscreens?\b",
                @"\bspecials?\b",
                @"\bova\b",
                @"\bnc(ed|op)?(\d+)?\b",
            ];
        }

        if (haystack.Contains("lidarr") || haystack.Contains("readarr"))
        {
            return
            [
                @"\bextras?\b",
                @"\bsamples?\b",
                @"\bscreens?\b",
            ];
        }

        return
        [
            @"\bextras?\b",
            @"\bfeaturettes?\b",
            @"\bsamples?\b",
            @"\bscreens?\b",
            @"\bnc(ed|op)?(\d+)?\b",
        ];
    }

    public static IReadOnlyList<string> DefaultFileNameExclusionRegex(string? arrType, string? category = null)
    {
        var haystack = $"{arrType} {category}".ToLowerInvariant();
        if (haystack.Contains("lidarr") || haystack.Contains("readarr"))
        {
            return
            [
                @"\bsample\b",
                @"brarbg.com\b",
                @"\btrailer\b",
                "comandotorrents.com",
            ];
        }

        return
        [
            @"\bncop\d+?\b",
            @"\bnced\d+?\b",
            @"\bsample\b",
            @"brarbg.com\b",
            @"\btrailer\b",
            "music video",
            "comandotorrents.com",
        ];
    }

    /// <summary>True when the list matches the original ebook-only default (order-insensitive).</summary>
    public static bool IsUnmodifiedReadarrEbookOnlyAllowlist(IEnumerable<string>? extensions)
    {
        if (extensions == null)
            return false;
        var set = extensions
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.SetEquals(ReadarrEbookOnlyAllowlist);
    }
}
