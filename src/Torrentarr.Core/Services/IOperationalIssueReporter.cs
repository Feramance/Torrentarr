namespace Torrentarr.Core.Services;

public sealed record OperationalIssueReport(string Code, string Category, string Title, string Impact,
    string Remediation, string ArrInstance = "", string ClientInstance = "", string SubjectType = "service",
    string SubjectId = "", string Severity = "error", string Target = "logs");

public sealed record PendingOperationalIssue(OperationalIssueReport Report, bool Resolved, DateTime ObservedAt, long ConfigurationRevision = 0, DateTime? FirstObserved = null);

public interface IOperationalIssueReporter
{
    Task ReportAsync(OperationalIssueReport report, CancellationToken ct = default);
    Task ResolveAsync(OperationalIssueReport report, CancellationToken ct = default);
    IReadOnlyList<PendingOperationalIssue> Pending { get; }
}
