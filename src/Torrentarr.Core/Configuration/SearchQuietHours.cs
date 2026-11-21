using System.Globalization;

namespace Torrentarr.Core.Configuration;

public static class SearchQuietHours
{
    public static string? Validate(SearchConfig settings)
    {
        if (!TimeOnly.TryParseExact(settings.QuietHoursStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(settings.QuietHoursEnd, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            return "Quiet hours require start and end in HH:mm format.";
        if (start == end) return "Quiet hours start and end must differ.";
        if (string.IsNullOrWhiteSpace(settings.QuietHoursTimeZone)
            || !TimeZoneInfo.TryConvertIanaIdToWindowsId(settings.QuietHoursTimeZone, out _))
            return "Quiet hours require an explicit IANA timezone, for example Europe/Malta or Etc/UTC.";
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(settings.QuietHoursTimeZone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { return "The quiet hours timezone is unavailable on this system."; }
        return null;
    }

    public static DateTimeOffset? NextEligible(SearchConfig settings, DateTimeOffset now)
    {
        if (!settings.QuietHoursEnabled) return null;
        if (Validate(settings) is { } error) throw new InvalidDataException(error);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.QuietHoursTimeZone);
        var start = TimeOnly.ParseExact(settings.QuietHoursStart, "HH:mm", CultureInfo.InvariantCulture);
        var end = TimeOnly.ParseExact(settings.QuietHoursEnd, "HH:mm", CultureInfo.InvariantCulture);
        bool Quiet(DateTimeOffset instant)
        {
            var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
            return start < end ? local >= start && local < end : local >= start || local < end;
        }
        if (!Quiet(now)) return null;
        // Walk real instants so repeated and nonexistent wall times need no special scheduling rules.
        var next = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 60 * 60).AddMinutes(1);
        for (var minutes = 0; minutes < 48 * 60; minutes++, next = next.AddMinutes(1))
            if (!Quiet(next)) return next;
        throw new InvalidDataException("No quiet hours boundary was observed within two days.");
    }
}
