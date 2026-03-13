using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;

namespace Torrentarr.Host;

/// <summary>
/// Singleton service that checks GitHub for new releases and, on request, downloads and applies the update.
/// §6.10 update endpoints + §1.8 auto-update.
/// </summary>
public class UpdateService
{
    private readonly ILogger<UpdateService> _logger;

    // Cached check result — refreshed at most once per hour
    private DateTime _lastChecked = DateTime.MinValue;
    private string? _latestVersion;
    private bool _updateAvailable;
    private string? _changelog;
    private string? _changelogUrl;
    private string? _binaryDownloadUrl;
    private string? _binaryDownloadName;
    private long? _binaryDownloadSize;
    private string? _binaryDownloadError;
    private string? _checkError;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    private const string RepoOwner = "Feramance";
    private const string RepoName = "Torrentarr";
    private const string GithubApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    public const string RepositoryUrl = $"https://github.com/{RepoOwner}/{RepoName}";

    /// <summary>State of any in-progress or last completed apply operation.</summary>
    public UpdateApplyState ApplyState { get; } = new();

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
    }

    public static string GetCurrentVersion()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        var ver = asm?.GetName().Version;
        if (ver == null) return "0.0.0";
        return $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }

    /// <summary>
    /// Fetches the latest GitHub release and caches the result for one hour.
    /// Set <paramref name="forceRefresh"/> to bypass the cache.
    /// </summary>
    public async Task CheckForUpdateAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _lastChecked != DateTime.MinValue &&
            (DateTime.UtcNow - _lastChecked).TotalHours < 1)
            return;

        await _checkLock.WaitAsync(ct);
        try
        {
            // Double-check inside lock
            if (!forceRefresh && _lastChecked != DateTime.MinValue &&
                (DateTime.UtcNow - _lastChecked).TotalHours < 1)
                return;

            var currentVersion = GetCurrentVersion();
            var assetPattern = GetAssetPattern();

            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", $"{RepoName}/{currentVersion}");
                http.Timeout = TimeSpan.FromSeconds(10);

                var response = await http.GetAsync(GithubApiUrl, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _checkError = $"GitHub API returned {(int)response.StatusCode}";
                    _lastChecked = DateTime.UtcNow;
                    return;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                var release = JObject.Parse(json);

                var latestTag = release["tag_name"]?.ToObject<string>() ?? currentVersion;
                _latestVersion = latestTag.TrimStart('v');
                _updateAvailable = IsNewerVersion(_latestVersion, currentVersion);
                _changelog = release["body"]?.ToObject<string>();
                _changelogUrl = release["html_url"]?.ToObject<string>();
                _checkError = null;

                // Find the asset that matches the current platform
                var assets = release["assets"] as JArray;
                var asset = assets?.FirstOrDefault(a =>
                    a["name"]?.ToObject<string>()?.Contains(assetPattern, StringComparison.OrdinalIgnoreCase) == true);

                if (asset != null)
                {
                    _binaryDownloadUrl = asset["browser_download_url"]?.ToObject<string>();
                    _binaryDownloadName = asset["name"]?.ToObject<string>();
                    _binaryDownloadSize = asset["size"]?.ToObject<long?>();
                    _binaryDownloadError = null;
                }
                else
                {
                    _binaryDownloadUrl = null;
                    _binaryDownloadName = null;
                    _binaryDownloadSize = null;
                    _binaryDownloadError = $"No asset found for platform: {assetPattern}";
                }
            }
            catch (Exception ex)
            {
                _checkError = ex.Message;
                _logger.LogWarning(ex, "UpdateService: GitHub check failed");
            }
            finally
            {
                _lastChecked = DateTime.UtcNow;
            }
        }
        finally
        {
            _checkLock.Release();
        }
    }

    /// <summary>Builds the MetaResponse-compatible anonymous object for <c>GET /web/meta</c>. When <paramref name="webUi"/> is provided, includes auth_required, local_auth_enabled, oidc_enabled.</summary>
    public object BuildMetaResponse(WebUIConfig? webUi = null)
    {
        var currentVersion = GetCurrentVersion();
        var updateState = new
        {
            in_progress = ApplyState.InProgress,
            last_result = ApplyState.LastResult,
            last_error = ApplyState.LastError,
            completed_at = ApplyState.CompletedAt?.ToString("o")
        };
        if (webUi == null)
        {
            return new
            {
                current_version = currentVersion,
                latest_version = _latestVersion,
                update_available = _updateAvailable,
                changelog = _changelog,
                current_version_changelog = (string?)null,
                changelog_url = _changelogUrl,
                repository_url = RepositoryUrl,
                homepage_url = RepositoryUrl,
                last_checked = _lastChecked == DateTime.MinValue ? (string?)null : _lastChecked.ToString("o"),
                error = _checkError,
                update_state = updateState,
                installation_type = "binary",
                binary_download_url = _binaryDownloadUrl,
                binary_download_name = _binaryDownloadName,
                binary_download_size = _binaryDownloadSize,
                binary_download_error = _binaryDownloadError,
                platform = Environment.OSVersion.Platform.ToString(),
                runtime = $".NET {Environment.Version}"
            };
        }
        return new
        {
            current_version = currentVersion,
            latest_version = _latestVersion,
            update_available = _updateAvailable,
            changelog = _changelog,
            current_version_changelog = (string?)null,
            changelog_url = _changelogUrl,
            repository_url = RepositoryUrl,
            homepage_url = RepositoryUrl,
            last_checked = _lastChecked == DateTime.MinValue ? (string?)null : _lastChecked.ToString("o"),
            error = _checkError,
            update_state = updateState,
            installation_type = "binary",
            binary_download_url = _binaryDownloadUrl,
            binary_download_name = _binaryDownloadName,
            binary_download_size = _binaryDownloadSize,
            binary_download_error = _binaryDownloadError,
            platform = Environment.OSVersion.Platform.ToString(),
            runtime = $".NET {Environment.Version}",
            auth_required = !webUi.AuthDisabled,
            local_auth_enabled = webUi.LocalAuthEnabled,
            oidc_enabled = webUi.OIDCEnabled,
            setup_required = !webUi.AuthDisabled && webUi.LocalAuthEnabled && string.IsNullOrEmpty(webUi.PasswordHash)
        };
    }

    /// <summary>
    /// Downloads the latest binary for the current platform, replaces the running executable,
    /// and stops the application so it can be restarted (by Docker / a process supervisor).
    /// Runs asynchronously in the background so the HTTP response can be returned immediately.
    /// </summary>
    public Task ApplyUpdateAsync(IHostApplicationLifetime lifetime, CancellationToken ct = default)
    {
        if (ApplyState.InProgress)
            return Task.CompletedTask;

        if (string.IsNullOrEmpty(_binaryDownloadUrl))
        {
            ApplyState.LastResult = "error";
            ApplyState.LastError = "No binary download URL available — run update check first.";
            ApplyState.CompletedAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        ApplyState.InProgress = true;
        ApplyState.LastResult = null;
        ApplyState.LastError = null;

        _ = Task.Run(async () =>
        {
            try
            {
                var currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                    ?? throw new InvalidOperationException("Cannot determine current executable path");
                var currentDir = Path.GetDirectoryName(currentExe)!;
                var tempDir = Path.Combine(Path.GetTempPath(), $"torrentarr-update-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);

                _logger.LogInformation("UpdateService: Downloading {Url}", _binaryDownloadUrl);

                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", $"{RepoName}/{GetCurrentVersion()}");
                http.Timeout = TimeSpan.FromMinutes(10);

                var archiveName = _binaryDownloadName ?? "update.zip";
                var archivePath = Path.Combine(tempDir, archiveName);

                using (var response = await http.GetAsync(_binaryDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    using var fs = File.Create(archivePath);
                    await response.Content.CopyToAsync(fs, ct);
                }

                _logger.LogInformation("UpdateService: Extracting {Archive}", archiveName);

                var extractDir = Path.Combine(tempDir, "extracted");
                Directory.CreateDirectory(extractDir);

                if (archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);
                }
                else if (archiveName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                         archiveName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                {
                    await using var gzStream = File.OpenRead(archivePath);
                    await using var decompressed = new GZipStream(gzStream, CompressionMode.Decompress);
                    await TarFile.ExtractToDirectoryAsync(decompressed, extractDir, overwriteFiles: true, ct);
                }
                else
                {
                    throw new NotSupportedException($"Unsupported archive format: {archiveName}");
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    ApplyWindowsUpdate(currentExe, currentDir, extractDir);
                else
                    await ApplyUnixUpdateAsync(currentExe, currentDir, extractDir);

                ApplyState.LastResult = "success";
                ApplyState.LastError = null;
                ApplyState.CompletedAt = DateTime.UtcNow;
                ApplyState.InProgress = false;

                _logger.LogInformation("UpdateService: Update applied — stopping application for restart");
                await Task.Delay(500, CancellationToken.None);
                lifetime.StopApplication();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateService: Update failed");
                ApplyState.InProgress = false;
                ApplyState.LastResult = "error";
                ApplyState.LastError = ex.Message;
                ApplyState.CompletedAt = DateTime.UtcNow;
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    private static async Task ApplyUnixUpdateAsync(string currentExe, string currentDir, string extractDir)
    {
        // Copy all files from the extracted archive into the current directory
        foreach (var file in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(extractDir, file);
            var dest = Path.Combine(currentDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }

        // Ensure the main executable is marked as executable
        var exeName = Path.GetFileName(currentExe);
        var destExe = Path.Combine(currentDir, exeName);
        if (File.Exists(destExe))
            await MakeExecutableAsync(destExe);
    }

    private static void ApplyWindowsUpdate(string currentExe, string currentDir, string extractDir)
    {
        // On Windows we cannot overwrite the running .exe, so we write a helper batch script
        // that waits for this process to exit, then copies the new files and restarts.
        var scriptPath = Path.Combine(Path.GetTempPath(), "torrentarr-update.bat");
        var pid = Environment.ProcessId;

        // Build the script content using a verbatim string for clarity
        var script =
            $"""
            @echo off
            :wait
            tasklist /fi "PID eq {pid}" 2>nul | findstr /i "torrentarr" >nul
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto :wait
            )
            xcopy /Y /E /I "{extractDir}\*" "{currentDir}\"
            start "" "{currentExe}"
            del "%~f0"
            """;

        File.WriteAllText(scriptPath, script);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c start /min \"\" \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static async Task MakeExecutableAsync(string path)
    {
        try
        {
            using var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (chmod != null)
                await chmod.WaitForExitAsync();
        }
        catch { /* best-effort — chmod may not exist on some platforms */ }
    }

    private static string GetAssetPattern()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        // Linux
        return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        if (Version.TryParse(latest, out var l) && Version.TryParse(current, out var c))
            return l > c;
        return false;
    }
}

/// <summary>State of an in-progress or last completed update-apply operation.</summary>
public class UpdateApplyState
{
    public bool InProgress { get; set; }
    /// <summary>"success" | "error" | null</summary>
    public string? LastResult { get; set; }
    public string? LastError { get; set; }
    public DateTime? CompletedAt { get; set; }
}
