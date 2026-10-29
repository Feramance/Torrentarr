using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database.Models;

namespace Torrentarr.Infrastructure.Services;

internal sealed record SonarrSearchPlan(
    SearchCandidate Trigger, string Command, List<SearchCandidate> Covered, int EligibleCount, int TotalCount);

internal static class SonarrSearchScope
{
    internal static List<SearchCandidate> Candidates(IEnumerable<EpisodeFilesModel> episodes, SearchConfig config,
        DateTime now, bool upgradesOnly = false)
    {
        var inventory = episodes.ToList();
        var specialsAllowed = SpecialSeries(inventory, config);
        var candidates = new List<SearchCandidate>();
        foreach (var episode in inventory)
        {
            if ((!config.Unmonitored && episode.Monitored != true) ||
                (episode.SeasonNumber == 0 && !specialsAllowed.Contains(episode.ArrSeriesId)))
                continue;

            string? reason = null;
            if (config.DoUpgradeSearch)
            {
                if (episode.HasFile && !episode.Upgrade) reason = "Upgrade";
            }
            else if (!episode.Searched)
            {
                if (!episode.HasFile && config.SearchMissing && !upgradesOnly &&
                    episode.Reason != "NotAvailable" && (episode.AirDateUtc == null || episode.AirDateUtc <= now)) reason = "Missing";
                else if (episode.HasFile && config.CustomFormatUnmetSearch && !episode.CustomFormatMet) reason = "CustomFormat";
                else if (episode.HasFile && config.QualityUnmetSearch && !episode.QualityMet) reason = "Quality";
            }
            if (reason == null) continue;

            candidates.Add(new SearchCandidate
            {
                ArrId = episode.ArrId,
                Title = $"{episode.SeriesTitle} S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}",
                Type = "Episode",
                Reason = reason,
                Priority = reason switch { "Missing" => 1, "CustomFormat" => 2, "Quality" => 3, _ => 4 },
                SeriesId = episode.ArrSeriesId > 0 ? episode.ArrSeriesId : null,
                SeasonNumber = episode.SeasonNumber,
                EpisodeNumber = episode.EpisodeNumber,
                AirDate = episode.AirDateUtc,
                Year = episode.AirDateUtc?.Year,
                IsTodaysRelease = config.PrioritizeTodaysReleases && episode.AirDateUtc >= now.AddHours(-25) && episode.AirDateUtc <= now.AddHours(-1)
            });
        }
        return candidates.DistinctBy(c => c.ArrId).ToList();
    }

    internal static List<SonarrSearchPlan> Plan(List<SearchCandidate> requested,
        List<EpisodeFilesModel> inventory, List<SeriesFilesModel> series, SearchConfig config, DateTime now)
    {
        var mode = config.SearchBySeries;
        var specialsAllowed = SpecialSeries(inventory, config);
        var needs = Candidates(inventory, config, now).ToDictionary(c => c.ArrId);
        var byId = inventory.DistinctBy(e => e.ArrId).ToDictionary(e => e.ArrId);
        var retries = requested.Where(c => c.Reason == "Retry").ToList();
        foreach (var retry in retries)
        {
            if (byId.TryGetValue(retry.ArrId, out var episode))
            {
                retry.SeriesId = episode.ArrSeriesId > 0 ? episode.ArrSeriesId : null;
                retry.SeasonNumber = episode.SeasonNumber;
                retry.EpisodeNumber = episode.EpisodeNumber;
            }
            needs.TryAdd(retry.ArrId, retry);
        }
        var plans = new List<SonarrSearchPlan>();
        var coveredIds = new HashSet<int>();
        var population = inventory.Where(e => e.Monitored == true &&
            (e.SeasonNumber != 0 || specialsAllowed.Contains(e.ArrSeriesId)) && e.AirDateUtc <= now).ToList();
        var monitoredSeries = series.Where(s => s.Monitored == true).Select(s => s.ArrId).ToHashSet();
        var populationIds = population.Select(e => e.ArrId).ToHashSet();
        var seriesPopulation = population.GroupBy(e => e.ArrSeriesId).ToDictionary(g => g.Key, g => g.ToList());
        var seriesNeeds = population.Where(e => needs.ContainsKey(e.ArrId)).GroupBy(e => e.ArrSeriesId)
            .ToDictionary(g => g.Key, g => g.Select(e => needs[e.ArrId]).ToList());
        var seasonPopulation = population.GroupBy(e => (e.ArrSeriesId, e.SeasonNumber)).ToDictionary(g => g.Key, g => g.ToList());
        var seasonNeeds = population.Where(e => needs.ContainsKey(e.ArrId)).GroupBy(e => (e.ArrSeriesId, e.SeasonNumber))
            .ToDictionary(g => g.Key, g => g.Select(e => needs[e.ArrId]).ToList());

        foreach (var candidate in requested.DistinctBy(c => c.ArrId))
        {
            if (coveredIds.Contains(candidate.ArrId)) continue;
            if (candidate.SeasonNumber == 0 && (!candidate.SeriesId.HasValue || !specialsAllowed.Contains(candidate.SeriesId.Value))) continue;
            // A previous pass may already have covered this episode as part of a broader search.
            if (mode == "smart" && byId.ContainsKey(candidate.ArrId) && !needs.ContainsKey(candidate.ArrId)) continue;
            var command = "EpisodeSearch";
            var covered = new List<SearchCandidate> { needs.GetValueOrDefault(candidate.ArrId, candidate) };
            var total = 1;
            var eligible = 1;
            var knownMonitored = byId.TryGetValue(candidate.ArrId, out var known) && known.Monitored == true;
            if (candidate.SeriesId is > 0 && mode == "series" && knownMonitored && populationIds.Contains(candidate.ArrId))
            {
                command = "SeriesSearch";
                covered = needs.Values.Where(c => c.SeriesId == candidate.SeriesId && populationIds.Contains(c.ArrId))
                    .Concat(requested.Where(c => c.SeriesId == candidate.SeriesId && populationIds.Contains(c.ArrId))).ToList();
                eligible = covered.DistinctBy(c => c.ArrId).Count();
                total = seriesPopulation.GetValueOrDefault(candidate.SeriesId.Value, []).Count;
            }
            else if (mode == "season" && candidate.SeriesId is > 0 && candidate.SeasonNumber.HasValue &&
                (config.Unmonitored || candidate.SeasonNumber != 0) && knownMonitored && populationIds.Contains(candidate.ArrId))
            {
                command = "SeasonSearch";
                var seasonKey = (candidate.SeriesId.Value, candidate.SeasonNumber.Value);
                covered = seasonNeeds.GetValueOrDefault(seasonKey, [candidate]);
                eligible = covered.Count;
                total = seasonPopulation.GetValueOrDefault(seasonKey, []).Count;
            }
            else if (mode == "smart" && candidate.SeriesId is > 0 &&
                byId.TryGetValue(candidate.ArrId, out var current) && current.Monitored == true &&
                populationIds.Contains(candidate.ArrId) && monitoredSeries.Contains(candidate.SeriesId.Value))
            {
                var allSeriesNeeds = seriesNeeds.GetValueOrDefault(candidate.SeriesId.Value, []);
                total = seriesPopulation.GetValueOrDefault(candidate.SeriesId.Value, []).Count;
                eligible = allSeriesNeeds.Count;
                if (eligible >= 2 && eligible * 2 > total)
                {
                    command = "SeriesSearch";
                    covered = allSeriesNeeds;
                }
                else if (candidate.SeasonNumber.HasValue)
                {
                    var seasonKey = (candidate.SeriesId.Value, candidate.SeasonNumber.Value);
                    var allSeasonNeeds = seasonNeeds.GetValueOrDefault(seasonKey, []);
                    total = seasonPopulation.GetValueOrDefault(seasonKey, []).Count;
                    eligible = allSeasonNeeds.Count;
                    if (eligible >= 2 && eligible * 2 > total && (config.Unmonitored || candidate.SeasonNumber != 0))
                    {
                        command = "SeasonSearch";
                        covered = allSeasonNeeds;
                    }
                }
            }
            covered = covered.DistinctBy(c => c.ArrId).ToList();
            plans.Add(new SonarrSearchPlan(candidate, command, covered, eligible, total));
            coveredIds.UnionWith(covered.Select(c => c.ArrId));
        }

        // Queue retries already use one EpisodeSearch payload for all remaining affected IDs.
        if (retries.Count == requested.Count && retries.Count > 0)
        {
            var episodePlans = plans.Where(p => p.Command == "EpisodeSearch").ToList();
            if (episodePlans.Count > 1)
            {
                plans.RemoveAll(p => p.Command == "EpisodeSearch");
                plans.Add(episodePlans[0] with { Covered = episodePlans.SelectMany(p => p.Covered).ToList() });
            }
        }
        return plans;
    }

    internal static HashSet<int> SpecialSeries(IEnumerable<EpisodeFilesModel> inventory, SearchConfig config) =>
        config.AlsoSearchSpecials
            ? inventory.Where(e => e.ArrSeriesId > 0 && e.SeasonNumber == 0 && e.Monitored == true)
                .Select(e => e.ArrSeriesId).ToHashSet()
            : [];
}
