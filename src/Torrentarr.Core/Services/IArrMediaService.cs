namespace Torrentarr.Core.Services;

public interface IArrMediaService
{
    Task<SearchResult> SearchMissingMediaAsync(string category, CancellationToken cancellationToken = default);

    Task<SearchResult> SearchQualityUpgradesAsync(string category, CancellationToken cancellationToken = default);

    Task<bool> IsQualityUpgradeAsync(int arrId, string quality, CancellationToken cancellationToken = default);

    Task<List<WantedMedia>> GetWantedMediaAsync(string category, CancellationToken cancellationToken = default);

    Task<QualityUpgradeResult> GetCustomFormatUnmetMediaAsync(string category, CancellationToken cancellationToken = default);
}

public class SearchResult
{
    public int ItemsSearched { get; set; }
    public int SearchesTriggered { get; set; }
    public List<int> SearchedIds { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public DateTime SearchTime { get; set; } = DateTime.UtcNow;
}

public class WantedMedia
{
    public int Id { get; set; }
    public int ArrId { get; set; }
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public string Quality { get; set; } = "";
    public int Year { get; set; }
    public int SeriesId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public int ArtistId { get; set; }
    public DateTime Added { get; set; }
    public bool Monitored { get; set; }
}

public class QualityUpgradeResult
{
    public List<CustomFormatUnmetItem> UnmetMedia { get; set; } = new();
    public int TotalUnmet => UnmetMedia.Count;
}

public class CustomFormatUnmetItem
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public int CurrentCustomFormatScore { get; set; }
    public int MinCustomFormatScore { get; set; }
    public int QualityProfileId { get; set; }
    public string QualityProfileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int? SeriesId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public int? ArtistId { get; set; }
}
