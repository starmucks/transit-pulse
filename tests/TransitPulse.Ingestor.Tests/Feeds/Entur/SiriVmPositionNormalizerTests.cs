using System.Text;
using System.Text.RegularExpressions;
using TransitPulse.Domain;
using TransitPulse.Ingestor.Feeds.Entur;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;
using TransitPulse.Ingestor.Tests.Normalization;

namespace TransitPulse.Ingestor.Tests.Feeds.Entur;

public sealed class SiriVmPositionNormalizerTests
{
    // A real record (bus 30, Bergen, 2026-10-23); each test changes only the elements it is about.
    private const string Sample = """
        <VehicleActivity><RecordedAtTime>2026-10-23T08:59:50+02:00</RecordedAtTime><MonitoredVehicleJourney>
        <LineRef>SKY:Line:30</LineRef><FramedVehicleJourneyRef><DataFrameRef>2026-10-23</DataFrameRef>
        <DatedVehicleJourneyRef>SKY:ServiceJourney:30-208199-20940214</DatedVehicleJourneyRef></FramedVehicleJourneyRef>
        <VehicleMode>bus</VehicleMode><Monitored>true</Monitored><InCongestion>false</InCongestion>
        <VehicleLocation><Longitude>5.30651173554361</Longitude><Latitude>60.4569782037288</Latitude></VehicleLocation>
        <Bearing>271.0</Bearing><Occupancy>seatsAvailable</Occupancy><Delay>-PT10S</Delay><VehicleStatus>inProgress</VehicleStatus>
        <VehicleRef>3350447527</VehicleRef></MonitoredVehicleJourney></VehicleActivity>
        """;

    private static readonly DateTimeOffset _recordedAt = new(2026, 10, 23, 6, 59, 50, TimeSpan.Zero);

    // Received 10 s after the measurement, the measured median.
    private static readonly DateTimeOffset _receivedAt = _recordedAt.AddSeconds(10);

    /// <summary>The sample with one element's text replaced, or the element removed when <paramref name="value"/> is null.</summary>
    private static string With(string element, string? value, string activity = Sample) =>
        Regex.Replace(activity, $"<{element}>[^<]*</{element}>", value is null ? "" : $"<{element}>{value}</{element}>");

    private static IReadOnlyList<VehiclePosition> Normalize(DateTimeOffset receivedAt, params string[] activities) =>
        Normalize(new DropLog(), receivedAt, activities);

    private static IReadOnlyList<VehiclePosition> Normalize(DropLog drops, DateTimeOffset receivedAt, params string[] activities) =>
        new SiriVmPositionNormalizer().Normalize(
            new RawRecord("entur-sky-vm", receivedAt, Encoding.UTF8.GetBytes(SiriVmSamples.Response(activities)), Topic: null), drops);

    private static VehiclePosition Position(string activity) => Assert.Single(Normalize(_receivedAt, activity));

    [Fact]
    public void Real_record_maps_every_field()
    {
        Assert.Equal(new VehiclePosition(
            "entur-sky", "3350447527", "SKY:ServiceJourney:30-208199-20940214", "SKY:Line:30", 60.4569782037288,
            5.30651173554361, null, 271f, -10, OccupancyLevel.ManySeatsAvailable, _recordedAt), Position(Sample));
    }

    [Theory]
    [InlineData("PT5M43S", 343)]
    [InlineData("-PT1H2M3S", -3723)]
    [InlineData("PT2H", 7200)]
    [InlineData("-PT2H", -7200)]
    [InlineData("PT30.6S", 31)]
    [InlineData("PT30.5S", 31)]
    [InlineData("-PT30.5S", -31)]
    [InlineData("PT0S", 0)]
    public void Delay_is_seconds_positive_when_late(string delay, int expected)
    {
        Assert.Equal(expected, Position(With("Delay", delay)).DelaySec);
    }

    [Theory]
    [InlineData("PT2H0M1S")]
    [InlineData("-PT2H52M12S")]
    [InlineData("P1D")]
    [InlineData("P99999999D")]
    // Parses to exactly TimeSpan.MinValue, whose Duration() throws.
    [InlineData("-P10675199DT2H48M5.4775808S")]
    [InlineData("5 minutes")]
    [InlineData("")]
    [InlineData(null)]
    public void Implausible_or_unreadable_delay_is_dropped_but_the_position_is_kept(string? delay)
    {
        Assert.Null(Position(With("Delay", delay)).DelaySec);
    }

    [Fact]
    public void Vehicle_only_assigned_to_a_journey_has_no_delay()
    {
        var assigned = With("VehicleStatus", "assigned", With("Delay", "-PT45M"));

        Assert.Null(Position(assigned).DelaySec);
    }

    [Theory]
    [InlineData("-PT1H39M48S", null)]
    [InlineData("-PT10S", null)]
    [InlineData("PT0S", 0)]
    [InlineData("PT3M", 180)]
    public void Vehicle_waiting_at_the_origin_keeps_only_a_late_departure(string delay, int? expected)
    {
        var atOrigin = With("VehicleStatus", "atOrigin", With("Delay", delay));

        Assert.Equal(expected, Position(atOrigin).DelaySec);
    }

    [Theory]
    [InlineData("empty", OccupancyLevel.Empty)]
    [InlineData("manySeatsAvailable", OccupancyLevel.ManySeatsAvailable)]
    [InlineData("seatsAvailable", OccupancyLevel.ManySeatsAvailable)]
    [InlineData("fewSeatsAvailable", OccupancyLevel.FewSeatsAvailable)]
    [InlineData("standingAvailable", OccupancyLevel.StandingRoomOnly)]
    [InlineData("standingRoomOnly", OccupancyLevel.StandingRoomOnly)]
    [InlineData("crushedStandingRoomOnly", OccupancyLevel.CrushedStandingRoomOnly)]
    [InlineData("full", OccupancyLevel.Full)]
    [InlineData("notAcceptingPassengers", OccupancyLevel.NotAcceptingPassengers)]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void Occupancy_maps_to_the_gtfs_levels(string? occupancy, OccupancyLevel? expected)
    {
        Assert.Equal(expected, Position(With("Occupancy", occupancy)).Occupancy);
    }

    [Theory]
    [InlineData("2026-10-23T08:59:50.776+02:00", "2026-10-23T06:59:50.776Z")]
    [InlineData("2026-10-27T05:02:35+01:00", "2026-10-27T04:02:35Z")]
    // Entur's ResponseTimestamp already has nine digits; anything past the seventh is dropped.
    [InlineData("2026-10-23T08:59:56.552794536+02:00", "2026-10-23T06:59:56.5527945Z")]
    [InlineData("2026-10-23T06:59:50Z", "2026-10-23T06:59:50Z")]
    public void Recorded_time_becomes_utc(string recordedAt, string expectedUtc)
    {
        var expected = DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture);

        var position = Assert.Single(Normalize(expected.AddSeconds(10), With("RecordedAtTime", recordedAt)));

        Assert.Equal(expected, position.ObservedAt);
        Assert.Equal(TimeSpan.Zero, position.ObservedAt.Offset);
    }

    [Fact]
    public void Record_older_than_the_limit_is_dropped_and_one_at_the_limit_kept()
    {
        var limit = _recordedAt + SiriVmPositionNormalizer.MaxRecordAge;
        var drops = new DropLog();

        Assert.Single(Normalize(drops, limit, Sample));
        Assert.Empty(Normalize(drops, limit.AddSeconds(1), Sample));
        Assert.Equal([PositionMetrics.Stale], drops.Reasons);
    }

    [Theory]
    [InlineData("VehicleRef", null)]
    [InlineData("VehicleRef", " ")]
    [InlineData("RecordedAtTime", null)]
    [InlineData("RecordedAtTime", "2026-10-23T08:59:50")]
    [InlineData("RecordedAtTime", "2026-10-23T08:59:50.5")]
    [InlineData("RecordedAtTime", "2026-10-23T08:59:50+02")]
    [InlineData("RecordedAtTime", "yesterday at 10:00")]
    // A time without a date would get the date of the day the code runs.
    [InlineData("RecordedAtTime", "08:59:50+02:00")]
    [InlineData("RecordedAtTime", "2026-10-23+02:00")]
    [InlineData("RecordedAtTime", "2026-10-23 08:59:50+02:00")]
    [InlineData("Latitude", null)]
    [InlineData("Latitude", "NaN")]
    [InlineData("Latitude", "91")]
    [InlineData("Longitude", "-180.5")]
    [InlineData("Longitude", "5,3")]
    public void Unusable_record_skips_only_its_vehicle(string element, string? value)
    {
        var other = With("VehicleRef", "3350447528");
        var drops = new DropLog();

        var position = Assert.Single(Normalize(drops, _receivedAt, With(element, value), other));

        Assert.Equal("3350447528", position.VehicleId);
        Assert.Equal(
            [element is "Latitude" or "Longitude" ? PositionMetrics.BadLocation : PositionMetrics.Incomplete],
            drops.Reasons);
    }

    [Theory]
    [InlineData("360")]
    [InlineData("359.99999999")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("north")]
    [InlineData(null)]
    public void Unusable_bearing_is_null(string? bearing)
    {
        Assert.Null(Position(With("Bearing", bearing)).Bearing);
    }

    [Fact]
    public void Blank_journey_and_line_are_null()
    {
        var position = Position(With("LineRef", "", With("DatedVehicleJourneyRef", " ")));

        Assert.Equal((null, null), (position.TripId, position.RouteId));
    }

    [Fact]
    public void Malformed_response_throws_format_exception()
    {
        var truncated = Encoding.UTF8.GetBytes(SiriVmSamples.Response(Sample, Sample)[..^300]);

        Assert.Throws<FormatException>(() =>
            new SiriVmPositionNormalizer().Normalize(new RawRecord("entur-sky-vm", _receivedAt, truncated, Topic: null), new DropLog()));
    }

    [Fact]
    public void Response_with_a_dtd_throws_format_exception()
    {
        var withDtd = SiriVmSamples.Response(Sample).Replace(
            "?>", "?><!DOCTYPE Siri [<!ENTITY x \"x\">]>", StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => new SiriVmPositionNormalizer().Normalize(
            new RawRecord("entur-sky-vm", _receivedAt, Encoding.UTF8.GetBytes(withDtd), Topic: null), new DropLog()));
    }

    [Fact]
    public void Reads_only_the_siri_vm_feed()
    {
        Assert.Equal(["entur-sky-vm"], new SiriVmPositionNormalizer().RawFeedIds);
    }
}
