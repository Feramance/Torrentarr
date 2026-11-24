using System.ComponentModel.DataAnnotations.Schema;

namespace Torrentarr.Infrastructure.Database.Models;

[Table("torrentarr_search_decisions")]
public sealed class SearchDecision
{
    public string Instance { get; set; } = "";
    public string Key { get; set; } = "";
    public long ObservedAt { get; set; }
    public int Occurrences { get; set; }
    public long? GrabbedAt { get; set; }
    public long? NoGrabAt { get; set; }
    public string Data { get; set; } = "";
}
