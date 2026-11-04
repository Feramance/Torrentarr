namespace Torrentarr.Core.Services;

public sealed record RemovalBlocker(string Code, string Reason, string Remediation);

public sealed record RemovalDecision(IReadOnlyList<RemovalBlocker> Blockers)
{
    public bool Allowed => Blockers.Count == 0;
    public string Reason => string.Join("; ", Blockers.Select(b => $"{b.Code}: {b.Reason} Next: {b.Remediation}"));
}
