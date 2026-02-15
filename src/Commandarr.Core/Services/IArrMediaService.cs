namespace Commandarr.Core.Services;

/// <summary>
/// Service for managing media searches and quality upgrades in Arr applications
/// </summary>
public interface IArrMediaService
{
    /// <summary>
    /// Search for missing media in the Arr instance
    /// </summary>
    Task<SearchResult> SearchMissingMediaAsync(string category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search for quality upgrades for existing media
    /// </summary>
    Task<SearchResult> SearchQualityUpgradesAsync(string category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a torrent qualifies for a quality upgrade
    /// </summary>
    Task<bool> IsQualityUpgradeAsync(int arrId, string quality, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get wanted/missing media from Arr instance
    /// </summary>
    Task<List<WantedMedia>> GetWantedMediaAsync(string category, CancellationToken cancellationToken = default);
}

public class SearchResult
{
    public int ItemsSearched { get; set; }
    public int SearchesTriggered { get; set; }
    public List<string> Errors { get; set; } = new();
    public DateTime SearchTime { get; set; } = DateTime.UtcNow;
}

public class WantedMedia
{
    public int ArrId { get; set; }
    public string Title { get; set; } = "";
    public string Type { get; set; } = ""; // Movie, Series, Album
    public string Quality { get; set; } = "";
    public DateTime Added { get; set; }
    public bool Monitored { get; set; }
}
