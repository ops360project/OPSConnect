namespace OpsConnect.Application.Common;

/// <summary>
/// Provides UTC and business time-zone (Asia/Kolkata by default) date/time conversions.
/// </summary>
public interface IBusinessClock
{
    DateTime UtcNow { get; }
    DateOnly GetBusinessDate(string timeZoneId = "Asia/Kolkata");
    DateTimeOffset ConvertUtcToTimeZone(DateTime utcDateTime, string timeZoneId = "Asia/Kolkata");
}

public sealed class SystemBusinessClock : IBusinessClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public DateOnly GetBusinessDate(string timeZoneId = "Asia/Kolkata")
    {
        var local = ConvertUtcToTimeZone(UtcNow, timeZoneId);
        return DateOnly.FromDateTime(local.DateTime);
    }

    public DateTimeOffset ConvertUtcToTimeZone(DateTime utcDateTime, string timeZoneId = "Asia/Kolkata")
    {
        var normalizedUtc = utcDateTime.Kind == DateTimeKind.Utc
            ? utcDateTime
            : DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);

        var tz = ResolveTimeZone(timeZoneId);
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(normalizedUtc, TimeSpan.Zero), tz);
    }

    public static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException) when (string.Equals(timeZoneId, "Asia/Kolkata", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
    }
}
