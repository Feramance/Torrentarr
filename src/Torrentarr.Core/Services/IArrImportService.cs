namespace Torrentarr.Core.Services;

/// <summary>
/// Service for triggering manual imports to Arr applications
/// </summary>
public interface IArrImportService
{
    /// <summary>
    /// Trigger manual import for a completed torrent
    /// </summary>
    Task<ImportResult> TriggerImportAsync(
        string hash,
        string contentPath,
        string category,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a torrent has been imported
    /// </summary>
    Task<bool> IsImportedAsync(string hash, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an import operation
/// </summary>
public class ImportResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int? CommandId { get; set; }
}
