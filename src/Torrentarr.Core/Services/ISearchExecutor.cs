namespace Torrentarr.Core.Services;

public interface ISearchExecutor
{
    Task<SearchResult> ExecuteSearchesAsync(
        string instanceName,
        IEnumerable<SearchCandidate> candidates,
        CancellationToken cancellationToken = default);

    Task<int> GetActiveCommandCountAsync(string instanceName, CancellationToken cancellationToken = default);

    bool CanSearch(int activeCommandCount, int searchLimit);
}

public class SearchCandidate
{
    public int ArrId { get; set; }
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public string Reason { get; set; } = "";
    public int Priority { get; set; }
    public int? SeriesId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public int? ArtistId { get; set; }
    public int? AlbumId { get; set; }
    public int? Year { get; set; }
    public DateTime? AirDate { get; set; }
    public bool IsTodaysRelease { get; set; }
}
