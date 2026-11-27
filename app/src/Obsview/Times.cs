using System.Globalization;

namespace Obsview;

/// <summary>Timestamps from Log Analytics rows, and how the pages write them.</summary>
public static class Times
{
    /// <summary>
    /// A UTC time from a row's value, or null if it isn't one. The SDK gives
    /// <see cref="DateTimeOffset"/>s; the demo and tests sometimes pass ISO
    /// strings. A malformed string is null, so one bad row is skipped rather
    /// than turning the whole page into a 500. A time with no offset is UTC.
    /// </summary>
    public static DateTimeOffset? Parse(object? value) => value switch
    {
        DateTimeOffset at => at.ToUniversalTime(),
        DateTime at => new DateTimeOffset(DateTime.SpecifyKind(at, at.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : at.Kind)).ToUniversalTime(),
        string text => ParseText(text),
        _ => null,
    };

    private static DateTimeOffset? ParseText(string text)
    {
        var style = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, style, out var when) ? when : null;
    }

    /// <summary>The UTC calendar date of a time.</summary>
    public static DateOnly Day(DateTimeOffset when) => DateOnly.FromDateTime(when.UtcDateTime);

    /// <summary>A UTC date's first instant.</summary>
    public static DateTimeOffset Midnight(DateOnly day) =>
        new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>
    /// ISO 8601 that every browser's <c>Date</c> parses: milliseconds and
    /// <c>Z</c>. Anything finer, or an offset spelled <c>+00:00</c>, is
    /// outside ECMAScript's date-time format, and a strict engine returns
    /// Invalid Date.
    /// </summary>
    public static string JsTime(DateTimeOffset when) =>
        when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary><c>yyyy-MM-dd</c>, as a chart's category axis reads it.</summary>
    public static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A full timestamp for a <c>title</c> attribute, to the second, in UTC.</summary>
    public static string IsoStamp(DateTimeOffset when) =>
        when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);

    /// <summary>"3 Nov": a day of the month with no leading zero, and the month's short name.</summary>
    public static string DayMonth(DateOnly day) => day.ToString("d MMM", CultureInfo.InvariantCulture);
}
