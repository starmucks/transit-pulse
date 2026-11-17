namespace TransitPulse.Domain.Tests;

public sealed class StopTimetableTests
{
    [Fact]
    public void Finds_calls_by_non_consecutive_stop_sequence()
    {
        var timetable = new StopTimetable([new(2, 3600, 3610), new(7, 3700, 3710)]);

        Assert.True(timetable.TryFind(7, out var found));
        Assert.Equal(new ScheduledStopTime(7, 3700, 3710), found);
        Assert.False(timetable.TryFind(3, out _));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    public void Rejects_calls_out_of_sequence_order(int first, int second)
    {
        Assert.Throws<ArgumentException>(() => new StopTimetable([new(first, null, null), new(second, null, null)]));
    }

    [Fact]
    public void Rejects_negative_sequence_and_times()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduledStopTime(-1, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduledStopTime(1, -1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduledStopTime(1, null, -1));
    }
}
