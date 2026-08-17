# Readarr Configuration

This guide covers how to configure Readarr instances in Torrentarr for book (and audiobook) library management. Readarr is **author → book** only — there is no track layer like Lidarr.

---

## Quick Start

Every Readarr instance requires a dedicated section whose name starts with `Readarr-`.

```toml
[Readarr-Books]
Managed = true
URI = "http://localhost:8787"
APIKey = "your-readarr-api-key"
Category = "readarr-books"
ReSearch = true
ImportMode = "Auto"
RssSyncTimer = 1
RefreshDownloadsTimer = 1
ArrErrorCodesToBlocklist = [
  "Not an upgrade for existing book file(s)",
  "Unable to determine if file is a sample"
]

[Readarr-Books.Torrent]
FileExtensionAllowlist = ['.epub', '.kepub', '.mobi', '.azw', '.azw3', '.pdf', '.cbz', '.cbr', '.flac', '.ape', '.wavpack', '.wav', '.alac', '.mp2', '.mp3', '.wma', '.m4a', '.m4p', '.m4b', '.aac', '.mp4a', '.ogg', '.oga', '.vorbis', '.!qB', '.parts']

[Readarr-Books.Search]
SearchMissing = true
SearchByYear = true
```

!!! warning "Category Mismatch"
    `Category` must match the category configured on Readarr's qBittorrent download client.

!!! tip "Naming Convention"
    - ✅ `Readarr-Books`
    - ✅ `Readarr-Audiobooks`
    - ❌ `Books` (missing prefix)

---

## How Readarr differs from Lidarr

| Lidarr | Readarr |
| --- | --- |
| Artist / album / **track** | Author / **book** (no tracks) |
| `AlbumSearch` | `BookSearch` |
| `DownloadedAlbumsScan` | `DownloadedBooksScan` |
| `SearchByYear` off | `SearchByYear` **on** (book year) |
| Temp quality profile on artist | Temp quality profile on **author** |
| No Ombi/Overseerr | Same — request integrations are Radarr/Sonarr only |
| Default allowlist is audio | Default allowlist is ebook **and** audiobook |

---

## File extensions

New Readarr instances default to ebook + audiobook extensions (including `.m4b`, `.flac`, `.mp3`). Older ebook-only default lists are expanded on load. **Custom** allowlists are never rewritten.

Ebook and comic files (`.epub`, `.pdf`, `.cbz`, …) skip ffprobe so they are not marked invalid.

WebUI save must keep audiobook extensions; do not replace a Readarr allowlist with the video defaults.

---

## Search

`SearchByYear` is enabled for Readarr (unlike Lidarr). Missing-book search, quality upgrades, and custom-format unmet search follow the same `[….Search]` keys as other Arr types.

Ombi and Overseerr blocks are omitted for Readarr.

---

## WebUI

The Readarr tab lists **authors** with expandable **books**. Open in Readarr uses `/web/arr/<category>/open/author/<id>`.

Catalog API:

- `GET /web/readarr/<category>/authors`
- `GET /web/readarr/<category>/author/<id>`
- `GET /web/readarr/<category>/author/<id>/thumbnail`

---

## Related

- [Arr instance configuration](index.md)
- [Configuration file reference](../config-file.md)
- [Lidarr configuration](lidarr.md) (music; includes a track layer Readarr does not have)
