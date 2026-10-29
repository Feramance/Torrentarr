using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Torrentarr.Infrastructure.Database.Models;

[Table("torrentarr_operational_issues")]
public sealed class OperationalIssue
{
    [Key] public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Category { get; set; } = "";
    public string ArrInstance { get; set; } = "";
    public string ClientInstance { get; set; } = "";
    public string SubjectType { get; set; } = "service";
    public string SubjectId { get; set; } = "";
    public string Severity { get; set; } = "error";
    public string Title { get; set; } = "";
    public string Impact { get; set; } = "";
    public string Remediation { get; set; } = "";
    public string Target { get; set; } = "logs";
    public DateTime FirstObserved { get; set; }
    public DateTime LastObserved { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? Resolution { get; set; }
    public long Occurrences { get; set; }
}
