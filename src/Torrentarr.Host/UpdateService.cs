using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Host;

/// <summary>
/// Singleton service that checks GitHub for new releases and, on request, downloads and applies the update.
/// §6.10 update endpoints + §1.8 auto-update.
/// </summary>
public class UpdateService
{
    private readonly ILogger<UpdateService> _logger;
    private readonly TorrentarrConfig _config;
    private readonly WorkerProcessSupervisor? _workers;

    // Cached check result — refreshed at most once per hour
    private DateTime _lastChecked = DateTime.MinValue;
    private string? _checkedChannel;
    private string? _latestVersion;
    private bool _updateAvailable;
    private string? _changelog;
    private string? _changelogUrl;
    private string? _binaryDownloadUrl;
    private string? _binaryDownloadName;
    private long? _binaryDownloadSize;
    private string? _binaryDownloadError;
    private string? _workerDownloadUrl;
    private string? _workerDownloadName;
    private string? _checkError;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    private const string RepoOwner = "Feramance";
    private const string RepoName = "Torrentarr";
    private const string GithubLatestUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    private const string GithubReleasesUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases";
    public const string RepositoryUrl = $"https://github.com/{RepoOwner}/{RepoName}";

    /// <summary>State of any in-progress or last completed apply operation.</summary>
    public UpdateApplyState ApplyState { get; } = new();

    private static bool IsContainer() =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
        || File.Exists("/.dockerenv");

    public UpdateService(ILogger<UpdateService> logger, TorrentarrConfig config, WorkerProcessSupervisor? workers = null)
    {
        _logger = logger;
        _config = config;
        _workers = workers;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var saved = System.Text.Json.JsonSerializer.Deserialize<UpdateApplyState>(File.ReadAllText(UpdateResultPath));
                if (saved != null)
                {
                    ApplyState.LastResult = saved.LastResult;
                    ApplyState.LastError = saved.LastError;
                    ApplyState.CompletedAt = saved.CompletedAt;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
    }

    private static string UpdateResultPath => Path.Combine(ConfigurationLoader.GetDataDirectoryPath(), "update-result.json");

    public string AutoUpdateChannel =>
        ConfigurationLoader.NormalizeAutoUpdateChannel(_config.Settings.AutoUpdateChannel);

    public bool CanApplyBinaries => !IsContainer() && !IsSourceBuild() && AutoUpdateChannel != "nightly";

    public static string GetCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        var informational = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

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
        if (!forceRefresh && _checkedChannel == AutoUpdateChannel && _lastChecked != DateTime.MinValue &&
            (DateTime.UtcNow - _lastChecked).TotalHours < 1)
            return;

        await _checkLock.WaitAsync(ct);
        try
        {
            // Double-check inside lock
            if (!forceRefresh && _checkedChannel == AutoUpdateChannel && _lastChecked != DateTime.MinValue &&
                (DateTime.UtcNow - _lastChecked).TotalHours < 1)
                return;

            var channel = AutoUpdateChannel;
            if (_checkedChannel != channel)
            {
                _latestVersion = null;
                _updateAvailable = false;
                _binaryDownloadUrl = null;
                _workerDownloadUrl = null;
            }
            _checkedChannel = channel;
            var currentVersion = GetCurrentVersion();
            var assetPattern = GetAssetPattern();

            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", $"{RepoName}/{currentVersion}");
                http.Timeout = TimeSpan.FromSeconds(10);

                var release = await FetchReleaseForChannelAsync(http, channel, ct);
                if (release == null)
                {
                    _checkError = "GitHub API returned no matching release";
                    _lastChecked = DateTime.UtcNow;
                    return;
                }

                ApplyReleaseMetadata(release, currentVersion, assetPattern, allowBinary: CanApplyBinaries);
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
                installation_type = IsContainer() ? "docker" : IsSourceBuild() ? "source" : "binary",
                auto_update_supported = !IsContainer(),
                update_channel = _config.Settings.AutoUpdateChannel,
                binary_download_url = _binaryDownloadUrl,
                binary_download_name = _binaryDownloadName,
                binary_download_size = _binaryDownloadSize,
                binary_download_error = _binaryDownloadError,
                worker_download_url = _workerDownloadUrl,
                worker_download_name = _workerDownloadName,
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
            installation_type = IsContainer() ? "docker" : IsSourceBuild() ? "source" : "binary",
            auto_update_supported = !IsContainer(),
            update_channel = _config.Settings.AutoUpdateChannel,
            binary_download_url = _binaryDownloadUrl,
            binary_download_name = _binaryDownloadName,
            binary_download_size = _binaryDownloadSize,
            binary_download_error = _binaryDownloadError,
            worker_download_url = _workerDownloadUrl,
            worker_download_name = _workerDownloadName,
            platform = Environment.OSVersion.Platform.ToString(),
            runtime = $".NET {Environment.Version}",
            auth_required = !webUi.AuthDisabled,
            local_auth_enabled = webUi.LocalAuthEnabled,
            oidc_enabled = webUi.OIDCEnabled,
            setup_required = !webUi.AuthDisabled && webUi.LocalAuthEnabled && string.IsNullOrEmpty(webUi.PasswordHash),
            url_base = UrlBaseHelper.NormalizeUrlBase(webUi.UrlBase)
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

        if (IsSourceBuild())
        {
            ApplyState.LastResult = "error";
            ApplyState.LastError = "Auto-update is disabled for source / dotnet run builds.";
            ApplyState.CompletedAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        if (AutoUpdateChannel == "nightly")
        {
            ApplyState.LastResult = "error";
            ApplyState.LastError = "Nightly channel does not download or apply binaries.";
            ApplyState.CompletedAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        if (IsContainer())
        {
            ApplyState.LastResult = "error";
            ApplyState.LastError = "Docker installations update by pulling and recreating the container.";
            ApplyState.CompletedAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        if (_checkedChannel != AutoUpdateChannel || string.IsNullOrEmpty(_binaryDownloadUrl) || string.IsNullOrEmpty(_workerDownloadUrl))
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
            string? tempDir = null;
            var helperStarted = false;
            var workersStopped = false;
            try
            {
                var currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                    ?? throw new InvalidOperationException("Cannot determine current executable path");
                var currentDir = Path.GetDirectoryName(currentExe)!;
                tempDir = Path.Combine(Path.GetTempPath(), $"torrentarr-update-{Guid.NewGuid():N}");
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

                var workerName = _workerDownloadName ?? "worker-update";
                var workerPath = Path.Combine(tempDir, workerName);
                using (var response = await http.GetAsync(_workerDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    using var fs = File.Create(workerPath);
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
                    File.Copy(archivePath, Path.Combine(extractDir, archiveName), overwrite: true);
                }
                File.Copy(workerPath, Path.Combine(extractDir, workerName), overwrite: true);

                if (OperatingSystem.IsWindows())
                {
                    var workerPids = _workers == null ? Array.Empty<int>() : (await _workers.GetProcessStatusAsync()).Values
                        .Where(s => s.ProcessId.HasValue).Select(s => s.ProcessId!.Value).ToArray();
                    if (_workers != null)
                    {
                        workersStopped = true;
                        await ((Torrentarr.Core.Interfaces.IProcessOrchestrator)_workers).StopAsync(CancellationToken.None);
                    }
                    ApplyWindowsUpdate(currentExe, currentDir, extractDir, workerPids);
                    helperStarted = true;
                    ApplyState.LastResult = "pending";
                }
                else
                {
                    await ApplyUnixUpdateAsync(currentExe, currentDir, extractDir);
                    ApplyState.LastResult = "success";
                    ApplyState.CompletedAt = DateTime.UtcNow;
                    ApplyState.InProgress = false;
                }
                ApplyState.LastError = null;

                _logger.LogInformation("UpdateService: Update staged or applied — stopping application for restart");
                await Task.Delay(500, CancellationToken.None);
                lifetime.StopApplication();
            }
            catch (Exception ex)
            {
                if (workersStopped && !helperStarted && _workers != null)
                    try { await ((Torrentarr.Core.Interfaces.IProcessOrchestrator)_workers).StartAsync(CancellationToken.None); }
                    catch (Exception restartError) { _logger.LogError(restartError, "Unable to resume workers after failed update"); }
                _logger.LogError(ex, "UpdateService: Update failed");
                ApplyState.InProgress = false;
                ApplyState.LastResult = "error";
                ApplyState.LastError = ex.Message;
                ApplyState.CompletedAt = DateTime.UtcNow;
            }
            finally
            {
                if (!helperStarted && tempDir != null)
                    try { Directory.Delete(tempDir, true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Unable to remove update staging directory"); }
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    private sealed record UpdateTarget(string Source, string Destination, string Staged, string Backup, bool Existed);

    private static List<UpdateTarget> ResolveUpdateTargets(string currentExe, string currentDir, string extractDir, string? configuredWorker)
    {
        var host = Directory.GetFiles(extractDir, "torrentarr-*").Single(file => !Path.GetFileName(file).StartsWith("torrentarr-workers-", StringComparison.OrdinalIgnoreCase));
        var worker = Directory.GetFiles(extractDir, "torrentarr-workers-*").Single();
        configuredWorker ??= Environment.GetEnvironmentVariable("TORRENTARR_WORKER_PATH");
        var workerDestination = string.IsNullOrWhiteSpace(configuredWorker)
            ? Path.Combine(currentDir, Path.GetFileName(worker)) : Path.GetFullPath(configuredWorker);
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (workerDestination.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(configuredWorker) && !File.Exists(workerDestination)
            || string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(workerDestination), comparer))
            throw new InvalidOperationException("The configured worker must be an existing executable distinct from the Host; DLL overrides cannot receive binary updates.");
        var suffix = Guid.NewGuid().ToString("N");
        // Install the worker first; rollback restores the pair if installing the Host fails.
        return new[] { (worker, workerDestination), (host, currentExe) }
            .Select(pair => new UpdateTarget(pair.Item1, pair.Item2, pair.Item2 + ".new-" + suffix, pair.Item2 + ".bak-" + suffix, File.Exists(pair.Item2))).ToList();
    }

    private static void StageUpdate(List<UpdateTarget> targets, bool unix)
    {
        try
        {
            foreach (var target in targets)
            {
                File.Copy(target.Source, target.Staged, overwrite: false);
                if (unix && !OperatingSystem.IsWindows()) File.SetUnixFileMode(target.Staged, File.GetUnixFileMode(target.Source) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                if (target.Existed) File.Copy(target.Destination, target.Backup, overwrite: false);
            }
        }
        catch
        {
            foreach (var target in targets)
            {
                File.Delete(target.Staged);
                File.Delete(target.Backup);
            }
            throw;
        }
    }

    internal static Task ApplyUnixUpdateAsync(string currentExe, string currentDir, string extractDir, string? configuredWorker = null)
    {
        var targets = ResolveUpdateTargets(currentExe, currentDir, extractDir, configuredWorker);
        StageUpdate(targets, unix: true);
        var installed = new List<UpdateTarget>();
        var recovered = true;
        try
        {
            foreach (var target in targets)
            {
                File.Move(target.Staged, target.Destination, overwrite: true);
                installed.Add(target);
            }
        }
        catch
        {
            foreach (var target in installed.AsEnumerable().Reverse())
            {
                try
                {
                    if (target.Existed) File.Move(target.Backup, target.Destination, overwrite: true);
                    else File.Delete(target.Destination);
                }
                catch { recovered = false; }
            }
            if (!recovered) throw new IOException("Update rollback failed; recovery copies remain beside the installed executables");
            throw;
        }
        finally
        {
            foreach (var target in targets)
            {
                File.Delete(target.Staged);
                if (recovered) File.Delete(target.Backup);
            }
        }
        return Task.CompletedTask;
    }

    private static void ApplyWindowsUpdate(string currentExe, string currentDir, string extractDir, int[] workerPids)
    {
        var targets = ResolveUpdateTargets(currentExe, currentDir, extractDir, null);
        StageUpdate(targets, unix: false);
        var script = BuildWindowsUpdateScript(currentExe, Path.GetDirectoryName(extractDir)!, Environment.ProcessId, workerPids, UpdateResultPath, targets);
        try
        {
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            using var helper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList = { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded },
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Unable to start the Windows update helper");
        }
        catch
        {
            foreach (var target in targets) { File.Delete(target.Staged); File.Delete(target.Backup); }
            throw;
        }
    }

    internal static string PrepareWindowsUpdateScript(string currentExe, string currentDir, string extractDir, int pid,
        int[]? workerPids = null, string? configuredWorker = null, string? resultPath = null)
    {
        var targets = ResolveUpdateTargets(currentExe, currentDir, extractDir, configuredWorker);
        StageUpdate(targets, unix: false);
        return BuildWindowsUpdateScript(currentExe, extractDir, pid, workerPids, resultPath ?? UpdateResultPath, targets);
    }

    private static string BuildWindowsUpdateScript(string currentExe, string cleanupDirectory, int pid, int[]? workerPids,
        string resultPath, List<UpdateTarget> targets)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var targetObjects = string.Join(",\n", targets.Select(t =>
            $"@{{Destination={Quote(t.Destination)};Staged={Quote(t.Staged)};Backup={Quote(t.Backup)};Existed=${t.Existed.ToString().ToLowerInvariant()}}}"));
        var pids = string.Join(",", (workerPids ?? []).Append(pid).Distinct());
        return $$"""
            $ErrorActionPreference = 'Stop'
            $targets = @({{targetObjects}})
            $resultPath = {{Quote(resultPath)}}
            $installed = @()
            $recovered = $true
            $result = @{LastResult='error';LastError=$null;CompletedAt=$null}
            try {
                $processes = @(Get-Process -Id {{pids}} -ErrorAction SilentlyContinue)
                foreach ($process in $processes) { $process.WaitForExit() }
                foreach ($target in $targets) {
                    Move-Item -LiteralPath $target.Staged -Destination $target.Destination -Force
                    $installed += $target
                }
                $result.LastResult = 'success'
            } catch {
                $result.LastError = $_.Exception.Message
                [array]::Reverse($installed)
                foreach ($target in $installed) {
                    try {
                        if ($target.Existed) { Move-Item -LiteralPath $target.Backup -Destination $target.Destination -Force }
                        else { Remove-Item -LiteralPath $target.Destination -Force }
                    } catch { $recovered = $false; $result.LastError += '; rollback: ' + $_.Exception.Message }
                }
            } finally {
                try {
                $result.CompletedAt = [DateTime]::UtcNow.ToString('o')
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resultPath)) | Out-Null
                $result | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
                foreach ($target in $targets) {
                    Remove-Item -LiteralPath $target.Staged -Force -ErrorAction SilentlyContinue
                    if ($recovered) { Remove-Item -LiteralPath $target.Backup -Force -ErrorAction SilentlyContinue }
                }
                Remove-Item -LiteralPath {{Quote(cleanupDirectory)}} -Recurse -Force -ErrorAction SilentlyContinue
                } finally {
                    Start-Process -FilePath {{Quote(currentExe)}} -WorkingDirectory {{Quote(Directory.GetCurrentDirectory())}}
                }
            }
            """;
    }

    internal static bool IsSourceBuild()
    {
        var env = Environment.GetEnvironmentVariable("TORRENTARR_SOURCE_BUILD")
                  ?? Environment.GetEnvironmentVariable("QBITRR_SOURCE_BUILD");
        if (string.Equals(env, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase))
            return true;

        // Assembly.Location is empty for assemblies bundled into a single-file app.
        // AppContext.BaseDirectory remains available in both bundled and source builds.
        var location = AppContext.BaseDirectory;
        var sep = Path.DirectorySeparatorChar;
        if (location.Contains($"{sep}bin{sep}Debug{sep}", StringComparison.OrdinalIgnoreCase)
            || location.Contains($"{sep}bin{sep}Release{sep}", StringComparison.OrdinalIgnoreCase))
            return true;

        return HasGitDirectory(Directory.GetCurrentDirectory())
               || HasGitDirectory(AppContext.BaseDirectory);
    }

    private static bool HasGitDirectory(string? start)
    {
        var dir = start;
        for (var i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    private async Task<JObject?> FetchReleaseForChannelAsync(HttpClient http, string channel, CancellationToken ct)
    {
        if (channel == "latest")
        {
            var response = await http.GetAsync(GithubLatestUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                _checkError = $"GitHub API returned {(int)response.StatusCode}";
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(ct);
            return JObject.Parse(json);
        }

        var listResponse = await http.GetAsync(GithubReleasesUrl, ct);
        if (!listResponse.IsSuccessStatusCode)
        {
            _checkError = $"GitHub API returned {(int)listResponse.StatusCode}";
            return null;
        }

        var listJson = await listResponse.Content.ReadAsStringAsync(ct);
        var releases = JArray.Parse(listJson);
        foreach (var item in releases.OfType<JObject>())
        {
            var prerelease = item["prerelease"]?.ToObject<bool>() ?? false;
            var tag = item["tag_name"]?.ToObject<string>();
            if (SkipReleaseForChannel(channel, prerelease, tag))
                continue;
            return item;
        }

        return null;
    }

    private void ApplyReleaseMetadata(JObject release, string currentVersion, string assetPattern, bool allowBinary)
    {
        var latestTag = release["tag_name"]?.ToObject<string>() ?? currentVersion;
        _latestVersion = latestTag.TrimStart('v');
        _updateAvailable = IsNewerVersion(_latestVersion, currentVersion);
        _changelog = release["body"]?.ToObject<string>();
        _changelogUrl = release["html_url"]?.ToObject<string>();
        _checkError = null;

        if (!allowBinary)
        {
            _binaryDownloadUrl = null;
            _binaryDownloadName = null;
            _binaryDownloadSize = null;
            _workerDownloadUrl = null;
            _workerDownloadName = null;
            _binaryDownloadError = AutoUpdateChannel == "nightly"
                ? "Nightly channel does not download or apply binaries"
                : "Source builds cannot apply binary updates";
            return;
        }

        var assets = release["assets"] as JArray;
        var asset = assets?.FirstOrDefault(a =>
            a["name"]?.ToObject<string>() is { } name
            && name.StartsWith("torrentarr-", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("torrentarr-workers-", StringComparison.OrdinalIgnoreCase)
            && name.Contains(assetPattern, StringComparison.OrdinalIgnoreCase));
        var workerAsset = assets?.FirstOrDefault(a =>
            a["name"]?.ToObject<string>() is { } name
            && name.StartsWith("torrentarr-workers-", StringComparison.OrdinalIgnoreCase)
            && name.Contains(GetWorkerAssetPattern(), StringComparison.OrdinalIgnoreCase));

        if (asset != null && workerAsset != null)
        {
            _binaryDownloadUrl = asset["browser_download_url"]?.ToObject<string>();
            _binaryDownloadName = asset["name"]?.ToObject<string>();
            _binaryDownloadSize = asset["size"]?.ToObject<long?>();
            _binaryDownloadError = null;
            _workerDownloadUrl = workerAsset["browser_download_url"]?.ToObject<string>();
            _workerDownloadName = workerAsset["name"]?.ToObject<string>();
        }
        else
        {
            _binaryDownloadUrl = null;
            _binaryDownloadName = null;
            _binaryDownloadSize = null;
            _binaryDownloadError = $"No asset found for platform: {assetPattern}";
            _workerDownloadUrl = null;
            _workerDownloadName = null;
        }
    }

    private static string GetAssetPattern() => GetAssetPattern(
        OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux,
        RuntimeInformation.ProcessArchitecture);

    internal static string GetAssetPattern(OSPlatform platform, Architecture architecture)
    {
        var os = platform == OSPlatform.Windows ? "windows" : platform == OSPlatform.OSX ? "macos" : "linux";
        return $"{os}-{(architecture == Architecture.Arm64 ? "arm64" : "x64")}";
    }

    private static string GetWorkerAssetPattern() => GetAssetPattern().Replace("macos-", "osx-", StringComparison.Ordinal);

    /// <summary>
    /// <c>stable</c> matches Docker <c>:stable</c>: skip GitHub prereleases and weekly
    /// dependency builds (<c>MAJOR.MINOR.PATCH-N</c> where <c>N &gt; 1</c>).
    /// Patch/minor/major releases use <c>-1</c> (or no suffix) and remain eligible.
    /// </summary>
    internal static bool SkipReleaseForChannel(string channel, bool prerelease, string? tagName)
    {
        if (!string.Equals(channel, "stable", StringComparison.OrdinalIgnoreCase))
            return false;
        return prerelease || IsWeeklyBuildTag(tagName);
    }

    /// <summary>
    /// Weekly <c>release_type=build</c> increments the numeric suffix
    /// (<c>6.14.3-1</c> → <c>6.14.3-2</c>). First-of-line tags (<c>6.14.3</c> / <c>6.14.3-1</c>)
    /// are patch/minor/major and must not be skipped on <c>stable</c>.
    /// </summary>
    internal static bool IsWeeklyBuildTag(string? tag)
    {
        var parsed = ParseReleaseVersion(tag ?? "");
        return parsed is { Build: > 1 };
    }

    internal static bool IsNewerVersion(string latest, string current)
    {
        var parsedLatest = ParseReleaseVersion(latest);
        var parsedCurrent = ParseReleaseVersion(current);
        if (parsedLatest is null || parsedCurrent is null)
            return false;
        return parsedLatest.Value.CompareTo(parsedCurrent.Value) > 0;
    }

    /// <summary>
    /// Parse <c>MAJOR.MINOR.PATCH</c> or qBitrr-style <c>MAJOR.MINOR.PATCH-BUILD</c> (optional leading <c>v</c>).
    /// </summary>
    internal static (int Major, int Minor, int Patch, int Build)? ParseReleaseVersion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];

        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        var build = 0;
        if (dash >= 0)
        {
            var suffix = s[(dash + 1)..];
            if (!int.TryParse(suffix, out build))
                return null;
        }

        if (!Version.TryParse(core, out var v))
            return null;

        return (v.Major, v.Minor, v.Build, build);
    }
}

/// <summary>State of an in-progress or last completed update-apply operation.</summary>
public class UpdateApplyState
{
    public bool InProgress { get; set; }
    /// <summary>"pending" | "success" | "error" | null</summary>
    public string? LastResult { get; set; }
    public string? LastError { get; set; }
    public DateTime? CompletedAt { get; set; }
}
