# Torrent Configuration

Torrentarr provides granular control over how torrents are processed, filtered, and managed for each Arr instance. This page covers all torrent-specific settings available in the `[<Arr>-<Name>.Torrent]` section.

---

## Overview

Torrent settings control:

- **File filtering** - Which files to download/ignore based on extensions, names, folders
- **Processing timing** - When to start processing torrents
- **Completion thresholds** - Percentage and ETA requirements
- **Stalled torrent handling** - Detection and removal of stuck downloads
- **Seeding rules** - Per-instance seeding configurations
- **Tracker-specific overrides** - Custom settings per tracker

**Configuration section:**

```toml
[Radarr-Movies.Torrent]
# All torrent settings for this Arr instance
```

---

## File Filtering

### Shared file filter lists

Define reviewed lists under general Settings and reference their case-sensitive
names from each Arr's Torrent settings. The WebUI collection editor and per-Arr
list-name editor save the same fields.

```toml
[Settings]
SharedFileFilters = [
    { Name = "reviewed", Source = "filters/reviewed.txt" },
    { Name = "public", Source = "https://example.org/torrent-exclusions.txt" },
]

[Radarr-Movies.Torrent]
SharedFileFilters = ["reviewed", "public"]

[Sonarr-TV.Torrent]
SharedFileFilters = ["reviewed"]
```

Use UTF-8 text containing one regular expression per line. Blank lines and lines
starting with `#` are ignored. Expressions match the complete torrent-relative
path with forward slashes, using each Arr's `CaseSensitiveMatches` setting.
For example, `\.exe$` excludes executable files. The format supports .NET's
nonbacktracking regex subset. Lookarounds and backreferences fail validation.
Each source has a 32 KiB limit, at most 128 patterns, and at most 256 characters
per pattern. Matching has a 25 ms per-pattern timeout and a 4096-character path
limit. A source must validate in full before activation.

At most 32 named lists can be configured. Local paths are relative to the
Torrentarr data directory and must identify ordinary files. Parent traversal,
absolute paths, links, and devices are rejected. The data directory must be
operator-controlled. On Linux, source files are opened through directory handles
with no-follow checks. HTTPS sources require port 443 and public addresses,
with no credentials, query, fragment, proxy, or redirects. DNS answers are checked
and the connection uses a vetted address. Fetches have a ten-second timeout.

Each process refreshes lists outside torrent processing every five minutes.
A refresh failure raises an Issue and retains that process's last valid version.
A process starting without a usable version holds affected torrent processing and
imports until loading succeeds. Changing the configured source also holds work
until that source loads. Removing a reference disables its exclusions. Current
local exclusions and extension rules continue to apply.

Shared matches set excluded file priorities to zero. Torrentarr records previous
priorities in its maintenance-state extension before changing them, so it can
restore its own shared-only priority changes when an exclusion stops matching.
The record includes the torrent's added timestamp, file index, and filename.
Restoration does not claim ownership of files already disabled by the operator.
Shared-only matches retain the payload and torrent entry. Eligible media can
still import alongside excluded extras.

Expand **Preview saved file filters** in the Arr editor to check an example path.
The preview uses saved settings and the processing matcher, showing each list's
source, last successful load, latest check, and refresh error. An unavailable
list shows a processing hold.

### CaseSensitiveMatches

**Type:** Boolean
**Default:** `false`

Enable case-sensitive regex matching for file/folder exclusions.

```toml
CaseSensitiveMatches = false
```

**Examples:**

| Value | Pattern | Matches |
|-------|---------|---------|
| `false` | `\bsample\b` | `Sample`, `SAMPLE`, `sample` |
| `true` | `\bsample\b` | `sample` only |

---

### FolderExclusionRegex

**Type:** List of regex patterns
**Default:** Common extras/sample folders

Exclude internal torrent subfolders matching these regex patterns from processing. The torrent's top-level title folder is preserved, so a title containing “Special” does not match a rule intended for a `Specials` subfolder.

**Default patterns:**

```toml
FolderExclusionRegex = [
    "\\bextras?\\b",        # Extras, Extra
    "\\bfeaturettes?\\b",   # Featurettes
    "\\bsamples?\\b",       # Samples, Sample
    "\\bscreens?\\b",       # Screens, Screenshots
    "\\bnc(ed|op)?(\\d+)?\\b"  # NCOP, NCED (anime)
]
```

**Anime-specific additions:**

```toml
FolderExclusionRegex = [
    "\\bextras?\\b",
    "\\bsamples?\\b",
    "\\bspecials?\\b",     # Specials (anime)
    "\\bova\\b",           # OVA folders
    "\\bnc(ed|op)?(\\d+)?\\b"
]
```

**Custom examples:**

```toml
# Exclude bonus content
FolderExclusionRegex = [
    "\\bbehind.the.scenes\\b",
    "\\bdeleted.scenes\\b",
    "\\binterviews?\\b"
]
```

!!! warning "Regex Escaping"
    Backslashes must be doubled in TOML strings: `\\b` not `\b`

---

### FileNameExclusionRegex

**Type:** List of regex patterns
**Default:** Common junk file patterns

Exclude individual files matching these regex patterns.

**Default patterns:**

```toml
FileNameExclusionRegex = [
    "\\bncop\\d+?\\b",          # Anime NCOP files
    "\\bnced\\d+?\\b",          # Anime NCED files
    "\\bsample\\b",             # Sample videos
    "brarbg.com\\b",            # RARBG ads
    "\\btrailer\\b",            # Trailers
    "music video",              # Music videos
    "comandotorrents.com"       # Torrent site ads
]
```

**Custom examples:**

```toml
# Block specific release groups
FileNameExclusionRegex = [
    "\\bYIFY\\b",
    "\\bRAR?BG\\b",
    "\\b1337x\\b"
]

```

```toml
# Block unwanted audio tracks
FileNameExclusionRegex = [
    "\\.commentary\\.",
    "\\.ac3\\.audio\\."
]
```

---

### FileExtensionAllowlist

**Type:** List of file extensions
**Default:** Common video/subtitle extensions

Only allow files with these extensions. Regex extension patterns such as `'\.r[0-9]{2}'` are also supported and match the complete extension. Empty list = allow all. Filtering and media validation use the same matcher.

**Default:**

```toml
FileExtensionAllowlist = [
    ".mp4", ".mkv",      # Video files
    ".sub", ".ass", ".srt",  # Subtitles
    ".!qB", ".parts"     # Incomplete download markers
]
```

**Custom examples:**

```toml
# Only MKV and MP4 (no subtitles)
FileExtensionAllowlist = [".mkv", ".mp4"]

# Include audio for music
```

```toml
FileExtensionAllowlist = [".flac", ".mp3", ".m4a", ".alac"]

# Allow all files (empty list)
```

```toml
FileExtensionAllowlist = []
```

!!! tip "Performance"
    Using an allowlist reduces bandwidth by not downloading unwanted files (NFO, images, etc.)

---

### AutoDelete

**Type:** Boolean
**Default:** `false`

Automatically delete files that don't match the allowlist or match exclusion patterns.

```toml
AutoDelete = false
```

**When enabled:**

- Non-playable files (`.exe`, `.txt`, `.nfo`) are deleted immediately
- Files matching `FileNameExclusionRegex` are deleted
- Files not in `FileExtensionAllowlist` are deleted

**Recommendation:** `false` to review files manually before deletion

---

## Processing Timing

### IgnoreTorrentsYoungerThan

**Type:** Integer (seconds)
**Default:** `180` (3 minutes)

Wait this many seconds after torrent is added before processing.

```toml
IgnoreTorrentsYoungerThan = 300  # 5 minutes
```

**Why delay processing?**

- **Metadata downloads:** Torrents need time to fetch file lists
- **Fast starts:** Avoid processing before files begin downloading
- **Duplicate prevention:** Give Arr time to process the download

**Examples:**

```toml
# Fast processing (risky)
IgnoreTorrentsYoungerThan = 60  # 1 minute

# Standard (recommended)
```

```toml
IgnoreTorrentsYoungerThan = 180  # 3 minutes

# Conservative (slow tracker/large torrents)
```

```toml
IgnoreTorrentsYoungerThan = 600  # 10 minutes
```

!!! warning "Too Low Values"
    Values below 60 seconds can cause duplicate imports or missing files.

---

### MaximumETA

**Type:** Integer (seconds) or `-1` (disabled)
**Default:** `-1` (disabled)

Maximum allowed estimated time to completion. Torrents exceeding this are removed.

```toml
MaximumETA = 3600  # 1 hour
```

**How it works:**

- Torrentarr calculates remaining time based on download speed
- If ETA > MaximumETA, torrent is **marked for removal**
- **Removal happens AFTER download completes** (`amount_left == 0`)
- Prevents seeding slow torrents, but allows them to finish downloading

!!! warning "Removal Timing"

    **MaximumETA is enforced AFTER completion, not during download.**

    1. During download: Torrentarr calculates ETA
    2. If ETA > MaximumETA: Torrent marked for removal
    3. **Removal happens**: After `amount_left == 0` (download complete)

    **Why:** Deleting incomplete downloads wastes bandwidth. Torrentarr waits for completion, then removes if it took too long.

    **Example:**
    ```
    MaximumETA = 3600  # 1 hour

    Torrent downloads in 2 hours (exceeds limit)
    → Torrentarr allows it to finish downloading
    → After completion, removes torrent (won't seed)
    ```

**Examples:**

```toml
# Remove torrents taking over 2 hours
MaximumETA = 7200

# Remove torrents taking over 30 minutes
```

```toml
MaximumETA = 1800

# Disabled (no ETA limit)
```

```toml
MaximumETA = -1
```

!!! info "Tracker Override"
    If `MaximumETA` is set on a tracker basis (`[<Arr>.<Torrent>.Trackers.<TrackerName>]`), that value overrides this global setting.

---

### MaximumDeletablePercentage

**Type:** Float (0.0-1.0)
**Default:** `0.99` (99%)

Don't delete torrents that have downloaded more than this percentage.

```toml
MaximumDeletablePercentage = 0.95  # 95%
```

**Why use this?**

Prevents accidentally deleting nearly-complete torrents due to temporary stalls or tracker issues.

**Examples:**

```toml
# Very conservative (only delete if < 50% complete)
MaximumDeletablePercentage = 0.50

# Aggressive (delete even at 99%)
```

```toml
MaximumDeletablePercentage = 0.99

# Never auto-delete based on completion
```

```toml
MaximumDeletablePercentage = 0.01  # Only delete if < 1% complete
```

---

## Stalled Torrent Handling

### DoNotRemoveSlow

**Type:** Boolean
**Default:** `true`

Don't remove slow torrents automatically.

```toml
DoNotRemoveSlow = true
```

**When enabled:**

- Slow torrents are **not** automatically deleted
- Manual intervention required for stuck downloads

**When disabled:**

- Slow/stalled torrents are removed based on `StalledDelay`

**Recommendation:** `true` to avoid removing torrents on slow but working trackers

---

### StalledDelay

**Type:** Integer (minutes), `-1` (infinite grace), or `0` (immediate cleanup)
**Default:** `15` minutes

Maximum time a torrent can remain stalled before removal.

```toml
StalledDelay = 30  # 30 minutes
```

**Stalled conditions:**

- No download/upload progress
- No seeds available
- Tracker offline

**Values:**

- `-1` = Infinite grace (never remove through stalled handling)
- `0` = Immediate cleanup (no recovery grace period)
- `> 0` = Minutes until removal

**Examples:**

```toml
# Quick removal (5 minutes)
StalledDelay = 5

# Patient (1 hour)
```

```toml
StalledDelay = 60

# Never remove
```

```toml
StalledDelay = -1
```

---

### qBittorrent stall safeguards

These opt-in settings are available in the Arr integration's torrent settings.
Each defaults to `0` or `false`.

| Setting | Behavior |
| --- | --- |
| `MetadataStallMinutes` | Adds an elapsed observation threshold while qBittorrent reports a metadata download state. Ordinary `StalledDelay`, minimum age, and removal protections still apply. |
| `ResetStallOnDownloadProgress` | With a positive `StalledDelay`, requires a continuous payload stall. Torrentarr resets the window when downloaded bytes increase together with a decrease in remaining bytes. Rechecks and missing observations reset continuity. |
| `RespectAlternateSpeedLimits` | Holds slow ETA cleanup when qBittorrent reports its alternate limits are active or the current limit mode is unavailable. Other torrent workflows continue under their own rules. |

Stall windows restart on worker restart, configuration change, or a gap between
samples exceeding twice the client's configured scan interval. Debug logs show elapsed time,
threshold and resets. [qBittorrent WebAPI transfer methods](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-5.0%29#get-alternative-speed-limits-state)

### ReSearchStalled

**Type:** Boolean
**Default:** `false`

Trigger a new search **before** removing a stalled torrent.

```toml
ReSearchStalled = true
```

**When enabled:**

1. Torrent detected as stalled
2. Torrentarr triggers Arr to search for alternatives
3. Original stalled torrent is removed

**When disabled:**

1. Torrent detected as stalled
2. Torrent is removed
3. Arr's natural retry logic handles re-search

**Recommendation:** `false` to avoid aggressive re-searching; let Arr handle retries

---

## Seeding Configuration

Seeding settings are configured in the `[<Arr>.Torrent.SeedingMode]` subsection. See **[Seeding Configuration](seeding.md)** for complete details.

**Quick reference:**

```toml
[Radarr-Movies.Torrent.SeedingMode]
DownloadRateLimitPerTorrent = -1        # KB/s download limit (-1 = unlimited)
UploadRateLimitPerTorrent = -1          # KB/s upload limit (-1 = unlimited)
MaxUploadRatio = 2.0                    # Remove after 2:1 ratio
MaxSeedingTime = 43200                  # Remove after 12 hours (seconds)
RemoveTorrent = -1                      # Additional removal delay (seconds)
RemoveDeadTrackers = false              # Report dead trackers; never delete torrents
RemoveTrackerWithMessage = []           # Remove matching tracker URLs; private configured/last URLs preserved
```

While protection is unmet or a private tracker is unmatched, Torrentarr records and
temporarily clears conflicting per-torrent native stop limits so qBittorrent cannot
remove the torrent first. Restored only when Torrentarr still owns the unlimited
values and the torrent identity matches.

**See also:** [Seeding Configuration Guide](seeding.md)

---

## Tracker-Specific Configuration

Override global torrent settings on a per-tracker basis.

### Tracker Configuration Structure

```toml
[Radarr-Movies]

[[Trackers]]
ArrInstances = ["Radarr-Movies"]
MaximumETA = 1800                       # 30 minutes (overrides global)
DownloadRateLimit = -1
UploadRateLimit = -1
MaxUploadRatio = 3.0                    # Seed to 3:1
MaxSeedingTime = 86400                  # 24 hours
RemoveTorrent = -1
URI = "https://passthepopcorn.me"
```

### Tracker Settings

#### URI

The tracker announce host or URL. A rule matches that host and its subdomains.
Create one rule for each alternate host.

#### MaximumETA (per-tracker)

**Type:** Integer (seconds) or `-1`
**Default:** Inherits global setting

Override global `MaximumETA` for this tracker.

```toml
MaximumETA = 3600  # 1 hour for this tracker only
```

---

#### Download/Upload Rate Limits (per-tracker)

**Type:** Integer (KB/s) or `-1` (unlimited)
**Default:** Inherits global setting

```toml
DownloadRateLimit = 5000  # 5 MB/s
UploadRateLimit = 2000    # 2 MB/s
```

---

#### Seeding Limits (per-tracker)

**Type:** Float (ratio) or Integer (seconds)
**Default:** Inherits global setting

```toml
MaxUploadRatio = 3.0      # Seed to 3:1
MaxSeedingTime = 172800   # 48 hours
```

---

## Complete Configuration Examples

### Example 1: Movies (Radarr)

```toml
[Radarr-Movies.Torrent]
CaseSensitiveMatches = false

# Exclude extras and samples
FolderExclusionRegex = [
    "\\bextras?\\b",
    "\\bfeaturettes?\\b",
    "\\bsamples?\\b",
    "\\bbehind.the.scenes\\b"
]

# Exclude junk files
FileNameExclusionRegex = [
    "\\bsample\\b",
    "\\btrailer\\b",
    "brarbg.com\\b"
]

# Only video/subtitle files
FileExtensionAllowlist = [".mkv", ".mp4", ".srt", ".ass"]

AutoDelete = false
IgnoreTorrentsYoungerThan = 180  # 3 minutes
MaximumETA = 7200  # 2 hours
MaximumDeletablePercentage = 0.95  # 95%

DoNotRemoveSlow = true
StalledDelay = 30  # 30 minutes
ReSearchStalled = false

[Radarr-Movies.Torrent.SeedingMode]
MaxUploadRatio = 2.0
MaxSeedingTime = 43200  # 12 hours
```

---

### Example 2: TV (Sonarr)

```toml
[Sonarr-TV.Torrent]
CaseSensitiveMatches = false

FolderExclusionRegex = [
    "\\bextras?\\b",
    "\\bsamples?\\b"
]

FileNameExclusionRegex = [
    "\\bsample\\b",
    "\\btrailer\\b"
]

FileExtensionAllowlist = [".mkv", ".mp4", ".srt"]

AutoDelete = false
IgnoreTorrentsYoungerThan = 120  # 2 minutes (faster for new episodes)
MaximumETA = 3600  # 1 hour
MaximumDeletablePercentage = 0.99

DoNotRemoveSlow = false  # Remove slow downloads for TV
StalledDelay = 15  # 15 minutes
ReSearchStalled = true  # Re-search quickly for new episodes

[Sonarr-TV.Torrent.SeedingMode]
MaxUploadRatio = 1.5
MaxSeedingTime = 21600  # 6 hours
```

---

### Example 3: Anime (Sonarr)

```toml
[Sonarr-Anime.Torrent]
CaseSensitiveMatches = false

# Anime-specific exclusions
FolderExclusionRegex = [
    "\\bextras?\\b",
    "\\bsamples?\\b",
    "\\bspecials?\\b",
    "\\bova\\b",
    "\\bnc(ed|op)?(\\d+)?\\b"
]

FileNameExclusionRegex = [
    "\\bncop\\d+?\\b",
    "\\bnced\\d+?\\b",
    "\\bsample\\b"
]

FileExtensionAllowlist = [".mkv", ".mp4", ".ass", ".srt"]

AutoDelete = false
IgnoreTorrentsYoungerThan = 300  # 5 minutes (larger files)
MaximumETA = -1  # Disabled (anime can be slow)
MaximumDeletablePercentage = 0.99

DoNotRemoveSlow = true  # Keep slow anime torrents
StalledDelay = 60  # 1 hour
ReSearchStalled = false

[Sonarr-Anime.Torrent.SeedingMode]
MaxUploadRatio = 2.0
MaxSeedingTime = 86400  # 24 hours
```

---

### Example 4: Music (Lidarr)

```toml
[Lidarr-Music.Torrent]
CaseSensitiveMatches = false

FolderExclusionRegex = [
    "\\bscans?\\b",
    "\\bartwork\\b"
]

FileNameExclusionRegex = [
    "\\bcover\\b",
    "\\.log$",
    "\\.cue$"
]

# Allow audio + cue/log for verification
FileExtensionAllowlist = [
    ".flac", ".mp3", ".m4a",
    ".cue", ".log"
]

AutoDelete = false
IgnoreTorrentsYoungerThan = 180
MaximumETA = -1  # No limit for music
MaximumDeletablePercentage = 0.95

DoNotRemoveSlow = true
StalledDelay = -1  # Never remove (music trackers can be slow)
ReSearchStalled = false

[Lidarr-Music.Torrent.SeedingMode]
MaxUploadRatio = 3.0  # Higher ratio for music
MaxSeedingTime = 172800  # 48 hours
```

---

### Example 5: Private Tracker with Custom Rules

```toml
[Radarr-Movies.Torrent]
IgnoreTorrentsYoungerThan = 180
MaximumETA = -1  # Disabled globally
MaximumDeletablePercentage = 0.99
DoNotRemoveSlow = true
StalledDelay = -1

[Radarr-Movies.Torrent.SeedingMode]
MaxUploadRatio = -1  # No global ratio limit
MaxSeedingTime = -1  # No global time limit

# Tracker-specific overrides
[[Trackers]]
ArrInstances = ["Radarr-Movies"]
URI = "https://passthepopcorn.me"
MaximumETA = 1800  # 30 minutes (fast tracker)
MaxUploadRatio = 3.0
MaxSeedingTime = 86400  # 24 hours
DownloadRateLimit = -1
UploadRateLimit = -1

[[Trackers]]
ArrInstances = ["Radarr-Movies"]
URI = "https://broadcasthe.net"
MaximumETA = 3600  # 1 hour
MaxUploadRatio = 2.0
MaxSeedingTime = 172800  # 48 hours (more patient)

[[Trackers]]
ArrInstances = ["Radarr-Movies"]
URI = "https://landof.tv"
MaximumETA = 3600
MaxUploadRatio = 2.0
MaxSeedingTime = 172800
```

---

## Troubleshooting

### Files Not Downloading

**Symptom:** Torrent added but no files download

**Check:**

1. **FileExtensionAllowlist:**
   ```toml
   # If too restrictive, no files match
   FileExtensionAllowlist = [".mkv"]  # Won't download .mp4 files
```

2. **FolderExclusionRegex:**
   ```toml
   # Too broad, excludes everything
   FolderExclusionRegex = [".*"]  # BAD: Matches all folders
```

**Solution:** Widen allowlist or reduce exclusions

---

### Torrents Deleted Too Quickly

**Symptom:** Torrents removed before completing

**Check:**

1. **MaximumETA:**
   ```toml
   MaximumETA = 600  # 10 minutes (too aggressive)
```

2. **StalledDelay:**
   ```toml
   StalledDelay = 5  # 5 minutes (too quick)
```

**Solution:** Increase timeouts or disable with `-1`

---

### Stalled Torrents Not Removed

**Symptom:** Stuck torrents never delete

**Check:**

1. **Stalled cleanup disabled (infinite grace):**
   ```toml
   StalledDelay = -1  # Infinite stalled grace; other cleanup rules may still remove torrents
```

2. **DoNotRemoveSlow enabled:**
   ```toml
   DoNotRemoveSlow = true  # Keeps slow torrents
```

**Solution:** Enable stalled detection:
```toml
DoNotRemoveSlow = false
StalledDelay = 30  # 30 minutes
```

---

### Regex Not Matching

**Symptom:** Exclusion patterns don't work

**Check escaping:**

```toml
# Incorrect
FolderExclusionRegex = ["\bsample\b"]  # Single backslash

# Correct
```

```toml
FolderExclusionRegex = ["\\bsample\\b"]  # Double backslash
```

**Test regex:**

Use online regex testers with test strings:

- Test string: `Sample.mkv`
- Pattern: `\\bsample\\b` (case-insensitive)
- Should match: ✅

---

## Best Practices

### 1. Start Conservative

```toml
# Safe defaults
AutoDelete = false  # Manual review first
MaximumETA = -1  # No automatic removal
StalledDelay = -1  # Manual intervention
```

Test for 1-2 weeks, then enable automated removal.

---

### 2. Use Allowlists for Bandwidth Savings

```toml
# Only download video files
FileExtensionAllowlist = [".mkv", ".mp4"]
```

Saves bandwidth by not downloading NFO, artwork, etc.

---

### 3. Adjust IgnoreTorrentsYoungerThan by Content Type

```toml
# TV (fast processing for new episodes)
IgnoreTorrentsYoungerThan = 120  # 2 minutes

# Movies (larger files, more patient)
```

```toml
IgnoreTorrentsYoungerThan = 300  # 5 minutes

# Music/Anime (very patient)
```

```toml
IgnoreTorrentsYoungerThan = 600  # 10 minutes
```

---

### 4. Use Tracker-Specific Overrides

```toml
# Slow public tracker
[Radarr-Movies]

[[Trackers]]
ArrInstances = ["Radarr-Movies"]
URI = "https://yts.mx"
MaximumETA = -1  # Disable ETA limit
MaxSeedingTime = 3600  # Seed for 1 hour only

# Fast private tracker
[[Trackers]]
ArrInstances = ["Radarr-Movies"]
URI = "https://privatehd.to"
MaximumETA = 1800  # 30 minutes
MaxSeedingTime = 86400  # Seed for 24 hours
```

---

### 5. Monitor Exclusions Impact

Check logs for excluded files:

```
[DEBUG] Excluded folder: /data/torrents/movie-2023/Extras/
[DEBUG] Excluded file: sample.mkv (matches: \\bsample\\b)
```

Adjust patterns if too many valid files are excluded.

---

## Related Documentation

- **[Seeding Configuration](seeding.md)** - Complete seeding and ratio management
- **[qBittorrent Configuration](qbittorrent.md)** - qBittorrent connection setup
- **[Health Monitoring](../features/health-monitoring.md)** - Torrent health checks

---

## Summary

- **File filtering** - Control which files are downloaded with allowlists and exclusions
- **Processing timing** - Delay processing with `IgnoreTorrentsYoungerThan`
- **Completion thresholds** - Set `MaximumETA` and `MaximumDeletablePercentage`
- **Stalled handling** - Configure `StalledDelay` and `DoNotRemoveSlow`
- **Tracker overrides** - Custom settings per tracker for optimal performance
- **Start conservative** and enable automation after testing

## Import rejection and deferred removal

An import allowlist warning means that the configured rules excluded content needed for import. Executables remain excluded. Rejected `.rar` and split `.r00`/`.r01` archive files are identified in the diagnostic; Torrentarr does not extract archives through this workflow. Use an appropriate unpacking workflow outside Torrentarr or select a release containing allowed media files. Adding archive extensions to the allowlist does not extract them.

Import rejection and cleanup are separate outcomes. H&R can preserve a rejected release until its configured obligations are met. A healthy protection wait is informational; missing mappings, unknown tracker evidence, and tracker deadline or native-stop risks remain actionable warnings. Legacy diagnostics use client-reported seed time, including the configured tracker update buffer; remaining time describes only the time requirement and does not clear unmet ratio obligations.

Unchanged allowlist and removal diagnostics move to Debug after the first message. Changed blockers or configuration produce a fresh message; retries continue normally and operational issues remain current. Suppression is in memory and resets after a restart or cache eviction.

Configured file inclusion/exclusion rules determine import eligibility. If allowed content remains and ordinary readiness checks pass, excluded files remaining on disk do not block import. `AutoDelete` cleanup is independent and retries safely; H&R can prevent that cleanup or torrent removal without suppressing the import. This applies to all excluded file types, with no screenshot or metadata exception list. All-files-excluded releases remain rejected, and unsafe paths, missing storage, failed filtering requests, and Arr failures remain actionable.


## Rescue supported blocked imports

`RescueBlockedImports` defaults to `false` and is available for Radarr instances.
Enable it in the Arr torrent settings or the instance's Torrent section.

```toml
[Radarr-Movies.Torrent]
RescueBlockedImports = true
```

The first supported incident is Radarr's blocked import for a release matched by
ID through grab history. Torrentarr asks Radarr's manual-import preview for its
own match and accepts exactly one eligible file for the same movie and download.
The preview must contain no rejection for that file. Ambiguous matches and other
rejection reasons keep their existing failure policy. Sonarr, Lidarr and Readarr
rescue policies remain disabled until their rejection and import contracts have
fixtures. Radarr's upstream implementation defines this warning and its manual
import workflow. [Radarr completed-download handling](https://github.com/Radarr/Radarr/blob/develop/src/NzbDrone.Core/Download/CompletedDownloadService.cs)

Rescue requires a completed torrent past the normal sixty-second grace period,
fresh queue and client observations, unique client ownership, a stable download
identity, and explicit client-to-local path mappings. The Arr output path must
match the client content path or its explicitly mapped local path. Each selected file must be
owned by the torrent, accessible through its mapping, and match its reported
size. Current local exclusions, extension rules and shared filter snapshots all
apply. Excluded extras stay outside the submitted import.

Torrentarr submits one `ManualImport` command in Copy mode, preserving the client
entry and source payload. It stores a claim before the request and sends the POST
once, with redirects disabled. A timeout or worker restart keeps that claim and
cannot repeat the command for the same incident. Command acceptance and queue
disappearance leave the result pending. Positive import history must match the
movie, download ID and submission time, and the queue must be clear, before the
rescue is confirmed. A reported source path must also match the selected file.

Competing automatic removal and failed outcomes wait while confirmation is
pending. After fifteen minutes, fresh Arr and client observations can classify
the result as unconfirmed and resume normal failure handling. An unavailable
observation keeps the hold and raises an Issue. Fresh queue or command evidence
of an active import extends the hold beyond that window. A confirmed rescue skips failed
custom-format handling, blocklisting and replacement. Existing configured
post-import cleanup still uses its normal protection checks.

Receipts use Torrentarr's existing maintenance-state extension, with one record
per client and torrent hash. They retain download identity, incident and outcome
information across restarts. A repeated warning cannot create another attempt
until a fresh, unique queue entry reports a healthy import-pending state for the
same movie and output path, or a new verified download identity supersedes it.
Queue disappearance preserves the old claim. Receipts are retained alongside torrent state.
Disabling rescue stops new attempts while existing pending claims are reconciled.
Catalog sync also reconciles receipts after Arr removes a successfully imported
download from the client. It checks up to 100 pending receipts within a thirty-second
budget, rotating a persisted cursor between batches. A missing torrent requires a
fresh client inventory, positive matching history and no active import command;
it never triggers a new submission. Database updates match the original download
epoch so a re-added torrent cannot inherit success. A SQLite partial index bounds
receipt selection to pending rescues for that Arr server.
A pending claim is scoped to the original Arr server and owner category. A
configuration change holds the claim until that scope is restored and its
outcome can be inspected.
Unreadable or incomplete receipts raise an Issue and preserve removal protection
until valid database evidence is restored.

### Failed torrent action

In Torrent Handling, each Arr integration can **Use existing policy** (the
default) or **Move to a category and keep files**. For the move policy, select an
existing category on every configured, enabled qBittorrent client. The editor
shows categories on their own clients and identifies destinations managed by Arr.
Torrentarr checks the destination again before applying the move. The source,
failed and recheck categories, and destinations owned by an Arr are unavailable.

Set `FailedTorrentAction = 'move'` and configure a separate
`FailedTorrentDestinations = [{ Client = 'qBit', Category = 'parked' }]` entry per
client in the Arr's Torrent section when using TOML. Torrentarr requires a
fresh, unambiguous torrent identity and manual torrent management with known
absolute save and content paths. It holds automatic or unknown torrent management
because a category change can relocate data.

The worker retains the original torrent and files. Once the new category and the
unchanged paths are confirmed, it clears the saved Arr queue entries using
queue-only removal. Configured blocklisting uses that same queue request. A
replacement is queued only after a fresh Arr queue read confirms clearance.
Pending and confirmed import rescue take precedence over the move.

SQLite records the original client, download epoch, Arr server and owner category,
paths, and queue identities before the category change. Catalog sync can continue the queue step
after a restart, including after the policy is disabled. It reviews up to 50
pending receipts per batch with a 30-second budget. An ambiguous blocklist response
holds the operation if the row is still present. A move never falls back to a
torrent or file delete. Its receipt continues to protect the original download
epoch against automatic removal, even after a later category or policy change.

The upstream contracts are documented in the
[qBittorrent category change](https://github.com/qbittorrent/qBittorrent/blob/master/src/base/bittorrent/torrentimpl.cpp)
and the queue controllers for
[Radarr](https://github.com/Radarr/Radarr/blob/develop/src/Radarr.Api.V3/Queue/QueueController.cs),
[Sonarr](https://github.com/Sonarr/Sonarr/blob/develop/src/Sonarr.Api.V3/Queue/QueueController.cs),
[Lidarr](https://github.com/Lidarr/Lidarr/blob/develop/src/Lidarr.Api.V1/Queue/QueueController.cs),
and [Readarr](https://github.com/Readarr/Readarr/blob/develop/src/Readarr.Api.V1/Queue/QueueController.cs).
