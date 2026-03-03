using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// §1.2: Implements UseTempForMissing quality profile switching.
/// Before searching a missing item, temporarily switches its quality profile to the mapped
/// temp profile so qBitrr can grab any available quality. Restores after TempProfileResetTimeoutMinutes.
/// </summary>
public class QualityProfileSwitcherService
{
    private readonly ILogger<QualityProfileSwitcherService> _logger;
    private readonly TorrentarrDbContext _db;

    public QualityProfileSwitcherService(
        ILogger<QualityProfileSwitcherService> logger,
        TorrentarrDbContext db)
    {
        _logger = logger;
        _db = db;
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    /// <summary>
    /// If ForceResetTempProfiles = true, restores all items in the DB whose quality profile
    /// was switched but not restored (OriginalProfileId is set).
    /// Called once per instance on worker startup.
    /// </summary>
    public async Task ForceResetAllTempProfilesAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        CancellationToken ct = default)
    {
        if (!arrConfig.Search.ForceResetTempProfiles)
            return;

        _logger.LogInformation("§1.2 ForceResetTempProfiles: scanning {Instance} for switched profiles", instanceName);

        switch (arrConfig.Type.ToLowerInvariant())
        {
            case "radarr":
                var movies = await _db.Movies
                    .Where(m => m.ArrInstance == instanceName && m.OriginalProfileId.HasValue)
                    .ToListAsync(ct);

                if (movies.Count == 0) break;

                _logger.LogInformation("§1.2 ForceReset: restoring {Count} movie profiles for {Instance}", movies.Count, instanceName);
                var radarr = new RadarrClient(arrConfig.URI, arrConfig.APIKey);
                foreach (var movie in movies)
                {
                    await TryRestoreMovieAsync(radarr, movie.ArrId, movie.OriginalProfileId!.Value, instanceName, ct);
                    movie.CurrentProfileId = movie.OriginalProfileId;
                    movie.OriginalProfileId = null;
                    movie.LastProfileSwitchTime = null;
                }
                await _db.SaveChangesAsync(ct);
                break;

            case "sonarr":
                var series = await _db.Series
                    .Where(s => s.ArrInstance == instanceName && s.OriginalProfileId.HasValue)
                    .ToListAsync(ct);

                if (series.Count == 0) break;

                _logger.LogInformation("§1.2 ForceReset: restoring {Count} series profiles for {Instance}", series.Count, instanceName);
                var sonarr = new SonarrClient(arrConfig.URI, arrConfig.APIKey);
                foreach (var s in series)
                {
                    await TryRestoreSeriesAsync(sonarr, s.ArrId, s.OriginalProfileId!.Value, instanceName, ct);
                    s.CurrentProfileId = s.OriginalProfileId;
                    s.OriginalProfileId = null;
                    s.LastProfileSwitchTime = null;
                }
                await _db.SaveChangesAsync(ct);
                break;

            case "lidarr":
                var artists = await _db.Artists
                    .Where(a => a.ArrInstance == instanceName && a.OriginalProfileId.HasValue)
                    .ToListAsync(ct);

                if (artists.Count == 0) break;

                _logger.LogInformation("§1.2 ForceReset: restoring {Count} artist profiles for {Instance}", artists.Count, instanceName);
                var lidarr = new LidarrClient(arrConfig.URI, arrConfig.APIKey);
                foreach (var artist in artists)
                {
                    await TryRestoreArtistAsync(lidarr, artist.ArrId, artist.OriginalProfileId!.Value, instanceName, ct);
                    artist.CurrentProfileId = artist.OriginalProfileId;
                    artist.OriginalProfileId = null;
                    artist.LastProfileSwitchTime = null;
                }
                await _db.SaveChangesAsync(ct);
                break;
        }
    }

    // ── Per-cycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores quality profiles for items whose TempProfileResetTimeoutMinutes has elapsed.
    /// If KeepTempProfile = true, nothing is restored (profiles stay until manually reset).
    /// </summary>
    public async Task RestoreTimedOutProfilesAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        CancellationToken ct = default)
    {
        if (!arrConfig.Search.UseTempForMissing)
            return;

        if (arrConfig.Search.KeepTempProfile)
            return;

        var timeoutMinutes = arrConfig.Search.TempProfileResetTimeoutMinutes;
        if (timeoutMinutes <= 0)
            return;

        var cutoff = DateTime.UtcNow.AddMinutes(-timeoutMinutes);

        switch (arrConfig.Type.ToLowerInvariant())
        {
            case "radarr":
                var expiredMovies = await _db.Movies
                    .Where(m => m.ArrInstance == instanceName
                             && m.OriginalProfileId.HasValue
                             && m.LastProfileSwitchTime.HasValue
                             && m.LastProfileSwitchTime.Value < cutoff)
                    .ToListAsync(ct);

                if (expiredMovies.Count == 0) break;

                _logger.LogInformation("§1.2 RestoreTimedOut: restoring {Count} movie profiles for {Instance} (timeout={Timeout}min)",
                    expiredMovies.Count, instanceName, timeoutMinutes);
                var radarr = new RadarrClient(arrConfig.URI, arrConfig.APIKey);
                foreach (var movie in expiredMovies)
                {
                    await TryRestoreMovieAsync(radarr, movie.ArrId, movie.OriginalProfileId!.Value, instanceName, ct);
                    movie.CurrentProfileId = movie.OriginalProfileId;
                    movie.OriginalProfileId = null;
                    movie.LastProfileSwitchTime = null;
                }
                await _db.SaveChangesAsync(ct);
                break;

            case "sonarr":
                var expiredSeries = await _db.Series
                    .Where(s => s.ArrInstance == instanceName
                             && s.OriginalProfileId.HasValue
                             && s.LastProfileSwitchTime.HasValue
                             && s.LastProfileSwitchTime.Value < cutoff)
                    .ToListAsync(ct);

                if (expiredSeries.Count == 0) break;

                _logger.LogInformation("§1.2 RestoreTimedOut: restoring {Count} series profiles for {Instance}", expiredSeries.Count, instanceName);
                var sonarr = new SonarrClient(arrConfig.URI, arrConfig.APIKey);
                foreach (var s in expiredSeries)
                {
                    await TryRestoreSeriesAsync(sonarr, s.ArrId, s.OriginalProfileId!.Value, instanceName, ct);
                    s.CurrentProfileId = s.OriginalProfileId;
                    s.OriginalProfileId = null;
                    s.LastProfileSwitchTime = null;
                }
                await _db.SaveChangesAsync(ct);
                break;

            case "lidarr":
                var expiredArtists = await _db.Artists
                    .Where(a => a.ArrInstance == instanceName
                             && a.OriginalProfileId.HasValue
                             && a.LastProfileSwitchTime.HasValue
                             && a.LastProfileSwitchTime.Value < cutoff)
                    .ToListAsync(ct);

                if (expiredArtists.Count == 0) break;

                _logger.LogInformation("§1.2 RestoreTimedOut: restoring {Count} artist profiles for {Instance}", expiredArtists.Count, instanceName);
                var lidarr = new LidarrClient(arrConfig.URI, arrConfig.APIKey);
                foreach (var artist in expiredArtists)
                {
                    await TryRestoreArtistAsync(lidarr, artist.ArrId, artist.OriginalProfileId!.Value, instanceName, ct);
                    artist.CurrentProfileId = artist.OriginalProfileId;
                    artist.OriginalProfileId = null;
                    artist.LastProfileSwitchTime = null;
                }
                await _db.SaveChangesAsync(ct);
                break;
        }
    }

    /// <summary>
    /// For each missing search candidate, switch its quality profile to the mapped temp profile
    /// if QualityProfileMappings is configured and the item's current profile has a mapping.
    /// Skips items that are already switched (OriginalProfileId is set).
    /// </summary>
    public async Task SwitchToTempProfilesAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        IEnumerable<Core.Services.SearchCandidate> candidates,
        CancellationToken ct = default)
    {
        if (!arrConfig.Search.UseTempForMissing)
            return;

        if (arrConfig.Search.QualityProfileMappings.Count == 0)
            return;

        // Only switch for "Missing" reason — upgrades keep their current profile
        var missingCandidates = candidates
            .Where(c => c.Reason.Equals("Missing", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (missingCandidates.Count == 0)
            return;

        switch (arrConfig.Type.ToLowerInvariant())
        {
            case "radarr":
                await SwitchMovieProfilesAsync(instanceName, arrConfig, missingCandidates, ct);
                break;
            case "sonarr":
                await SwitchSeriesProfilesAsync(instanceName, arrConfig, missingCandidates, ct);
                break;
            case "lidarr":
                await SwitchArtistProfilesAsync(instanceName, arrConfig, missingCandidates, ct);
                break;
        }
    }

    // ── Per-type switch helpers ───────────────────────────────────────────────

    private async Task SwitchMovieProfilesAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        List<Core.Services.SearchCandidate> candidates,
        CancellationToken ct)
    {
        var radarr = new RadarrClient(arrConfig.URI, arrConfig.APIKey);
        var profiles = await radarr.GetQualityProfilesAsync(ct);
        // Build id→name and name→id maps for resolution
        var profilesById = profiles.ToDictionary(p => p.Id, p => p.Name);
        var profilesByName = profiles.ToDictionary(p => p.Name, p => p.Id, StringComparer.OrdinalIgnoreCase);

        var arrIds = candidates.Select(c => c.ArrId).ToHashSet();
        var movies = await _db.Movies
            .Where(m => m.ArrInstance == instanceName && arrIds.Contains(m.ArrId))
            .ToListAsync(ct);

        var changed = false;
        foreach (var movie in movies)
        {
            if (movie.OriginalProfileId.HasValue)
                continue; // already switched

            if (!movie.QualityProfileId.HasValue)
                continue;

            // Resolve current profile name from stored ID
            if (!profilesById.TryGetValue(movie.QualityProfileId.Value, out var currentProfileName))
                continue;

            if (!arrConfig.Search.QualityProfileMappings.TryGetValue(currentProfileName, out var tempProfileName))
                continue;

            if (!profilesByName.TryGetValue(tempProfileName, out var tempProfileId))
            {
                _logger.LogWarning("§1.2: Temp profile '{Name}' not found in Radarr for {Instance}", tempProfileName, instanceName);
                continue;
            }

            var switched = await radarr.UpdateMovieQualityProfileAsync(movie.ArrId, tempProfileId, ct);
            if (switched)
            {
                _logger.LogInformation("§1.2: Switched movie '{Title}' profile: {From} → {To}", movie.Title, currentProfileName, tempProfileName);
                movie.OriginalProfileId = movie.QualityProfileId;
                movie.CurrentProfileId = tempProfileId;
                movie.LastProfileSwitchTime = DateTime.UtcNow;
                changed = true;
            }
        }

        if (changed)
            await _db.SaveChangesAsync(ct);
    }

    private async Task SwitchSeriesProfilesAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        List<Core.Services.SearchCandidate> candidates,
        CancellationToken ct)
    {
        var sonarr = new SonarrClient(arrConfig.URI, arrConfig.APIKey);
        var profiles = await sonarr.GetQualityProfilesAsync(ct);
        var profilesById = profiles.ToDictionary(p => p.Id, p => p.Name);
        var profilesByName = profiles.ToDictionary(p => p.Name, p => p.Id, StringComparer.OrdinalIgnoreCase);

        // For Sonarr, switch by series (SeriesId from candidate)
        var seriesIds = candidates
            .Where(c => c.SeriesId.HasValue)
            .Select(c => c.SeriesId!.Value)
            .ToHashSet();

        var seriesList = await _db.Series
            .Where(s => s.ArrInstance == instanceName && seriesIds.Contains(s.ArrId))
            .ToListAsync(ct);

        var changed = false;
        foreach (var series in seriesList)
        {
            if (series.OriginalProfileId.HasValue)
                continue;

            if (!series.QualityProfileId.HasValue)
                continue;

            if (!profilesById.TryGetValue(series.QualityProfileId.Value, out var currentProfileName))
                continue;

            if (!arrConfig.Search.QualityProfileMappings.TryGetValue(currentProfileName, out var tempProfileName))
                continue;

            if (!profilesByName.TryGetValue(tempProfileName, out var tempProfileId))
            {
                _logger.LogWarning("§1.2: Temp profile '{Name}' not found in Sonarr for {Instance}", tempProfileName, instanceName);
                continue;
            }

            var switched = await sonarr.UpdateSeriesQualityProfileAsync(series.ArrId, tempProfileId, ct);
            if (switched)
            {
                _logger.LogInformation("§1.2: Switched series '{Title}' profile: {From} → {To}",
                    series.Title ?? series.ArrId.ToString(), currentProfileName, tempProfileName);
                series.OriginalProfileId = series.QualityProfileId;
                series.CurrentProfileId = tempProfileId;
                series.LastProfileSwitchTime = DateTime.UtcNow;
                changed = true;
            }
        }

        if (changed)
            await _db.SaveChangesAsync(ct);
    }

    private async Task SwitchArtistProfilesAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        List<Core.Services.SearchCandidate> candidates,
        CancellationToken ct)
    {
        // Lidarr quality profiles are on the artist, not the album
        var lidarr = new LidarrClient(arrConfig.URI, arrConfig.APIKey);
        var profiles = await lidarr.GetQualityProfilesAsync(ct);
        var profilesById = profiles.ToDictionary(p => p.Id, p => p.Name);
        var profilesByName = profiles.ToDictionary(p => p.Name, p => p.Id, StringComparer.OrdinalIgnoreCase);

        var artistIds = candidates
            .Where(c => c.ArtistId.HasValue)
            .Select(c => c.ArtistId!.Value)
            .ToHashSet();

        var artists = await _db.Artists
            .Where(a => a.ArrInstance == instanceName && artistIds.Contains(a.ArrId))
            .ToListAsync(ct);

        var changed = false;
        foreach (var artist in artists)
        {
            if (artist.OriginalProfileId.HasValue)
                continue;

            if (!artist.QualityProfileId.HasValue)
                continue;

            if (!profilesById.TryGetValue(artist.QualityProfileId.Value, out var currentProfileName))
                continue;

            if (!arrConfig.Search.QualityProfileMappings.TryGetValue(currentProfileName, out var tempProfileName))
                continue;

            if (!profilesByName.TryGetValue(tempProfileName, out var tempProfileId))
            {
                _logger.LogWarning("§1.2: Temp profile '{Name}' not found in Lidarr for {Instance}", tempProfileName, instanceName);
                continue;
            }

            var switched = await lidarr.UpdateArtistQualityProfileAsync(artist.ArrId, tempProfileId, ct);
            if (switched)
            {
                _logger.LogInformation("§1.2: Switched artist '{Name}' profile: {From} → {To}",
                    artist.Title ?? artist.ArrId.ToString(), currentProfileName, tempProfileName);
                artist.OriginalProfileId = artist.QualityProfileId;
                artist.CurrentProfileId = tempProfileId;
                artist.LastProfileSwitchTime = DateTime.UtcNow;
                changed = true;
            }
        }

        if (changed)
            await _db.SaveChangesAsync(ct);
    }

    // ── Restore helpers ───────────────────────────────────────────────────────

    private async Task TryRestoreMovieAsync(RadarrClient radarr, int arrId, int originalProfileId, string instanceName, CancellationToken ct)
    {
        try
        {
            await radarr.UpdateMovieQualityProfileAsync(arrId, originalProfileId, ct);
            _logger.LogInformation("§1.2: Restored movie {ArrId} → profileId={ProfileId} for {Instance}", arrId, originalProfileId, instanceName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "§1.2: Failed to restore movie {ArrId} for {Instance}", arrId, instanceName);
        }
    }

    private async Task TryRestoreSeriesAsync(SonarrClient sonarr, int arrId, int originalProfileId, string instanceName, CancellationToken ct)
    {
        try
        {
            await sonarr.UpdateSeriesQualityProfileAsync(arrId, originalProfileId, ct);
            _logger.LogInformation("§1.2: Restored series {ArrId} → profileId={ProfileId} for {Instance}", arrId, originalProfileId, instanceName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "§1.2: Failed to restore series {ArrId} for {Instance}", arrId, instanceName);
        }
    }

    private async Task TryRestoreArtistAsync(LidarrClient lidarr, int arrId, int originalProfileId, string instanceName, CancellationToken ct)
    {
        try
        {
            await lidarr.UpdateArtistQualityProfileAsync(arrId, originalProfileId, ct);
            _logger.LogInformation("§1.2: Restored artist {ArrId} → profileId={ProfileId} for {Instance}", arrId, originalProfileId, instanceName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "§1.2: Failed to restore artist {ArrId} for {Instance}", arrId, instanceName);
        }
    }
}
