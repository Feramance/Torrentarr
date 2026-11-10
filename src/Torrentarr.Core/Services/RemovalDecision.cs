namespace Torrentarr.Core.Services;

public sealed record RemovalBlocker(string Code, string Reason, string Remediation);

public sealed record RemovalDecision(IReadOnlyList<RemovalBlocker> Blockers)
{
    // Native limits are cleared automatically; only deadline risk is actionable.
    public bool IsExpectedProtectionWait() => Blockers.Count > 0 && Blockers.All(b =>
        b.Code is "hnr.protected" or "hnr.protected.native-stop-risk");
    public bool Allowed => Blockers.Count == 0;
    public string Reason => string.Join("; ", Blockers.Select(b => $"{b.Code}: {b.Reason} Next: {b.Remediation}"));
}
