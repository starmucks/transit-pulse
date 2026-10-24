using System.Text;
using TransitPulse.Domain;
using TransitPulse.Ingestor.Kafka;

namespace TransitPulse.Ingestor.Tests.Kafka;

public sealed class PositionMessageTests
{
    private static readonly DateTimeOffset _observedAt = new(2026, 10, 2, 19, 10, 52, 101, TimeSpan.Zero);

    // Fields of a real HSL HFP sample (brief §4.1), already normalized.
    private static readonly VehiclePosition _full = new(
        "hsl", "40/415", "1009-1-2026-10-02-21:31", "1009", 60.202731, 24.924969,
        8.03f, 254f, -195, OccupancyLevel.Empty, _observedAt);

    [Fact]
    public void Key_combines_feed_and_vehicle()
    {
        Assert.Equal("hsl/40/415", PositionMessage.Key(_full));
    }

    [Fact]
    public void Value_is_camel_case_json_with_enum_names()
    {
        var json = Encoding.UTF8.GetString(PositionMessage.Value(_full));

        // Validated properties are redeclared in the record body, so they follow the compiler-generated ones.
        Assert.Equal(
            """{"tripId":"1009-1-2026-10-02-21:31","routeId":"1009","delaySec":-195,"occupancy":"Empty","feedId":"hsl","vehicleId":"40/415","lat":60.202731,"lon":24.924969,"speedMps":8.03,"bearing":254,"observedAt":"2026-10-02T19:10:52.101+00:00"}""",
            json);
    }

    [Fact]
    public void Unknown_fields_are_left_out()
    {
        var sparse = new VehiclePosition("mbta", "y1234", null, null, 42.35, -71.06, null, null, null, null, _observedAt);

        var json = Encoding.UTF8.GetString(PositionMessage.Value(sparse));

        Assert.Equal("""{"feedId":"mbta","vehicleId":"y1234","lat":42.35,"lon":-71.06,"observedAt":"2026-10-02T19:10:52.101+00:00"}""", json);
    }

    [Fact]
    public void Value_reads_back_to_the_same_position()
    {
        Assert.Equal(_full, PositionMessage.Parse(PositionMessage.Value(_full)));
    }

    [Fact]
    public void Parsed_value_is_validated_like_any_position()
    {
        var value = """{"feedId":"hsl","vehicleId":"v1","lat":91,"lon":24.9,"observedAt":"2026-10-02T19:10:52Z"}"""u8.ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => PositionMessage.Parse(value));
    }
}
