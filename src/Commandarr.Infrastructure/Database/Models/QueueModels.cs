using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Commandarr.Infrastructure.Database.Models;

/// <summary>
/// Movie queue model matching qBitrr's Peewee schema
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
}

/// <summary>
/// Episode queue model matching qBitrr's Peewee schema
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
}

/// <summary>
/// Album queue model matching qBitrr's Peewee schema
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
}
