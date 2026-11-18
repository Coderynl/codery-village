namespace Codery.Village.Core.Time;

public sealed record CalendarMoment(DateOnly Date, TimeOnly Time, DayOfWeek Day, bool Weekend);

/// <summary>The calendar in the Netherlands. Follows the given time provider, so downtime moves it.</summary>
public sealed class Calendar(TimeProvider time)
{
    private static readonly TimeZoneInfo Zone = FindZone();

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone);

    public CalendarMoment Moment
    {
        get
        {
            var now = Now;
            var day = now.DayOfWeek;
            return new CalendarMoment(
                DateOnly.FromDateTime(now.DateTime),
                TimeOnly.FromDateTime(now.DateTime),
                day,
                day is DayOfWeek.Saturday or DayOfWeek.Sunday);
        }
    }

    private static TimeZoneInfo FindZone()
    {
        // Linux uses the IANA id; Windows uses its own name for the same zone.
        if (TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Amsterdam", out var zone)) return zone;
        if (TimeZoneInfo.TryFindSystemTimeZoneById("W. Europe Standard Time", out zone)) return zone;
        throw new InvalidOperationException("The Dutch time zone is not available on this system.");
    }
}
