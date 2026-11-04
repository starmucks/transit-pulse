using System.Text;
using System.Text.Json.Nodes;
using TransitPulse.Domain;
using TransitPulse.Ingestor.Feeds.Hsl;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;
using TransitPulse.Ingestor.Tests.Normalization;

namespace TransitPulse.Ingestor.Tests.Feeds.Hsl;

public sealed class HslPositionNormalizerTests
{
    // A real message (tram 2, 2026-10-22); each test changes only the fields it is about.
    private const string Sample = """
        {"VP":{"desi":"2","dir":"1","oper":40,"veh":108,"tst":"2026-10-22T06:59:59.952Z","tsi":1792652399,"spd":0.00,
        "hdg":357,"lat":60.167646,"long":24.952242,"acc":0.00,"dl":64,"odo":732,"drst":0,"oday":"2026-10-22","jrn":578,
        "line":30,"start":"09:58","loc":"GPS","stop":1030425,"route":"1002","occu":0}}
        """;

    private const string SampleTopic = "/hfp/v2/journey/ongoing/vp/tram/0040/00108/1002/1/Katajanokka/09:58/1030425/5/60;24/19/65/64";

    private static IReadOnlyList<VehiclePosition> Normalize(params (string Field, string Json)[] changes) =>
        Normalize(new DropLog(), changes);

    private static IReadOnlyList<VehiclePosition> Normalize(DropLog drops, params (string Field, string Json)[] changes)
    {
        var root = JsonNode.Parse(Sample)!;
        foreach (var (field, json) in changes)
        {
            root["VP"]![field] = JsonNode.Parse(json);
        }

        return new HslPositionNormalizer().Normalize(Record(root.ToJsonString()), drops);
    }

    private static VehiclePosition Position(params (string Field, string Json)[] changes) => Assert.Single(Normalize(changes));

    private static RawRecord Record(string payload, string? topic = SampleTopic) =>
        new("hsl", DateTimeOffset.UnixEpoch, Encoding.UTF8.GetBytes(payload), topic);

    [Fact]
    public void Real_message_maps_every_field()
    {
        var position = Position();

        Assert.Equal(new VehiclePosition(
            "hsl", "40/108", "1002/0/20261022/09:58:00", "1002", 60.167646, 24.952242, 0f, 357f,
            -64, null, new DateTimeOffset(2026, 10, 22, 6, 59, 59, 952, TimeSpan.Zero)), position);
    }

    [Theory]
    [InlineData(-195, 195)]
    [InlineData(30, -30)]
    [InlineData(-7200, 7200)]
    public void Delay_is_positive_when_late(int dl, int expected)
    {
        Assert.Equal(expected, Position(("dl", $"{dl}")).DelaySec);
    }

    [Theory]
    [InlineData(-7201)]
    [InlineData(-43468)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Implausible_delay_is_dropped_but_the_position_is_kept(int dl)
    {
        Assert.Null(Position(("dl", $"{dl}")).DelaySec);
    }

    [Theory]
    [InlineData("2026-10-20T21:20:00Z", "2026-10-20", "00:15", "20261020/24:15:00")] // after midnight
    [InlineData("2026-10-20T21:10:00Z", "2026-10-20", "00:15", "20261020/24:15:00")] // waits before departure
    [InlineData("2026-10-20T21:10:00Z", "2026-10-20", "23:50", "20261020/23:50:00")] // runs past midnight
    [InlineData("2026-10-21T00:15:00Z", "2026-10-20", "03:10", "20261020/27:10:00")] // night journey of the day before
    [InlineData("2026-10-21T00:35:00Z", "2026-10-21", "03:30", "20261021/03:30:00")] // first journey of the day
    [InlineData("2026-10-20T20:39:00Z", "2026-10-20", "10:09", "20261020/10:09:00")] // still on a journey 12.5 h old
    public void Trip_start_is_written_as_gtfs_time(string tst, string oday, string start, string expected)
    {
        var position = Position(("tst", $"\"{tst}\""), ("oday", $"\"{oday}\""), ("start", $"\"{start}\""));

        Assert.Equal($"1002/0/{expected}", position.TripId);
    }

    [Fact]
    public void Placeholder_journey_keeps_the_position_without_trip_route_or_delay()
    {
        // A plausible offset, so only the placeholder rule can drop it.
        var position = Position(("jrn", "9999"), ("dl", "-60"));

        Assert.Equal((null, null, null), (position.TripId, position.RouteId, position.DelaySec));
        Assert.Equal(60.167646, position.Lat);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, null)]
    [InlineData(1, OccupancyLevel.ManySeatsAvailable)]
    [InlineData(49, OccupancyLevel.ManySeatsAvailable)]
    [InlineData(50, OccupancyLevel.FewSeatsAvailable)]
    [InlineData(89, OccupancyLevel.FewSeatsAvailable)]
    [InlineData(90, OccupancyLevel.StandingRoomOnly)]
    [InlineData(99, OccupancyLevel.StandingRoomOnly)]
    [InlineData(100, OccupancyLevel.Full)]
    [InlineData(101, null)]
    public void Occupancy_maps_percent_to_level(int occu, OccupancyLevel? expected)
    {
        Assert.Equal(expected, Position(("occu", $"{occu}")).Occupancy);
    }

    [Fact]
    public void Missing_values_become_null_and_integer_speed_is_read()
    {
        var position = Position(("hdg", "null"), ("dl", "null"), ("dir", "null"), ("spd", "35"));

        Assert.Equal((null, null, null, 35f), (position.Bearing, position.DelaySec, position.TripId, position.SpeedMps));
    }

    [Theory]
    [InlineData("lat", "null", PositionMetrics.BadLocation)]
    [InlineData("long", "null", PositionMetrics.BadLocation)]
    [InlineData("tst", "null", PositionMetrics.Incomplete)]
    [InlineData("lat", "91", PositionMetrics.BadLocation)]
    [InlineData("long", "-181", PositionMetrics.BadLocation)]
    public void Vehicle_without_valid_coordinates_or_time_is_skipped(string field, string json, string reason)
    {
        var drops = new DropLog();

        Assert.Empty(Normalize(drops, (field, json)));
        Assert.Equal([reason], drops.Reasons);
    }

    [Fact]
    public void Vehicle_is_identified_by_the_topics_owner_not_by_the_payloads_operator()
    {
        var topic = "/hfp/v2/journey/ongoing/vp/bus/0020/00016/5583K/2/Aviapolis/15:26/4180212/5/60;24/28/96/06";

        var position = Assert.Single(new HslPositionNormalizer().Normalize(Record(Sample, topic), new DropLog()));

        // The sample's payload says oper 40, veh 108.
        Assert.Equal("20/16", position.VehicleId);
    }

    [Fact]
    public void Payload_without_oper_and_veh_still_yields_a_position()
    {
        Assert.Equal("40/108", Position(("oper", "null"), ("veh", "null")).VehicleId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/hfp/v2/journey/ongoing/vp/tram")]
    [InlineData("/hfp/v2/journey/ongoing/vp/tram/0040")]
    [InlineData("/hfp/v2/journey/ongoing/vp/tram/0040/")]
    [InlineData("/hfp/v2/journey/ongoing/vp/tram/x040/00108/1002")]
    [InlineData("/hfp/v2/journey/ongoing/vp/tram/0040/00108x/1002")]
    [InlineData("/hfp/v2/journey/ongoing/vp/tram/-040/00108/1002")]
    public void Without_a_usable_topic_the_vehicle_is_skipped_even_though_the_payload_names_one(string? topic)
    {
        var drops = new DropLog();

        Assert.Empty(new HslPositionNormalizer().Normalize(Record(Sample, topic), drops));
        Assert.Equal([PositionMetrics.Incomplete], drops.Reasons);
    }

    [Theory]
    [InlineData("""{"DUE":{"veh":1}}""", false)]
    [InlineData("""{"VP":""", true)]
    public void Other_events_yield_nothing_and_broken_json_fails(string payload, bool fails)
    {
        var record = Record(payload);
        var normalizer = new HslPositionNormalizer();

        if (fails)
        {
            Assert.Throws<FormatException>(() => normalizer.Normalize(record, new DropLog()));
        }
        else
        {
            var drops = new DropLog();

            Assert.Empty(normalizer.Normalize(record, drops));
            Assert.Empty(drops.Reasons);
        }
    }
}
