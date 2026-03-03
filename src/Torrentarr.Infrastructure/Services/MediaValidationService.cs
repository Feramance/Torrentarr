using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Service for validating media files using ffprobe.
/// Supports downloading and updating ffprobe binary automatically.
/// </summary>
public class MediaValidationService : IMediaValidationService
{
    private readonly ILogger<MediaValidationService> _logger;
    private readonly TorrentarrConfig _config;
    private readonly string _ffprobePath;
    private readonly string _ffprobeVersionPath;
    private readonly HashSet<string> _probedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _mediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm",
        ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".wma",
        ".m2ts", ".ts", ".vob", ".mpg", ".mpeg"
    };

    public bool IsFFprobeAvailable => File.Exists(_ffprobePath);

    public MediaValidationService(
        ILogger<MediaValidationService> logger,
        TorrentarrConfig config)
    {
        _logger = logger;
        _config = config;

        var appDataPath = GetAppDataPath();
        _ffprobePath = Path.Combine(appDataPath, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe");
        _ffprobeVersionPath = Path.Combine(appDataPath, "ffprobe_info.json");
    }

    public async Task<MediaValidationResult> ValidateFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var result = new MediaValidationResult { FilePath = filePath };

        if (!IsFFprobeAvailable)
        {
            result.IsValid = true;
            result.ErrorMessage = "ffprobe not available";
            _logger.LogTrace("ffprobe not available, skipping validation for {File}", filePath);
            return result;
        }

        if (!File.Exists(filePath))
        {
            result.IsValid = false;
            result.ErrorMessage = "File not found";
            return result;
        }

        if (_probedFiles.Contains(filePath))
        {
            result.IsValid = true;
            _logger.LogTrace("File already probed: {File}", filePath);
            return result;
        }

        if (filePath.EndsWith(".!qB", StringComparison.OrdinalIgnoreCase))
        {
            result.IsValid = false;
            result.ErrorMessage = "File still downloading";
            _logger.LogTrace("File still downloading: {File}", filePath);
            return result;
        }

        var extension = Path.GetExtension(filePath);
        if (!_mediaExtensions.Contains(extension))
        {
            result.IsValid = true;
            result.IsMediaFile = false;
            _logger.LogTrace("File is not a media file: {File}", filePath);
            return result;
        }

        try
        {
            var probeResult = await RunFFprobeAsync(filePath, cancellationToken);
            
            if (probeResult == null)
            {
                result.IsValid = false;
                result.ErrorMessage = "ffprobe returned no output";
                return result;
            }

            result.IsValid = true;
            result.IsMediaFile = true;
            result.Format = probeResult.Format?.FormatName;
            result.Duration = probeResult.Format?.Duration;
            result.Codec = probeResult.Streams?.FirstOrDefault()?.CodecName;
            result.Width = probeResult.Streams?.FirstOrDefault()?.Width;
            result.Height = probeResult.Streams?.FirstOrDefault()?.Height;

            _probedFiles.Add(filePath);
            _logger.LogTrace("Validated media file: {File} (Format: {Format}, Duration: {Duration})",
                filePath, result.Format, result.Duration);
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.ErrorMessage = ex.Message;
            
            if (ex.Message.Contains("Invalid data found when processing input"))
            {
                _logger.LogWarning("Invalid media file: {File}", filePath);
            }
            else
            {
                _logger.LogError(ex, "Error probing file: {File}", filePath);
            }
        }

        return result;
    }

    public async Task<DirectoryValidationResult> ValidateDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        var result = new DirectoryValidationResult { DirectoryPath = directoryPath };

        if (!Directory.Exists(directoryPath))
        {
            _logger.LogWarning("Directory not found: {Directory}", directoryPath);
            return result;
        }

        var files = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories);
        result.TotalFiles = files.Length;

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            
            if (fileName is "desktop.ini" or ".DS_Store")
                continue;

            var extension = Path.GetExtension(file);
            if (extension.Equals(".parts", StringComparison.OrdinalIgnoreCase))
                continue;

            var validationResult = await ValidateFileAsync(file, cancellationToken);
            result.Results.Add(validationResult);

            if (validationResult.IsValid && validationResult.IsMediaFile)
                result.ValidFiles++;
            else if (!validationResult.IsValid)
                result.InvalidFiles++;
        }

        _logger.LogInformation("Directory validation complete: {Directory} - {Valid}/{Total} valid media files",
            directoryPath, result.ValidFiles, result.TotalFiles);

        return result;
    }

    public async Task<bool> UpdateFFprobeAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.Settings.FFprobeAutoUpdate)
        {
            _logger.LogTrace("FFprobe auto-update disabled");
            return false;
        }

        try
        {
            var currentVersion = GetCurrentVersion();
            var latestVersion = await GetLatestVersionAsync(cancellationToken);

            if (latestVersion == null)
            {
                _logger.LogWarning("Could not retrieve latest ffprobe version");
                return false;
            }

            if (currentVersion == latestVersion.Version && IsFFprobeAvailable)
            {
                _logger.LogTrace("FFprobe is up to date: {Version}", currentVersion);
                return true;
            }

            var downloadUrl = GetDownloadUrl(latestVersion);
            if (downloadUrl == null)
            {
                _logger.LogWarning("Could not determine ffprobe download URL for current platform");
                return false;
            }

            _logger.LogInformation("Downloading ffprobe from: {Url}", downloadUrl);
            await DownloadAndExtractAsync(downloadUrl, cancellationToken);

            await File.WriteAllTextAsync(_ffprobeVersionPath, 
                JsonSerializer.Serialize(new { version = latestVersion.Version }), cancellationToken);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    File.SetUnixFileMode(_ffprobePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to set ffprobe permissions");
                }
            }

            _logger.LogInformation("FFprobe updated to version {Version}", latestVersion.Version);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update ffprobe");
            return false;
        }
    }

    private async Task<FFprobeResult?> RunFFprobeAsync(string filePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            Arguments = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new Exception($"ffprobe failed: {error}");
        }

        return JsonSerializer.Deserialize<FFprobeResult>(output);
    }

    private string GetCurrentVersion()
    {
        try
        {
            if (!File.Exists(_ffprobeVersionPath))
                return "";

            var json = File.ReadAllText(_ffprobeVersionPath);
            var data = JsonSerializer.Deserialize<FFprobeVersionInfo>(json);
            return data?.Version ?? "";
        }
        catch
        {
            return "";
        }
    }

    private async Task<FFprobeLatestVersion?> GetLatestVersionAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        try
        {
            var response = await client.GetStringAsync("https://ffbinaries.com/api/v1/version/latest", cancellationToken);
            return JsonSerializer.Deserialize<FFprobeLatestVersion>(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get latest ffprobe version");
            return null;
        }
    }

    private string? GetDownloadUrl(FFprobeLatestVersion version)
    {
        var archKey = GetArchitectureKey();
        return version.Bin?.GetValueOrDefault(archKey)?.GetValueOrDefault("ffprobe");
    }

    private string GetArchitectureKey()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Environment.Is64BitOperatingSystem ? "windows-64" : "windows-32";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var arch = RuntimeInformation.ProcessArchitecture;
            return arch switch
            {
                Architecture.Arm64 => "linux-arm64",
                Architecture.Arm => "linux-armhf",
                _ => Environment.Is64BitOperatingSystem ? "linux-64" : "linux-32"
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "osx-64";
        }

        throw new PlatformNotSupportedException("Unsupported platform for ffprobe");
    }

    private async Task DownloadAndExtractAsync(string url, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        var zipData = await client.GetByteArrayAsync(url, cancellationToken);

        var tempPath = Path.Combine(Path.GetTempPath(), $"ffprobe-{Guid.NewGuid()}.zip");
        await File.WriteAllBytesAsync(tempPath, zipData, cancellationToken);

        try
        {
            using var archive = ZipFile.OpenRead(tempPath);
            var ffprobeEntry = archive.Entries.FirstOrDefault(e => 
                e.Name.StartsWith("ffprobe", StringComparison.OrdinalIgnoreCase));

            if (ffprobeEntry != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_ffprobePath)!);
                ffprobeEntry.ExtractToFile(_ffprobePath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static string GetAppDataPath()
    {
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configPath = Path.Combine(homePath, ".config", "torrentarr");
        
        if (!Directory.Exists(configPath))
            Directory.CreateDirectory(configPath);

        return configPath;
    }

    private class FFprobeResult
    {
        public FFprobeFormat? Format { get; set; }
        public List<FFprobeStream>? Streams { get; set; }
    }

    private class FFprobeFormat
    {
        public string? FormatName { get; set; }
        public TimeSpan? Duration { get; set; }
    }

    private class FFprobeStream
    {
        public string? CodecName { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
    }

    private class FFprobeVersionInfo
    {
        public string? Version { get; set; }
    }

    private class FFprobeLatestVersion
    {
        public string? Version { get; set; }
        public Dictionary<string, Dictionary<string, string>>? Bin { get; set; }
    }
}
