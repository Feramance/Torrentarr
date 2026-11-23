using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public sealed class SharedFileFilterService(
    TorrentarrConfig config, IOperationalIssueReporter issues, ILogger<SharedFileFilterService> logger) : BackgroundService
{
    public sealed record ListState(string Name, string Source, DateTimeOffset? LoadedAt, DateTimeOffset CheckedAt,
        string? Error, Regex[] Sensitive, Regex[] Insensitive);
    private IReadOnlyDictionary<string, ListState> _states = new Dictionary<string, ListState>();

    public IReadOnlyList<ListState> Status => Volatile.Read(ref _states).Values.ToArray();

    public sealed record View(bool Available, bool CaseSensitive, IReadOnlyList<ListState> Lists)
    {
        public string? Match(string path)
        {
            if (!Available) throw new InvalidOperationException("Shared file filter list is unavailable.");
            if (path.Length > SharedFileFilterSource.MaxPathLength)
                throw new FormatException("File path exceeds shared filter limit.");
            path = path.Replace('\\', '/');
            foreach (var state in Lists)
            {
                var patterns = CaseSensitive ? state.Sensitive : state.Insensitive;
                if (patterns.Any(pattern => pattern.IsMatch(path)))
                    return $"Shared list '{state.Name}' matched.";
            }
            return null;
        }
    }

    public View Capture(TorrentConfig torrent)
    {
        var states = Volatile.Read(ref _states);
        if (torrent.SharedFileFilters.Count > 32) return new(false, torrent.CaseSensitiveMatches, []);
        var lists = new List<ListState>();
        foreach (var name in torrent.SharedFileFilters)
        {
            if (!TryGet(name, states, out var state))
                return new(false, torrent.CaseSensitiveMatches, []);
            lists.Add(state);
        }
        return new(true, torrent.CaseSensitiveMatches, lists);
    }

    public bool Available(TorrentConfig torrent) => Capture(torrent).Available;
    public string? Match(string path, TorrentConfig torrent) => Capture(torrent).Match(path);

    private bool TryGet(string name, IReadOnlyDictionary<string, ListState> states, out ListState state)
    {
        state = null!;
        var definitions = config.Settings.SharedFileFilters.Where(item => item.Name == name).ToArray();
        return definitions.Length == 1 && states.TryGetValue(name, out state!) && state.LoadedAt != null
            && state.Source == definitions[0].Source;
    }

    internal async Task RefreshAsync(CancellationToken ct, string? directory = null)
    {
        using var operation = config.BeginOperation();
        var previous = Volatile.Read(ref _states);
        var next = new Dictionary<string, ListState>(StringComparer.Ordinal);
        var definitions = config.Settings.SharedFileFilters;
        var names = definitions.Select(item => item.Name)
            .Concat(config.ArrInstances.Values.SelectMany(arr => arr.Torrent.SharedFileFilters)).Distinct().Take(32).ToArray();
        foreach (var name in names)
        {
            var candidates = definitions.Where(item => item.Name == name).ToArray();
            var source = candidates.Length == 1 ? candidates[0].Source : "";
            previous.TryGetValue(name, out var last);
            if (last?.Source != source) last = null;
            string? error = null;
            Regex[] sensitive = [], insensitive = [];
            DateTimeOffset? loaded = last?.LoadedAt;
            try
            {
                if (candidates.Length != 1 || string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
                    throw new FormatException("List name must be unique, nonempty, and at most 80 printable characters.");
                var content = await SharedFileFilterSource.ReadAsync(source, directory ?? ConfigurationLoader.GetDataDirectoryPath(), ct);
                sensitive = SharedFileFilterSource.Compile(content, true);
                insensitive = SharedFileFilterSource.Compile(content, false);
                loaded = DateTimeOffset.UtcNow;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Source paths and network exception text can contain credentials or local details.
                error = ex is FormatException ? ex.Message : "List could not be read. Check its source, reachability, and encoding.";
                sensitive = last?.Sensitive ?? [];
                insensitive = last?.Insensitive ?? [];
                logger.LogWarning("Shared file filter refresh failed for {ListName}: {Reason}", DiagnosticText.Safe(name), error);
            }
            next[name] = new(name, source, loaded, DateTimeOffset.UtcNow, error, sensitive, insensitive);
        }

        Volatile.Write(ref _states, next);
        foreach (var state in next.Values)
        {
            var issue = new OperationalIssueReport("shared-file-filter", "system", "Shared file filter unavailable",
                state.LoadedAt == null ? "Torrent processing and import wait for a usable shared list."
                    : "Filtering uses the last valid version while refresh is failing.",
                "Review the list source and format in general settings.", SubjectId: state.Name, Target: "config");
            if (state.Error != null) await issues.ReportAsync(issue.WithEvidence(state.Error), ct);
            else await issues.ResolveAsync(issue, ct);
        }
        foreach (var removed in previous.Keys.Except(next.Keys))
            await issues.ResolveAsync(new("shared-file-filter", "system", "Shared file filter unavailable", "", "", SubjectId: removed), ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Shared file filter refresh could not complete");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}
