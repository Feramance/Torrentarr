using System.ComponentModel.DataAnnotations.Schema;

namespace Torrentarr.Infrastructure.Database.Models;

[Table("torrentarr_issue_alert_events")]
public sealed class IssueAlertEvent
{
    public long Id { get; set; }
    public long IssueId { get; set; }
    public long? OpeningId { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime ObservedAt { get; set; }
    public string State { get; set; } = "";
    public string Code { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Instance { get; set; } = "";
}
