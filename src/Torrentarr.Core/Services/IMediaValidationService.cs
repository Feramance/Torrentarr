namespace Torrentarr.Core.Services;

/// <summary>
/// Service for validating media files using ffprobe
/// </summary>
public interface IMediaValidationService
{
    /// <summary>
    /// Check if ffprobe is available
    /// </summary>
    bool IsFFprobeAvailable { get; }

    /// <summary>
    /// Validate a media file
    /// </summary>
    Task<MediaValidationResult> ValidateFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate all media files in a directory
    /// </summary>
    Task<DirectoryValidationResult> ValidateDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Download/update ffprobe binary
    /// </summary>
    Task<bool> UpdateFFprobeAsync(CancellationToken cancellationToken = default);
}

public class MediaValidationResult
{
    public string FilePath { get; set; } = "";
    public bool IsValid { get; set; }
    public bool IsMediaFile { get; set; }
    public string? Codec { get; set; }
    public string? Format { get; set; }
    public TimeSpan? Duration { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? ErrorMessage { get; set; }
}

public class DirectoryValidationResult
{
    public string DirectoryPath { get; set; } = "";
    public int TotalFiles { get; set; }
    public int ValidFiles { get; set; }
    public int InvalidFiles { get; set; }
    public List<MediaValidationResult> Results { get; set; } = new();
    public bool HasValidMedia => ValidFiles > 0;
}
