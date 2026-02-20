using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Service for managing media searches and quality upgrades in Arr applications.
/// Supports Radarr, Sonarr, and Lidarr search operations.
/// </summary>
public class ArrMediaServiceSimple : IArrMediaService
{
    private readonly ILogger<ArrMediaServiceSimple> _logger;
    private readonly TorrentarrDbContext _dbContext;
    private readonly TorrentarrConfig _config;

    public ArrMediaServiceSimple(
        ILogger<ArrMediaServiceSimple> logger,
        TorrentarrDbContext dbContext,
        TorrentarrConfig config)
    {
        _logger = logger;
        _dbContext = dbContext;
        _config = config;
    }

    public async Task<SearchResult> SearchMissingMediaAsync(string category, CancellationToken cancellationToken = default)
    {
        var result = new SearchResult();

        var arrInstance = _config.ArrInstances.Values.FirstOrDefault(i =>
            i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (arrInstance == null)
        {
            _logger.LogWarning("No Arr instance found for category {Category}", category);
            return result;
        }

        try
        {
            var wanted = await GetWantedMediaAsync(category, cancellationToken);
            
            if (wanted.Count == 0)
            {
                _logger.LogDebug("No missing media found for category {Category}", category);
                return result;
            }

            _logger.LogInformation("Found {Count} missing media items for category {Category}", wanted.Count, category);

            var searchLimit = GetSearchLimit(arrInstance);
            var toSearch = wanted.Take(searchLimit).ToList();

            foreach (var media in toSearch)
            {
                try
                {
                    var searchTriggered = await TriggerSearchAsync(arrInstance, media, cancellationToken);
                    if (searchTriggered)
                    {
                        result.SearchesTriggered++;
                        result.SearchedIds.Add(media.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error searching for {Title} (ID: {Id})", media.Title, media.Id);
                }
            }

            _logger.LogInformation("Triggered {Count} searches for missing media in {Category}",
                result.SearchesTriggered, category);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching missing media for category {Category}", category);
        }

        return result;
    }

    public async Task<SearchResult> SearchQualityUpgradesAsync(string category, CancellationToken cancellationToken = default)
    {
        var result = new SearchResult();

        var arrInstance = _config.ArrInstances.Values.FirstOrDefault(i =>
            i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (arrInstance == null)
        {
            return result;
        }

        try
        {
            var client = CreateArrClient(arrInstance);
            if (client == null)
            {
                return result;
            }

            var queue = await GetQueueWithCustomFormatScoresAsync(arrInstance, client, cancellationToken);
            
            var upgradesAvailable = queue.Where(q => 
                q.CustomFormatScore.HasValue && q.CustomFormatScore.Value > 0).ToList();

            if (upgradesAvailable.Count == 0)
            {
                return result;
            }

            _logger.LogDebug("Found {Count} potential quality upgrades in {Category}",
                upgradesAvailable.Count, category);

            foreach (var item in upgradesAvailable.Take(5))
            {
                _logger.LogDebug("Quality upgrade available: {Title} (CF Score: {Score})",
                    item.Title, item.CustomFormatScore);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching quality upgrades for category {Category}", category);
        }

        return result;
    }

    public async Task<bool> IsQualityUpgradeAsync(int arrId, string quality, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        return false;
    }

    public async Task<List<WantedMedia>> GetWantedMediaAsync(string category, CancellationToken cancellationToken = default)
    {
        var wanted = new List<WantedMedia>();

        var arrInstance = _config.ArrInstances.Values.FirstOrDefault(i =>
            i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (arrInstance == null)
        {
            return wanted;
        }

        try
        {
            var client = CreateArrClient(arrInstance);
            if (client == null)
            {
                return wanted;
            }

            switch (arrInstance.Type.ToLower())
            {
                case "radarr":
                    var radarrClient = client as RadarrClient;
                    if (radarrClient != null)
                    {
                        wanted = await GetRadarrWantedAsync(radarrClient, cancellationToken);
                    }
                    break;

                case "sonarr":
                    var sonarrClient = client as SonarrClient;
                    if (sonarrClient != null)
                    {
                        wanted = await GetSonarrWantedAsync(sonarrClient, cancellationToken);
                    }
                    break;

                case "lidarr":
                    var lidarrClient = client as LidarrClient;
                    if (lidarrClient != null)
                    {
                        wanted = await GetLidarrWantedAsync(lidarrClient, cancellationToken);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting wanted media for category {Category}", category);
        }

        return wanted;
    }

    private object? CreateArrClient(ArrInstanceConfig arrInstance)
    {
        return arrInstance.Type.ToLower() switch
        {
            "radarr" => new RadarrClient(arrInstance.URI, arrInstance.APIKey),
            "sonarr" => new SonarrClient(arrInstance.URI, arrInstance.APIKey),
            "lidarr" => new LidarrClient(arrInstance.URI, arrInstance.APIKey),
            _ => null
        };
    }

    private async Task<List<WantedMedia>> GetRadarrWantedAsync(RadarrClient client, CancellationToken cancellationToken)
    {
        var wanted = new List<WantedMedia>();

        try
        {
            var movies = await client.GetMoviesAsync(cancellationToken);
            
            foreach (var movie in movies.Where(m => m.Monitored && !m.HasFile))
            {
                wanted.Add(new WantedMedia
                {
                    Id = movie.Id,
                    Title = movie.Title,
                    Year = movie.Year,
                    Monitored = movie.Monitored
                });
            }

            _logger.LogDebug("Found {Count} wanted movies in Radarr", wanted.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting wanted movies from Radarr");
        }

        return wanted;
    }

    private async Task<List<WantedMedia>> GetSonarrWantedAsync(SonarrClient client, CancellationToken cancellationToken)
    {
        var wanted = new List<WantedMedia>();

        try
        {
            var wantedResponse = await client.GetWantedAsync(pageSize: 100, ct: cancellationToken);
            
            foreach (var episode in wantedResponse.Records.Where(e => e.Monitored && !e.HasFile))
            {
                wanted.Add(new WantedMedia
                {
                    Id = episode.Id,
                    Title = episode.Title,
                    SeriesId = episode.SeriesId,
                    SeasonNumber = episode.SeasonNumber,
                    EpisodeNumber = episode.EpisodeNumber,
                    Monitored = episode.Monitored
                });
            }

            _logger.LogDebug("Found {Count} wanted episodes in Sonarr", wanted.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting wanted episodes from Sonarr");
        }

        return wanted;
    }

    private async Task<List<WantedMedia>> GetLidarrWantedAsync(LidarrClient client, CancellationToken cancellationToken)
    {
        var wanted = new List<WantedMedia>();

        try
        {
            var wantedResponse = await client.GetWantedAsync(pageSize: 100, ct: cancellationToken);
            
            foreach (var album in wantedResponse.Records.Where(a => a.Monitored))
            {
                wanted.Add(new WantedMedia
                {
                    Id = album.Id,
                    Title = album.Title,
                    ArtistId = album.ArtistId,
                    Monitored = album.Monitored
                });
            }

            _logger.LogDebug("Found {Count} wanted albums in Lidarr", wanted.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting wanted albums from Lidarr");
        }

        return wanted;
    }

    private async Task<bool> TriggerSearchAsync(ArrInstanceConfig arrInstance, WantedMedia media, CancellationToken cancellationToken)
    {
        try
        {
            switch (arrInstance.Type.ToLower())
            {
                case "radarr":
                    var radarrClient = new RadarrClient(arrInstance.URI, arrInstance.APIKey);
                    return await radarrClient.SearchMovieAsync(media.Id, cancellationToken);

                case "sonarr":
                    var sonarrClient = new SonarrClient(arrInstance.URI, arrInstance.APIKey);
                    return await sonarrClient.SearchEpisodeAsync(new List<int> { media.Id }, cancellationToken);

                case "lidarr":
                    var lidarrClient = new LidarrClient(arrInstance.URI, arrInstance.APIKey);
                    return await lidarrClient.SearchAlbumAsync(new List<int> { media.Id }, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering search for {Title}", media.Title);
        }

        return false;
    }

    private async Task<List<QueueItemWithScore>> GetQueueWithCustomFormatScoresAsync(
        ArrInstanceConfig arrInstance,
        object client,
        CancellationToken cancellationToken)
    {
        var items = new List<QueueItemWithScore>();

        try
        {
            switch (arrInstance.Type.ToLower())
            {
                case "radarr":
                    var radarrClient = client as RadarrClient;
                    if (radarrClient != null)
                    {
                        var queue = await radarrClient.GetQueueAsync(ct: cancellationToken);
                        items = queue.Records.Select(r => new QueueItemWithScore
                        {
                            Title = r.Title,
                            CustomFormatScore = r.CustomFormatScore
                        }).ToList();
                    }
                    break;

                case "sonarr":
                    var sonarrClient = client as SonarrClient;
                    if (sonarrClient != null)
                    {
                        var queue = await sonarrClient.GetQueueAsync(ct: cancellationToken);
                        items = queue.Records.Select(r => new QueueItemWithScore
                        {
                            Title = r.Title,
                            CustomFormatScore = r.CustomFormatScore
                        }).ToList();
                    }
                    break;

                case "lidarr":
                    var lidarrClient = client as LidarrClient;
                    if (lidarrClient != null)
                    {
                        var queue = await lidarrClient.GetQueueAsync(ct: cancellationToken);
                        items = queue.Records.Select(r => new QueueItemWithScore
                        {
                            Title = r.Title,
                            CustomFormatScore = r.CustomFormatScore
                        }).ToList();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting queue from {Type}", arrInstance.Type);
        }

        return items;
    }

    private int GetSearchLimit(ArrInstanceConfig arrInstance)
    {
        return arrInstance.Search.SearchLimit > 0 ? arrInstance.Search.SearchLimit : 5;
    }

    private class QueueItemWithScore
    {
        public string Title { get; set; } = "";
        public int? CustomFormatScore { get; set; }
    }
}
