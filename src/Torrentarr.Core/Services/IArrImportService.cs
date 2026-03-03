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
}

public class ImportResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int? CommandId { get; set; }
}
