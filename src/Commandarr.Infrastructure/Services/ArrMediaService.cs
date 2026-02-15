using Commandarr.Core.Configuration;
using Commandarr.Core.Services;
using Commandarr.Infrastructure.ApiClients.Arr;
using Commandarr.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Commandarr.Infrastructure.Services;

/// <summary>
/// Simplified service for managing media searches and quality upgrades in Arr applications
/// </summary>
public class ArrMediaServiceSimple : IArrMediaService
{
    private readonly ILogger<ArrMediaServiceSimple> _logger;
    private readonly CommandarrDbContext _dbContext;
    private readonly CommandarrConfig _config;

    public ArrMediaServiceSimple(
        ILogger<ArrMediaServiceSimple> logger,
        CommandarrDbContext dbContext,
        CommandarrConfig config)
    {
        _logger = logger;
        _dbContext = dbContext;
        _config = config;
    }

    public async Task<SearchResult> SearchMissingMediaAsync(string category, CancellationToken cancellationToken = default)
    {
        var result = new SearchResult();

        // TODO: Implement search logic
        _logger.LogInformation("Searching missing media for category {Category}", category);

        await Task.CompletedTask;
        return result;
    }

    public async Task<SearchResult> SearchQualityUpgradesAsync(string category, CancellationToken cancellationToken = default)
    {
        var result = new SearchResult();

        // TODO: Implement upgrade search logic
        _logger.LogInformation("Searching quality upgrades for category {Category}", category);

        await Task.CompletedTask;
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

        // TODO: Implement get wanted media logic
        _logger.LogInformation("Getting wanted media for category {Category}", category);

        await Task.CompletedTask;
        return wanted;
    }
}
