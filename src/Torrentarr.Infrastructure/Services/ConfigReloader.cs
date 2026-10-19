using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public sealed class ConfigReloader : BackgroundService, IConfigReloader
{
    private readonly ILogger<ConfigReloader> _logger;
    private readonly ConfigurationLoader _loader;
    private readonly TorrentarrConfig _config;
    private readonly bool _worker;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, ConfigurationComponentStatus> _components = new();
    private readonly FileSystemWatcher? _watcher;
    private TaskCompletionSource _changed = NewSignal();
    private IReadOnlyList<ConfigurationRename> _renameHistory = [];
    private readonly long _birthRevision;
    private string? _digest;
    private string? _effectiveDigest;
    private string? _error;
    private long _lastChangeTicks;
    private string? _observedDigest;
    private DateTime _observedAt;
    private bool _watching;
    private bool _disposed;
    private long _revision;
    public event EventHandler<ConfigReloadedEventArgs>? ConfigReloaded;
    public event Func<TorrentarrConfig, string?>? ValidateCandidate;
    public string ConfigPath => _loader.ConfigPath;
    public string SnapshotPath => Path.Combine(ConfigurationLoader.GetDataDirectoryPath(ConfigPath), "active-config.json");
    public long Revision => Interlocked.Read(ref _revision);

    public ConfigReloader(ILogger<ConfigReloader> logger) : this(logger, new TorrentarrConfig(), new ConfigurationLoader()) { }
    public ConfigReloader(ILogger<ConfigReloader> logger, TorrentarrConfig config, ConfigurationLoader loader, bool worker = false)
    {
        _logger = logger;
        _config = config;
        _loader = loader;
        _worker = worker;
        _birthRevision = config.Revision;
        var directory = Path.GetDirectoryName(Path.GetFullPath(worker ? SnapshotPath : ConfigPath))!;
        if (Directory.Exists(directory))
        {
            _watcher = new(directory, Path.GetFileName(worker ? SnapshotPath : ConfigPath))
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Renamed += OnChanged;
        }
    }
    public string ResolveInstanceName(string name)
    {
        foreach (var rename in Volatile.Read(ref _renameHistory))
            if (rename.Revision > _birthRevision) name = rename.Names.GetValueOrDefault(name, name);
        return name;
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void OnChanged(object sender, FileSystemEventArgs args) => Interlocked.Exchange(ref _lastChangeTicks, DateTime.UtcNow.Ticks);
    public void StartWatching() { _watching = true; if (_watcher != null) _watcher.EnableRaisingEvents = true; }
    public void StopWatching() { _watching = false; if (_watcher != null) _watcher.EnableRaisingEvents = false; }
    public ConfigurationApplicationStatus GetStatus()
    {
        var components = _components.ToDictionary(p => p.Key, p => p.Value);
        var state = _error != null || components.Values.Any(c => c.Error != null) ? "error"
            : components.Values.Any(c => c.AppliedRevision < Revision) ? "pending" : "applied";
        return new(Revision, state, components, _error);
    }
    public void Acknowledge(string component, long revision, string? error = null) =>
        _components.AddOrUpdate(component, new ConfigurationComponentStatus(revision, error), (_, previous) =>
            revision >= previous.AppliedRevision ? new(revision, error)
                : error != null ? previous with { Error = error } : previous);
    public async Task WaitForChangeAsync(long revision, TimeSpan delay, CancellationToken ct)
    {
        Task signal;
        lock (_gate) { if (Revision != revision) return; signal = _changed.Task; }
        try { await signal.WaitAsync(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, ct).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
    public (bool Ok, string? Error) Validate(TorrentarrConfig candidate)
    {
        var result = ConfigValidationHelper.ValidateAll(candidate);
        if (!result.Ok) return result;
        foreach (var validate in ValidateCandidate?.GetInvocationList().Cast<Func<TorrentarrConfig, string?>>() ?? [])
            if (validate(candidate) is { } error) return (false, error);
        return (true, null);
    }
    public bool Apply(TorrentarrConfig candidate, Dictionary<string, string>? renames = null)
    {
        lock (_gate)
        {
            var validation = Validate(candidate);
            if (!validation.Ok) { _error = validation.Error; return false; }
            foreach (var (name, client) in candidate.GetAllTorrentClients())
                if (_config.GetTorrentClient(name) is { } old && JsonConvert.SerializeObject(client) == JsonConvert.SerializeObject(old))
                    client.Maintenance.Armed = old.Maintenance.Armed;
            var json = JsonConvert.SerializeObject(candidate);
            if (_effectiveDigest == json && (renames == null || renames.Count == 0)) { _error = null; return true; }
            try
            {
                var revision = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Revision + 1);
                var history = _renameHistory.ToList();
                if (renames is { Count: > 0 }) history.Add(new(revision, new(renames, StringComparer.OrdinalIgnoreCase)));
                var snapshot = new ConfigurationSnapshot(revision, candidate, renames, history);
                if (!_worker)
                {
                    // The transport contains credentials, just like config.toml.
                    if (File.Exists(SnapshotPath) && !OperatingSystem.IsWindows()) File.SetUnixFileMode(SnapshotPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    ConfigMutationGuard.WriteAtomic(SnapshotPath, JsonConvert.SerializeObject(snapshot));
                }
                Publish(snapshot, json);
                return true;
            }
            catch (Exception ex) { Fail(ex); return false; }
        }
    }
    private void Publish(ConfigurationSnapshot snapshot, string json)
    {
        Volatile.Write(ref _renameHistory, snapshot.RenameHistory ?? []);
        _config.Apply(snapshot.Config, snapshot.Revision);
        Interlocked.Exchange(ref _revision, snapshot.Revision);
        _effectiveDigest = json;
        if (!_worker)
        {
            foreach (var key in _components.Keys.Where(k => k.StartsWith("worker:") && !snapshot.Config.ArrInstances.ContainsKey(k[7..]))) _components.TryRemove(key, out _);
            foreach (var (name, instance) in snapshot.Config.ArrInstances)
                if (instance.Managed && !string.IsNullOrWhiteSpace(instance.URI) && instance.URI != "CHANGE_ME") _components.TryAdd("worker:" + name, new(0));
        }
        _error = null;
        var signal = _changed;
        _changed = NewSignal();
        signal.TrySetResult();
        ConfigReloaded?.Invoke(this, new() { Success = true, Revision = Revision });
        Acknowledge("configuration", Revision);
    }
    public bool ReloadConfig()
    {
        lock (_gate)
        {
            try
            {
                if (_worker)
                {
                    var snapshot = JsonConvert.DeserializeObject<ConfigurationSnapshot>(File.ReadAllText(SnapshotPath)) ?? throw new InvalidDataException();
                    if (snapshot.Revision > Revision) Publish(snapshot, JsonConvert.SerializeObject(snapshot.Config));
                    return true;
                }
                TorrentarrConfig candidate;
                if (Revision == 0)
                {
                    using var migration = ConfigMutationGuard.EnterMutation(ConfigPath);
                    candidate = _loader.Load();
                }
                else
                {
                    using var reading = ConfigMutationGuard.EnterProcessing(ConfigPath);
                    candidate = _loader.Load(persistMigrations: false);
                }
                return Apply(candidate);
            }
            catch (ConfigurationBusyException) { return false; }
            catch (Exception ex) { Fail(ex); return false; }
        }
    }
    private void Fail(Exception ex)
    {
        // Parser diagnostics can contain credentials from source lines.
        var error = $"Configuration was not applied ({ex.GetType().Name}). Check the configuration file.";
        if (_error != error) _logger.LogWarning("{Error}", error);
        _error = error;
        ConfigReloaded?.Invoke(this, new() { Success = false, Revision = Revision, ErrorMessage = _error });
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_worker) Apply(_config);
        StartWatching();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var path = _worker ? SnapshotPath : ConfigPath;
                if (_worker || DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastChangeTicks) >= TimeSpan.TicksPerSecond)
                {
                    try
                    {
                        var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, stoppingToken)));
                        if (_worker) { if (_watching && digest != _digest && ReloadConfig()) _digest = digest; }
                        else if (digest != _observedDigest) { _observedDigest = digest; _observedAt = DateTime.UtcNow; }
                        else if (_watching && digest != _digest && DateTime.UtcNow - _observedAt >= TimeSpan.FromSeconds(1) && ReloadConfig()) _digest = digest;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(ex); }
                }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { StopWatching(); }
    }
    public override void Dispose() { if (_disposed) return; _disposed = true; StopWatching(); _watcher?.Dispose(); base.Dispose(); }
}
