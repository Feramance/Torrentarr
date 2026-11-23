namespace Torrentarr.Core.Services;

public sealed record RemovalBlocker(string Code, string Reason, string Remediation);

public sealed record RemovalDecision(IReadOnlyList<RemovalBlocker> Blockers)
{
    public bool IsExpectedPolicyDeferral() => Blockers.Count > 0 && Blockers.All(b =>
        b.Code is "hnr.protected" or "hnr.protected.native-stop-risk" or "hnr.unknown"
            or "torrent.minimum-age" or "torrent.free-space-paused" or "stalled.grace" or "seeding.minimum"
            or "seeding.limit" or "seeding.removal-disabled" or "torrent.changed"
            or "retirement.seeders" or "retirement.upload-inactivity" or "retirement.changed" or "retirement.incomplete");
    public bool Allowed => Blockers.Count == 0;
    public string Reason => string.Join("; ", Blockers.Select(b => $"{b.Code}: {b.Reason} Next: {b.Remediation}"));
}
