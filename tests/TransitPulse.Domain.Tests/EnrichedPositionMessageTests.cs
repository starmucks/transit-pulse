using System.Text;

namespace TransitPulse.Domain.Tests;

public sealed class EnrichedPositionMessageTests
{
    private static readonly VehiclePosition _position = new(
        "hsl", "40/415", "1009/0/20261104/14:28:00", "1009", 60.2, 24.9, 8, 180, 45, OccupancyLevel.FewSeatsAvailable,
        new DateTimeOffset(2026, 11, 4, 12, 30, 0, TimeSpan.Zero));

    [Fact]
    public void Round_trip_keeps_source_position_and_scheduled_progress()
    {
        var enriched = new EnrichedPosition(
            _position,
            new ScheduledProgress("1009_trip", "1009", 0, new DateOnly(2026, 11, 4), 1250.5, new EnrichedStop("1020453", 7, 52200, 52230)),
            45);

        var value = EnrichedPositionMessage.Value(enriched);

        Assert.Equal("hsl/40/415", EnrichedPositionMessage.Key(enriched));
        Assert.Equal(enriched, EnrichedPositionMessage.Parse(value));
    }

    [Fact]
    public void Json_contract_distinguishes_source_and_effective_delay()
    {
        var enriched = new EnrichedPosition(_position, null, 50);

        Assert.Equal(
            """{"position":{"tripId":"1009/0/20261104/14:28:00","routeId":"1009","delaySec":45,"occupancy":"FewSeatsAvailable","feedId":"hsl","vehicleId":"40/415","lat":60.2,"lon":24.9,"speedMps":8,"bearing":180,"observedAt":"2026-11-04T12:30:00+00:00"},"delaySec":50}""",
            Encoding.UTF8.GetString(EnrichedPositionMessage.Value(enriched)));
    }
}
