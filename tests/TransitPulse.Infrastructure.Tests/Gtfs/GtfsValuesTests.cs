using TransitPulse.Infrastructure.Gtfs;

namespace TransitPulse.Infrastructure.Tests.Gtfs;

public sealed class GtfsValuesTests
{
    [Theory]
    [InlineData("05:37:00", 0, 5, 37, 0)]
    [InlineData("5:37:00", 0, 5, 37, 0)]    // a single hour digit is allowed below 10
    [InlineData("00:00:00", 0, 0, 0, 0)]
    [InlineData("23:59:59", 0, 23, 59, 59)]
    [InlineData("25:10:00", 1, 1, 10, 0)]   // after midnight, still the evening's service day
    [InlineData("100:00:00", 4, 4, 0, 0)]
    public void Parses_times_including_hours_past_midnight(string text, int days, int hours, int minutes, int seconds)
    {
        Assert.True(GtfsValues.TryParseTime(text, out var time));
        Assert.Equal(new TimeSpan(days, hours, minutes, seconds), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("05:37")]
    [InlineData("5:7:00")]
    [InlineData("05:60:00")]
    [InlineData("05:37:60")]
    [InlineData("-1:00:00")]
    [InlineData("+5:00:00")]
    [InlineData("1000:00:00")]
    [InlineData("05.37.00")]
    [InlineData(" 05:37:00")]
    public void Rejects_text_that_is_not_a_gtfs_time(string text)
    {
        Assert.False(GtfsValues.TryParseTime(text, out _));
    }

    [Fact]
    public void Parses_dates_without_separators()
    {
        Assert.True(GtfsValues.TryParseDate("20261102", out var date));
        Assert.Equal(new DateOnly(2026, 11, 2), date);
    }

    [Theory]
    [InlineData("2026-11-02")]
    [InlineData("20260230")]
    [InlineData("2026110")]
    [InlineData("")]
    public void Rejects_text_that_is_not_a_gtfs_date(string text)
    {
        Assert.False(GtfsValues.TryParseDate(text, out _));
    }
}
