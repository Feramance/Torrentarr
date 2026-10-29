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

## Request Integration

### Supported Request Systems

| System | Type | Torrentarr Support |
|--------|------|----------------|
| [Overseerr](overseerr.md) | Full request management | ✅ Native API polling |
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

---

**Note:** Overseerr and Ombi integration are planned features. Check the latest release notes for availability.
