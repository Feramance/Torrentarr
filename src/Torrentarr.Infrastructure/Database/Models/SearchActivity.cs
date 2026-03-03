using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Torrentarr.Infrastructure.Database.Models;

/// <summary>
/// §5 qBitrr parity: Last search activity per category for Processes page (persists across restarts).
/// </summary>
[Table("searchactivity")]
public class SearchActivity
{
    [Key]
    [Column("category")]
    public string Category { get; set; } = "";

    [Column("summary")]
    public string? Summary { get; set; }

    [Column("timestamp")]
    public string? Timestamp { get; set; }
}
