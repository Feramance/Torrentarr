namespace Torrentarr.Core.Services;

/// <summary>Small five-field cron evaluator shared by scheduled host services.</summary>
public static class CronSchedule
{
    public static bool Matches(string expression, DateTime utcNow)
    {
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5
            && MatchField(parts[0], utcNow.Minute, 0, 59)
            && MatchField(parts[1], utcNow.Hour, 0, 23)
            && MatchField(parts[2], utcNow.Day, 1, 31)
            && MatchField(parts[3], utcNow.Month, 1, 12)
            && MatchDayOfWeek(parts[4], (int)utcNow.DayOfWeek);
    }

    public static DateTimeOffset? Next(string expression, DateTimeOffset fromUtc)
    {
        var candidate = new DateTimeOffset(fromUtc.UtcDateTime.AddMinutes(1), TimeSpan.Zero);
        candidate = candidate.AddSeconds(-candidate.Second).AddMilliseconds(-candidate.Millisecond);
        // A one-year horizon rejects valid sparse schedules such as Feb 29.
        // Five years covers the full leap-year cycle while keeping lookups bounded.
        for (var i = 0; i < 5 * 366 * 24 * 60; i++, candidate = candidate.AddMinutes(1))
            if (Matches(expression, candidate.UtcDateTime)) return candidate;
        return null;
    }

    private static bool MatchField(string field, int value, int minimum, int maximum)
        => field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => MatchPart(part, value, minimum, maximum));

    private static bool MatchDayOfWeek(string field, int value)
        => MatchField(field, value, 0, 7) || (value == 0 && MatchField(field, 7, 0, 7));

    private static bool MatchPart(string part, int value, int minimum, int maximum)
    {
        var pieces = part.Split('/', 2);
        if (pieces.Length == 2 && (!int.TryParse(pieces[1], out var step) || step <= 0)) return false;
        var range = pieces[0];
        var stepValue = pieces.Length == 2 ? int.Parse(pieces[1]) : 1;
        int start;
        int end;
        if (range == "*") { start = minimum; end = maximum; }
        else if (range.Contains('-'))
        {
            var bounds = range.Split('-', 2);
            if (!int.TryParse(bounds[0], out start) || !int.TryParse(bounds[1], out end)) return false;
        }
        else
        {
            if (!int.TryParse(range, out start)) return false;
            end = start;
        }
        return value >= start && value <= end && (value - start) % stepValue == 0;
    }
}
