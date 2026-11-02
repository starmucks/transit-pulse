using System.Globalization;

namespace TransitPulse.Infrastructure.Gtfs;

/// <summary>Parses the value formats GTFS defines on top of plain text and numbers.</summary>
/// <remarks>Spec: https://gtfs.org/documentation/schedule/reference/#field-types</remarks>
public static class GtfsValues
{
    /// <summary>
    /// Parses a GTFS time <c>H:MM:SS</c> or <c>HH:MM:SS</c>, measured from "noon minus 12 h" of the service day.
    /// Hours may exceed 23: a trip that starts in the evening keeps the same service day after midnight (<c>25:10:00</c>).
    /// </summary>
    /// <param name="text">The field, without surrounding spaces.</param>
    /// <param name="time">Time since the start of the service day.</param>
    /// <returns><see langword="false"/> when the text is not a GTFS time.</returns>
    public static bool TryParseTime(ReadOnlySpan<char> text, out TimeSpan time)
    {
        time = default;
        var hoursLength = text.IndexOf(':');

        // Three hour digits allow trips of multi-day services; minutes and seconds are always two digits.
        if (hoursLength is < 1 or > 3 || text.Length != hoursLength + 6 || text[hoursLength + 3] != ':')
        {
            return false;
        }

        if (!TryParseDigits(text[..hoursLength], out var hours)
            || !TryParseDigits(text.Slice(hoursLength + 1, 2), out var minutes)
            || !TryParseDigits(text.Slice(hoursLength + 4, 2), out var seconds)
            || minutes > 59
            || seconds > 59)
        {
            return false;
        }

        time = new TimeSpan(hours, minutes, seconds);
        return true;
    }

    /// <summary>Parses a GTFS date <c>YYYYMMDD</c>.</summary>
    /// <param name="text">The field, without surrounding spaces.</param>
    /// <param name="date">The calendar date.</param>
    /// <returns><see langword="false"/> when the text is not a valid date in that format.</returns>
    public static bool TryParseDate(ReadOnlySpan<char> text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    // int.TryParse would also accept a sign and surrounding spaces, which GTFS times never have.
    private static bool TryParseDigits(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        return true;
    }
}
