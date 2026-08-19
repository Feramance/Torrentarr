using System.Collections.Concurrent;
using Torrentarr.Core.Configuration;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Per-Arr-instance year cursor for <c>SearchByYear</c> (qBitrr <c>search_current_year</c>).
/// Filters missing/upgrade search to one library year at a time, then advances when that year drains.
/// </summary>
public sealed class SearchYearCursor
{
    private readonly ConcurrentDictionary<string, CursorState> _states = new(StringComparer.OrdinalIgnoreCase);

    private sealed class CursorState
    {
        public List<int> Years { get; set; } = [];
        public int Index { get; set; }
    }

    /// <summary>
    /// Returns the year this instance should search now, or null when year filtering is off / no years exist.
    /// </summary>
    public int? CurrentYear(string instanceName, ArrInstanceConfig arrConfig, IReadOnlyList<int> years)
    {
        if (!ShouldFilter(arrConfig) || years.Count == 0)
            return null;

        var ordered = OrderYears(years, arrConfig.Search.SearchInReverse);
        var state = _states.GetOrAdd(instanceName, _ => new CursorState());

        if (!state.Years.SequenceEqual(ordered))
        {
            var previous = state.Index >= 0 && state.Index < state.Years.Count
                ? state.Years[state.Index]
                : (int?)null;
            state.Years = ordered;
            var idx = previous is int y ? ordered.IndexOf(y) : 0;
            state.Index = idx >= 0 ? idx : 0;
        }

        if (state.Index < 0 || state.Index >= state.Years.Count)
            state.Index = 0;

        return state.Years[state.Index];
    }

    /// <summary>
    /// Advance to the next year after the current year drained.
    /// Returns true when another year remains (overall search loop is not complete).
    /// </summary>
    public bool Advance(string instanceName)
    {
        if (!_states.TryGetValue(instanceName, out var state) || state.Years.Count == 0)
            return false;

        if (state.Index >= state.Years.Count - 1)
        {
            state.Index = 0;
            return false;
        }

        state.Index++;
        return true;
    }

    public void Reset(string instanceName) => _states.TryRemove(instanceName, out _);

    public static bool ShouldFilter(ArrInstanceConfig arrConfig) =>
        arrConfig.Search.SearchByYear && ArrSectionHelper.SupportsSearchByYear(arrConfig.Type);

    public static List<int> OrderYears(IEnumerable<int> years, bool searchInReverse) =>
        searchInReverse
            ? years.Distinct().OrderByDescending(y => y).ToList()
            : years.Distinct().OrderBy(y => y).ToList();
}
