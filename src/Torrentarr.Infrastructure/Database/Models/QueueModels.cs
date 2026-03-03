using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Torrentarr.Infrastructure.Database.Models;

/// <summary>
/// Movie queue model - stores download queue info from Radarr + matching qBittorrent data
/// </summary>
[Table("moviequeuemodel")]
public class MovieQueueModel
{
    [Key]
    [Column("entryid")]
    public int EntryId { get; set; }

    [Column("completed")]
    public bool Completed { get; set; }

    [Column("arrinstance")]
    public string ArrInstance { get; set; } = "";

    [Column("queueid")]
    public int? QueueId { get; set; }

    [Column("movieid")]
    public int? MovieId { get; set; }

    [Column("downloadid")]
    public string? DownloadId { get; set; }

    [Column("title")]
    public string? Title { get; set; }

    [Column("status")]
    public string? Status { get; set; }

    [Column("trackeddownloadstatus")]
    public string? TrackedDownloadStatus { get; set; }

    [Column("trackeddownloadstate")]
    public string? TrackedDownloadState { get; set; }

    [Column("customformatscore")]
    public int? CustomFormatScore { get; set; }

    [Column("quality")]
    public string? Quality { get; set; }

    [Column("size")]
    public long? Size { get; set; }

    [Column("timeleft")]
    public string? TimeLeft { get; set; }

    [Column("estimatedcompletiontime")]
    public DateTime? EstimatedCompletionTime { get; set; }

    [Column("added")]
    public DateTime? Added { get; set; }

    [Column("torrentname")]
    public string? TorrentName { get; set; }

    [Column("torrenthash")]
    public string? TorrentHash { get; set; }

    [Column("torrentcategory")]
    public string? TorrentCategory { get; set; }

    [Column("torrentstate")]
    public string? TorrentState { get; set; }

    [Column("torrentprogress")]
    public double? TorrentProgress { get; set; }

    [Column("torrentcontentpath")]
    public string? TorrentContentPath { get; set; }

    [Column("torrentdownloadpath")]
    public string? TorrentDownloadPath { get; set; }
}

/// <summary>
/// Episode queue model - stores download queue info from Sonarr + matching qBittorrent data
/// </summary>
[Table("episodequeuemodel")]
public class EpisodeQueueModel
{
    [Key]
    [Column("entryid")]
    public int EntryId { get; set; }

    [Column("completed")]
    public bool Completed { get; set; }

    [Column("arrinstance")]
    public string ArrInstance { get; set; } = "";

    [Column("queueid")]
    public int? QueueId { get; set; }

    [Column("episodeid")]
    public int? EpisodeId { get; set; }

    [Column("seriesid")]
    public int? SeriesId { get; set; }

    [Column("seasonnumber")]
    public int? SeasonNumber { get; set; }

    [Column("episodenumber")]
    public int? EpisodeNumber { get; set; }

    [Column("downloadid")]
    public string? DownloadId { get; set; }

    [Column("title")]
    public string? Title { get; set; }

    [Column("seriestitle")]
    public string? SeriesTitle { get; set; }

    [Column("status")]
    public string? Status { get; set; }

    [Column("trackeddownloadstatus")]
    public string? TrackedDownloadStatus { get; set; }

    [Column("trackeddownloadstate")]
    public string? TrackedDownloadState { get; set; }

    [Column("customformatscore")]
    public int? CustomFormatScore { get; set; }

    [Column("quality")]
    public string? Quality { get; set; }

    [Column("size")]
    public long? Size { get; set; }

    [Column("timeleft")]
    public string? TimeLeft { get; set; }

    [Column("estimatedcompletiontime")]
    public DateTime? EstimatedCompletionTime { get; set; }

    [Column("added")]
    public DateTime? Added { get; set; }

    [Column("torrentname")]
    public string? TorrentName { get; set; }

    [Column("torrenthash")]
    public string? TorrentHash { get; set; }

    [Column("torrentcategory")]
    public string? TorrentCategory { get; set; }

    [Column("torrentstate")]
    public string? TorrentState { get; set; }

    [Column("torrentprogress")]
    public double? TorrentProgress { get; set; }

    [Column("torrentcontentpath")]
    public string? TorrentContentPath { get; set; }

    [Column("torrentdownloadpath")]
    public string? TorrentDownloadPath { get; set; }
}

/// <summary>
/// Album queue model - stores download queue info from Lidarr + matching qBittorrent data
/// </summary>
[Table("albumqueuemodel")]
public class AlbumQueueModel
{
    [Key]
    [Column("entryid")]
    public int EntryId { get; set; }

    [Column("completed")]
    public bool Completed { get; set; }

    [Column("arrinstance")]
    public string ArrInstance { get; set; } = "";

    [Column("queueid")]
    public int? QueueId { get; set; }

    [Column("albumid")]
    public int? AlbumId { get; set; }

    [Column("artistid")]
    public int? ArtistId { get; set; }

    [Column("downloadid")]
    public string? DownloadId { get; set; }

    [Column("title")]
    public string? Title { get; set; }

    [Column("artisttitle")]
    public string? ArtistTitle { get; set; }

    [Column("status")]
    public string? Status { get; set; }

    [Column("trackeddownloadstatus")]
    public string? TrackedDownloadStatus { get; set; }

    [Column("trackeddownloadstate")]
    public string? TrackedDownloadState { get; set; }

    [Column("customformatscore")]
    public int? CustomFormatScore { get; set; }

    [Column("quality")]
    public string? Quality { get; set; }

    [Column("size")]
    public long? Size { get; set; }

    [Column("timeleft")]
    public string? TimeLeft { get; set; }

    [Column("estimatedcompletiontime")]
    public DateTime? EstimatedCompletionTime { get; set; }

    [Column("added")]
    public DateTime? Added { get; set; }

    [Column("torrentname")]
    public string? TorrentName { get; set; }

    [Column("torrenthash")]
    public string? TorrentHash { get; set; }

    [Column("torrentcategory")]
    public string? TorrentCategory { get; set; }

    [Column("torrentstate")]
    public string? TorrentState { get; set; }

    [Column("torrentprogress")]
    public double? TorrentProgress { get; set; }

    [Column("torrentcontentpath")]
    public string? TorrentContentPath { get; set; }

    [Column("torrentdownloadpath")]
    public string? TorrentDownloadPath { get; set; }
}

/// <summary>
/// Files queued model matching qBitrr's Peewee schema
/// </summary>
[Table("filesqueued")]
public class FilesQueued
{
    [Key]
    [Column("entryid")]
    public int EntryId { get; set; }

    [Column("arrinstance")]
    public string ArrInstance { get; set; } = "";

    [Column("fileid")]
    public int? FileId { get; set; }

    [Column("filetype")]
    public string? FileType { get; set; }

    [Column("filepath")]
    public string? FilePath { get; set; }
}
