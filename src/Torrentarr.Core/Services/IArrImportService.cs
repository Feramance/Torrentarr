namespace Torrentarr.Core.Services;

public interface IArrImportService
{
    Task<ImportResult> TriggerImportAsync(
        string hash,
        string contentPath,
        string category,
        CancellationToken cancellationToken = default);

    Task<bool> IsImportedAsync(string hash, CancellationToken cancellationToken = default);

    Task MarkAsImportedAsync(string hash, IEnumerable<string> tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a torrent's custom format score is unmet per Arr queue data.
    /// Matches qBitrr's custom_format_unmet_check (arss.py:6255-6324).
    /// Returns true if the torrent should be deleted due to unmet CF requirements.
    /// </summary>
    Task<bool> IsCustomFormatUnmetAsync(string hash, string category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Blocklist a torrent in the Arr queue and trigger a re-search.
    /// Used by ReSearchStalled: removes from queue with blocklist=true so Arr re-searches.
    /// Matches qBitrr's process_entries + _process_failed_individual.
    /// </summary>
    Task<bool> BlocklistAndReSearchAsync(string hash, string category, CancellationToken cancellationToken = default);
}

public class ImportResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int? CommandId { get; set; }
}
