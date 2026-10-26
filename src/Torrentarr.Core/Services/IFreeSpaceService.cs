namespace Torrentarr.Core.Services;

public interface IFreeSpaceService
{
    Task<IReadOnlyList<FreeSpaceStats>> GetFreeSpaceStatsAsync(CancellationToken cancellationToken = default);
    Task<FreeSpacePassResult> ProcessAsync(IReadOnlySet<string> managedCategories, CancellationToken cancellationToken = default);
}

public record FreeSpacePassResult(int Paused, int Resumed, int MarkedCount, bool Available);

public class FreeSpaceStats
{
    public string ClientInstanceId { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Error { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long ThresholdBytes { get; set; }
    public bool BelowThreshold { get; set; }
}
