# Search Configuration

Configure automated search and request integration for Torrentarr.

## Overview

Torrentarr can automatically trigger searches in your Arr instances for:

- Missing media
- Quality upgrades
- User requests from Overseerr/Ombi
- Failed downloads (re-search after blacklisting)

## Automated Search

### Enable Missing Media Search

Configure Torrentarr to periodically search for missing content:

```toml
[Radarr-Movies]
Name = "Radarr-Main"
SearchMissing = true
SearchPeriodDays = 7  # Search every 7 days
```

This will automatically search for:

- Movies marked as "Wanted" in Radarr
- TV episodes marked as "Monitored" in Sonarr
- Albums marked as "Monitored" in Lidarr

### Search Intervals

Control how often Torrentarr searches:

```toml
[Radarr-Movies]
SearchPeriodDays = 1   # Daily (aggressive)
```

```toml
[Radarr-Movies]
SearchPeriodDays = 7   # Weekly (recommended)
```

```toml
[Radarr-Movies]
SearchPeriodDays = 30  # Monthly (conservative)
```

**Recommendations:**
- **Daily** - For new/popular content
- **Weekly** - Balanced approach
- **Monthly** - For established libraries

## Proactive search pacing

Under **Settings → Integrations**, edit an Arr instance and open **Proactive
search controls**. Set either limit independently.

```toml
[Radarr-Movies.EntrySearch]
RecentGrabCooldownMinutes = 30
ActiveDownloadCeiling = 4
```

- `RecentGrabCooldownMinutes` uses dated grab history from the same Arr
  instance and library item. The cooldown expires at the configured boundary
  and survives a Torrentarr restart while Arr retains the evidence. Sonarr
  uses episode commands with this control enabled so broad search commands
  cannot include deferred episodes.
- `ActiveDownloadCeiling` counts distinct client and torrent pairs in the
  integration's owned categories. Downloading, stalled downloads and metadata
  fetches count as active. Paused or queued torrents do not use a slot.

Use zero to disable either control. Missing evidence holds affected proactive
missing and upgrade searches. Torrentarr checks again before submission and
reports the reason under **Processes → Search activity**. Failed-download
replacement searches follow their usual path.

## Proactive library gates and quiet hours

Under **Settings → Integrations**, edit the Arr instance. Use **Proactive search
controls** for tags, Radarr availability, and no-grab rest. Set the daily interval
and timezone under **Quiet hours**. These controls apply to missing and upgrade
searches.
Replacement searches retain their existing policy. Every enabled gate must pass;
Processes → Search activity reports the deferral reason using the same gate checks
as execution. A preview does not issue commands or reserve work.

| Setting | Default | Behavior |
| --- | --- | --- |
| `IncludeTags` | `[]` | Require at least one owning-library tag label, case-insensitive. Empty disables the include gate. |
| `ExcludeTags` | `[]` | Defer any matching label; excludes win over includes. |
| `RequireRadarrAvailability` | `false` | Radarr only: require its fresh `isAvailable` flag. Missing evidence defers work. |
| `UnsuccessfulSearchRestMinutes` | `0` | Rest the same item and search scope after completed no-grab work; zero disables, maximum 10080 minutes. |
| `QuietHoursEnabled` | `false` | Defer proactive work within the daily local interval. |
| `QuietHoursStart` | `22:00` | Start-inclusive local time, exactly `HH:mm`. |
| `QuietHoursEnd` | `07:00` | End-exclusive local time; must differ from start. Midnight crossing is supported. |
| `QuietHoursTimeZone` | `Etc/UTC` | Explicit IANA timezone, such as `Europe/Malta`. |

Tags come from the owning movie, series, artist or author, including series tags
for a Sonarr episode. Missing parents, malformed tags or unavailable Arr reads
defer affected searches. Tag labels and their ID mappings are read fresh from Arr;
Torrentarr does not create or change tags. Tag and availability reads have a
ten-second budget and are rechecked before submission. Radarr's availability can
differ from release-date eligibility; the opt-in gate follows the app's flag.

Quiet hours evaluate each real instant in the selected zone. Repeated local times
follow the same interval rule, and skipped local times need no synthetic run.
Processes shows the next real instant outside quiet hours. Work resumes on the
normal search cycle without replaying missed searches. Disable the switch to
restore the original scheduling policy; saved boundaries and timezone are retained.

Upstream resource contracts: [Radarr movie](https://github.com/Radarr/Radarr/blob/develop/src/Radarr.Api.V3/Movies/MovieResource.cs),
[Sonarr series](https://github.com/Sonarr/Sonarr/blob/develop/src/Sonarr.Api.V3/Series/SeriesResource.cs),
[Lidarr artist](https://github.com/Lidarr/Lidarr/blob/develop/src/Lidarr.Api.V1/Artist/ArtistResource.cs),
and [Readarr author](https://github.com/Readarr/Readarr/blob/develop/src/Readarr.Api.V1/Author/AuthorResource.cs).

An unsuccessful-search rest requires a successfully completed proactive command
and a fresh, usable Arr grab-history read spanning its submission through a
thirty-minute confirmation window after completion. Rejected, timed-out, pending
or otherwise uncertain commands start no rest. A matching grab follows the separate
recent-grab cooldown instead. Collection more than fifteen minutes after the
confirmation window is treated as unknown and starts no rest.

Rest expiry is fixed at command completion plus the confirmation window and the
configured duration. Persisted maintenance-state receipts retain it across restarts,
repeated observations and Arr section renames. Repeats cannot extend the expiry.
The same instance, Arr server, item and search scope share one rest receipt; other
items remain eligible. Expired receipts are pruned hourly. Disabling rest bypasses
these gates while existing receipts expire, and never starts replacement work.
Search activity distinguishes command completion, observed grabs and confirmed
no-grab windows. Sonarr uses episode commands while item gates are enabled so a
broader command cannot bypass a held episode.

## Request Integration

### Supported Request Systems

| System | Type | Torrentarr Support |
|--------|------|----------------|
| [Seerr](overseerr.md) | Full request management | ✅ Native API polling |
| [Ombi](ombi.md) | Full request management | ✅ Native API polling |
| [Requestrr](requestrr.md) | Discord chatbot | ✅ Via Overseerr or Ombi |

!!! tip "Requestrr Users"
    Requestrr is a Discord chatbot. Connect it to **Overseerr or Ombi**, then Torrentarr
    will process requests through its existing integration.

### Request Priority

Prioritize searches for user requests:

```toml
[Radarr-Movies]
PrioritizeRequests = true  # Search requests first
RequestSearchDelay = 60    # Wait 1 minute after request
```

## Quality Upgrades

### Enable Upgrade Searches

Search for better quality releases:

```toml
[Radarr-Movies]
SearchUpgrades = true
UpgradePeriodDays = 14  # Search for upgrades every 2 weeks
```

### Upgrade Criteria

Configure what qualifies as an upgrade:

```toml
[Radarr-Movies]
# Only upgrade if:
MinimumQualityGain = "1080p"  # At least 1080p
CustomFormatScore = 100        # CF score improvement
```

## Failed Download Re-search

### Automatic Re-search

When a download fails, automatically search again:

```toml
[Radarr-Movies]
AutoResearchFailed = true
ResearchDelay = 300        # Wait 5 minutes before re-searching
MaxResearchAttempts = 3    # Try up to 3 times
```

### Blacklist Behavior

Configure blacklisting of failed releases:

```toml
[Radarr-Movies]
BlacklistFailed = true      # Blacklist failed torrents
BlacklistMinSize = 104857600  # Only blacklist if > 100MB
```

## Search Limits

### Rate Limiting

Prevent overwhelming your indexers:

```toml
[Settings]
MaxSearchesPerHour = 60    # Limit to 60 searches/hour
SearchDelaySeconds = 10    # Wait 10s between searches
```

### Concurrent Searches

Limit parallel searches:

```toml
[Settings]
MaxConcurrentSearches = 5  # Max 5 simultaneous searches
```

## Search Modes

### Radarr Search Modes

- **Standard** - Normal movie search
- **Interactive** - Allow manual selection
- **RSS** - Grab from RSS feeds

```toml
[Radarr-Movies]
SearchMode = "Standard"  # or "Interactive" or "RSS"
```

### Sonarr Search Modes

The WebUI **Search Mode** dropdown offers four choices:

| Selection | `SearchBySeries` value | Behavior |
| --- | --- | --- |
| Smart (default) | `"smart"` | Use series, then season searches when more than half of in-scope aired episodes need searching, with at least two eligible episodes; otherwise search episodes. |
| Series | `"series"` | Search each affected series once, splitting into permitted seasons when needed to exclude specials. |
| Season | `"season"` | Search each affected season once, without requiring a majority. |
| Episode | `"episode"` | Search individual episodes. |

```toml
[Sonarr-TV.EntrySearch]
SearchBySeries = "smart"  # "series", "season", or "episode"
AlsoSearchSpecials = false
```

Legacy `true` / `"true"` values select Series, and `false` / `"false"` select Episode. Saving uses the named mode. Smart counts configured missing, quality, custom-format, and upgrade needs across all years; the current search pass must contain a candidate to trigger a search.

Specials require `AlsoSearchSpecials = true` **and** at least one monitored special in that series. Once permitted, `Unmonitored = true` may include its other unmonitored specials. This gate applies to every mode and blocklist retries, including specials that a series command would search implicitly. Unmonitored episodes use explicit episode searches.

See [Sonarr search configuration](../arr/sonarr.md#search-mode) for details. Broad searches may also upgrade other permitted episodes according to Sonarr's profiles.

## Advanced Search Options

### Custom Format Requirements

Only search if custom format requirements can be met:

```toml
[Radarr-Movies]
RequireCustomFormat = true
MinimumCustomFormatScore = 100
```

### Release Restrictions

Filter searches by release properties:

```toml
[Radarr-Movies]
PreferredWords = ["PROPER", "REPACK"]
ForbiddenWords = ["CAM", "TS"]
MinSeeders = 5  # Require at least 5 seeders
```

## Search Triggers

Torrentarr triggers searches in these scenarios:

1. **Scheduled** - Based on SearchPeriodDays
2. **On Request** - When new Overseerr/Ombi request
3. **On Failure** - After torrent fails/is blacklisted
4. **Manual** - Via WebUI or API
5. **On Import** - If configured to search for upgrades

## Monitoring Searches

### Search Logs

Monitor search activity in logs:

```bash
# Check search activity
grep "search" ~/config/logs/Radarr-Main.log
```

### Search History

Torrentarr tracks search history in the database:

```sql
SELECT * FROM searches ORDER BY created_at DESC LIMIT 10;
```

## Troubleshooting

### Too Many Searches

**Problem:** Torrentarr is searching too frequently

**Solutions:**
```toml
# Increase search interval
SearchPeriodDays = 14

# Reduce concurrent searches
MaxConcurrentSearches = 2

# Add delay between searches
SearchDelaySeconds = 30
```

### No Search Results

**Problem:** Searches return no results

**Solutions:**
- Check indexer configuration in Arr instance
- Verify indexers are accessible
- Review search criteria (too restrictive?)
- Check Arr logs for errors

### Request Integration Not Working

**Problem:** Overseerr/Ombi requests not triggering searches

**Solutions:**
- Verify API key is correct
- Check Overseerr/Ombi is accessible
- Ensure CheckInterval is reasonable
- Review logs for connection errors

## Best Practices

### Search Frequency

- **New Libraries** - Search more frequently (daily)
- **Established Libraries** - Search less often (weekly/monthly)
- **Request-Driven** - Enable Overseerr/Ombi integration

### Resource Management

- Limit concurrent searches to avoid overload
- Add delays between searches
- Use reasonable check intervals
- Monitor indexer hit limits

### Quality Over Quantity

- Set minimum quality requirements
- Use custom format scoring
- Blacklist problematic releases
- Prioritize quality upgrades

## Configuration Examples

### Aggressive Search (New Library)

```toml
[Radarr-Movies]
Name = "Radarr-New"
SearchMissing = true
SearchPeriodDays = 1        # Daily
SearchUpgrades = true
UpgradePeriodDays = 3       # Upgrade search every 3 days
PrioritizeRequests = true
```

### Conservative Search (Established Library)

```toml
[Radarr-Movies]
Name = "Radarr-Established"
SearchMissing = true
SearchPeriodDays = 30       # Monthly
SearchUpgrades = false      # No automatic upgrades
MaxSearchesPerHour = 10     # Limited searches
```

### Request-Focused Configuration

```toml
[Settings.Overseerr]
Enabled = true
URI = "http://overseerr:5055"
APIKey = "overseerr-api-key"
CheckInterval = 180  # Check every 3 minutes

[Radarr-Movies]
PrioritizeRequests = true
RequestSearchDelay = 30     # Quick response
SearchMissing = false       # Only search requests
```

## Related Documentation

- [Radarr Configuration](../arr/radarr.md)
- [Sonarr Configuration](../arr/sonarr.md)
- [Lidarr Configuration](../arr/lidarr.md)
- [Configuration Reference](../config-file.md)

## Future Features

Planned enhancements for search configuration:

- Webhook support for custom triggers
- Advanced scheduling (cron-like)
- Search quota management
- A/B testing for search strategies
- Machine learning-based search optimization
