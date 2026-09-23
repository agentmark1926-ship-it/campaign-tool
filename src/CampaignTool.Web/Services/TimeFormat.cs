namespace CampaignTool.Web.Services;

/// <summary>Storage is UTC; display is in App:TimeZone.</summary>
public static class TimeFormat
{
    public static string Local(DateTime utc, string timeZone, string format = "MMM d, yyyy h:mm tt") =>
        TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var tz)
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz).ToString(format)
            : utc.ToString(format) + " UTC";

    public static string Local(DateTime? utc, string timeZone, string format = "MMM d, yyyy h:mm tt") =>
        utc is null ? "" : Local(utc.Value, timeZone, format);
}
