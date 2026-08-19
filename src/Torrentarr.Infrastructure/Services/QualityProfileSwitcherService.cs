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
    private readonly DatabaseRestartCoordinator _restartCoordinator;

    public QualityProfileSwitcherService(
        ILogger<QualityProfileSwitcherService> logger,
        TorrentarrDbContext db,
        DatabaseRestartCoordinator restartCoordinator)
    {
        _logger = logger;
        _db = db;
        _restartCoordinator = restartCoordinator;
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
                var radarr = new RadarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify);
                foreach (var movie in movies)
                {
                    if (!await TryRestoreMovieAsync(radarr, movie.ArrId, movie.OriginalProfileId!.Value, instanceName, arrConfig, ct))
                        continue;
                    movie.CurrentProfileId = movie.OriginalProfileId;
                    movie.OriginalProfileId = null;
                    movie.LastProfileSwitchTime = null;
                }
                await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: ct);
                break;

            case "sonarr":
                var series = await _db.Series
                    .Where(s => s.ArrInstance == instanceName && s.OriginalProfileId.HasValue)
                    .ToListAsync(ct);

                if (series.Count == 0) break;

                _logger.LogInformation("§1.2 ForceReset: restoring {Count} series profiles for {Instance}", series.Count, instanceName);
                var sonarr = new SonarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify);
                foreach (var s in series)
                {
                    if (!await TryRestoreSeriesAsync(sonarr, s.ArrId, s.OriginalProfileId!.Value, instanceName, arrConfig, ct))
                        continue;
                    s.CurrentProfileId = s.OriginalProfileId;
                    s.OriginalProfileId = null;
                    s.LastProfileSwitchTime = null;
                }
                await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: ct);
                break;

            case "lidarr":
                var artists = await _db.Artists
                    .Where(a => a.ArrInstance == instanceName && a.OriginalProfileId.HasValue)
                    .ToListAsync(ct);

                if (artists.Count == 0) break;

                _logger.LogInformation("§1.2 ForceReset: restoring {Count} artist profiles for {Instance}", artists.Count, instanceName);
                var lidarr = new LidarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify);
                foreach (var artist in artists)
                {
                    if (!await TryRestoreArtistAsync(lidarr, artist.ArrId, artist.OriginalProfileId!.Value, instanceName, arrConfig, ct))
                        continue;
                    artist.CurrentProfileId = artist.OriginalProfileId;
                    artist.OriginalProfileId = null;
                    artist.LastProfileSwitchTime = null;
                }
                await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: ct);
                break;

            case "readarr":
                var authors = await _db.Authors
                    .Where(a => a.ArrInstance == instanceName && a.OriginalProfileId.HasValue)
                    .ToListAsync(ct);

                if (authors.Count == 0) break;

                _logger.LogInformation("§1.2 ForceReset: restoring {Count} author profiles for {Instance}", authors.Count, instanceName);
                var readarr = new ReadarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify);
                foreach (var author in authors)
                {
                    if (!await TryRestoreAuthorAsync(readarr, author.ArrId, author.OriginalProfileId!.Value, instanceName, arrConfig, ct))
                        continue;
                    author.CurrentProfileId = author.OriginalProfileId;
                    author.OriginalProfileId = null;
                    author.LastProfileSwitchTime = null;
                }
                await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: ct);
                break;
        }
    }

    /// <summary>
    /// Restores quality profiles for items whose TempProfileResetTimeoutMinutes has elapsed.
    /// KeepTempProfile skips immediate restore after a search; timeout reset still applies (qBitrr).
    /// </summary>
    public async Task RestoreTimedOutProfilesAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        CancellationToken ct = default)
    {
        if (!arrConfig.Search.UseTempForMissing)
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
                var radarr = new RadarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify);
                foreach (var movie in expiredMovies)
                {
                    if (!await TryRestoreMovieAsync(radarr, movie.ArrId, movie.OriginalProfileId!.Value, instanceName, arrConfig, ct))
                        continue;
                    movie.CurrentProfileId = movie.OriginalProfileId;
                    movie.OriginalProfileId = null;
                    movie.LastProfileSwitchTime = null;
                }
                await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: ct);
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
                var sonarr = new SonarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify);
                foreach (var s in expiredSeries)
                {
                    if (!await TryRestoreSeriesAsync(sonarr, s.ArrId, s.OriginalProfileId!.Value, instanceName, arrConfig, ct))
                        continue;
                    s.CurrentProfileId = s.OriginalProfileId;
                    s.OriginalProfileId = null;
                    s.LastProfileSwitchTime = null;
                }
                await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: ct);
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
                var lidarr = new LidarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify);
                foreach (var artist in expiredArtists)
                {
                    if (!await TryRestoreArtistAsync(lidarr, artist.ArrId, artist.OriginalProfileId!.Value, instanceName, arrConfig, ct))
                        continue;
                    artist.CurrentProfileId = artist.OriginalProfileId;
                    artist.OriginalProfileId = null;
                    artist.LastProfileSwitchTime = null;
                }
                await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: ct);
                break;

            case "readarr":
                var expiredAuthors = await _db.Authors
                    .Where(a => a.ArrInstance == instanceName
                             && a.OriginalProfileId.HasValue
                             && a.LastProfileSwitchTime.HasValue
                             && a.LastProfileSwitchTime.Value < cutoff)
                    .ToListAsync(ct);

                if (expiredAuthors.Count == 0) break;

                _logger.LogInformation("§1.2 RestoreTimedOut: restoring {Count} author profiles for {Instance}", expiredAuthors.Count, instanceName);
                var readarr = new ReadarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify);
                foreach (var author in expiredAuthors)
                {
                    if (!await TryRestoreAuthorAsync(readarr, author.ArrId, author.OriginalProfileId!.Value, instanceName, arrConfig, ct))
                        continue;
                    author.CurrentProfileId = author.OriginalProfileId;
                    author.OriginalProfileId = null;
                    author.LastProfileSwitchTime = null;
                }
                await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: ct);
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
            case "readarr":
                await SwitchAuthorProfilesAsync(instanceName, arrConfig, missingCandidates, ct);
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
        var radarr = new RadarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify);
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

            var switched = await WithProfileSwitchRetryAsync(
                arrConfig,
                () => radarr.UpdateMovieQualityProfileAsync(movie.ArrId, tempProfileId, ct),
                "movie",
                ct);
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
            await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: ct);
    }

    private async Task SwitchSeriesProfilesAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        List<Core.Services.SearchCandidate> candidates,
        CancellationToken ct)
    {
        var sonarr = new SonarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify);
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

            var switched = await WithProfileSwitchRetryAsync(
                arrConfig,
                () => sonarr.UpdateSeriesQualityProfileAsync(series.ArrId, tempProfileId, ct),
                "series",
                ct);
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
            await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: ct);
    }

    private async Task SwitchArtistProfilesAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        List<Core.Services.SearchCandidate> candidates,
        CancellationToken ct)
    {
        // Lidarr quality profiles are on the artist, not the album
        var lidarr = new LidarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify);
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

            var switched = await WithProfileSwitchRetryAsync(
                arrConfig,
                () => lidarr.UpdateArtistQualityProfileAsync(artist.ArrId, tempProfileId, ct),
                "artist",
                ct);
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
            await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: ct);
    }

    // ── Restore helpers ───────────────────────────────────────────────────────

    private Task<bool> TryRestoreMovieAsync(RadarrClient radarr, int arrId, int originalProfileId, string instanceName, ArrInstanceConfig arrConfig, CancellationToken ct)
    {
        return WithProfileSwitchRetryAsync(
            arrConfig,
            async () =>
            {
                var ok = await radarr.UpdateMovieQualityProfileAsync(arrId, originalProfileId, ct);
                if (ok)
                    _logger.LogInformation("§1.2: Restored movie {ArrId} → profileId={ProfileId} for {Instance}", arrId, originalProfileId, instanceName);
                return ok;
            },
            "movie-restore",
            ct);
    }

    private Task<bool> TryRestoreSeriesAsync(SonarrClient sonarr, int arrId, int originalProfileId, string instanceName, ArrInstanceConfig arrConfig, CancellationToken ct)
    {
        return WithProfileSwitchRetryAsync(
            arrConfig,
            async () =>
            {
                var ok = await sonarr.UpdateSeriesQualityProfileAsync(arrId, originalProfileId, ct);
                if (ok)
                    _logger.LogInformation("§1.2: Restored series {ArrId} → profileId={ProfileId} for {Instance}", arrId, originalProfileId, instanceName);
                return ok;
            },
            "series-restore",
            ct);
    }

    private Task<bool> TryRestoreArtistAsync(LidarrClient lidarr, int arrId, int originalProfileId, string instanceName, ArrInstanceConfig arrConfig, CancellationToken ct)
    {
        return WithProfileSwitchRetryAsync(
            arrConfig,
            async () =>
            {
                var ok = await lidarr.UpdateArtistQualityProfileAsync(arrId, originalProfileId, ct);
                if (ok)
                    _logger.LogInformation("§1.2: Restored artist {ArrId} → profileId={ProfileId} for {Instance}", arrId, originalProfileId, instanceName);
                return ok;
            },
            "artist-restore",
            ct);
    }

    private Task<bool> TryRestoreAuthorAsync(ReadarrClient readarr, int arrId, int originalProfileId, string instanceName, ArrInstanceConfig arrConfig, CancellationToken ct)
    {
        return WithProfileSwitchRetryAsync(
            arrConfig,
            async () =>
            {
                var ok = await readarr.UpdateAuthorQualityProfileAsync(arrId, originalProfileId, ct);
                if (ok)
                    _logger.LogInformation("§1.2: Restored author {ArrId} → profileId={ProfileId} for {Instance}", arrId, originalProfileId, instanceName);
                return ok;
            },
            "author-restore",
            ct);
    }

    private async Task SwitchAuthorProfilesAsync(
        string instanceName,
        ArrInstanceConfig arrConfig,
        List<Core.Services.SearchCandidate> candidates,
        CancellationToken ct)
    {
        var readarr = new ReadarrClient(arrConfig.URI, arrConfig.APIKey, arrConfig.SkipTLSVerify);
        var profiles = await readarr.GetQualityProfilesAsync(ct);
        var profilesById = profiles.ToDictionary(p => p.Id, p => p.Name);
        var profilesByName = profiles.ToDictionary(p => p.Name, p => p.Id, StringComparer.OrdinalIgnoreCase);

        var authorIds = candidates
            .Where(c => c.AuthorId.HasValue)
            .Select(c => c.AuthorId!.Value)
            .ToHashSet();

        var authors = await _db.Authors
            .Where(a => a.ArrInstance == instanceName && authorIds.Contains(a.ArrId))
            .ToListAsync(ct);

        var changed = false;
        foreach (var author in authors)
        {
            if (author.OriginalProfileId.HasValue)
                continue;

            if (!author.QualityProfileId.HasValue)
                continue;

            if (!profilesById.TryGetValue(author.QualityProfileId.Value, out var currentProfileName))
                continue;

            if (!arrConfig.Search.QualityProfileMappings.TryGetValue(currentProfileName, out var tempProfileName))
                continue;

            if (!profilesByName.TryGetValue(tempProfileName, out var tempProfileId))
            {
                _logger.LogWarning("§1.2: Temp profile '{Name}' not found in Readarr for {Instance}", tempProfileName, instanceName);
                continue;
            }

            var switched = await WithProfileSwitchRetryAsync(
                arrConfig,
                () => readarr.UpdateAuthorQualityProfileAsync(author.ArrId, tempProfileId, ct),
                "author",
                ct);
            if (switched)
            {
                _logger.LogInformation("§1.2: Switched author '{Name}' profile: {From} → {To}",
                    author.Title ?? author.ArrId.ToString(), currentProfileName, tempProfileName);
                author.OriginalProfileId = author.QualityProfileId;
                author.CurrentProfileId = tempProfileId;
                author.LastProfileSwitchTime = DateTime.UtcNow;
                changed = true;
            }
        }

        if (changed)
            await _db.SaveChangesWithRetryAsync(_logger, _restartCoordinator, cancellationToken: ct);
    }

    private async Task<bool> WithProfileSwitchRetryAsync(
        ArrInstanceConfig arrConfig,
        Func<Task<bool>> action,
        string kind,
        CancellationToken ct)
    {
        var attempts = Math.Max(1, arrConfig.Search.ProfileSwitchRetryAttempts);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < attempts - 1)
            {
                _logger.LogWarning(ex, "Profile switch retry {Attempt}/{Max} for {Kind}", attempt + 1, attempts, kind);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update {Kind} profile after {Attempts} attempts", kind, attempts);
                return false;
            }
        }
        return false;
    }
}
