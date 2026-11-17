using System.Text;

namespace TransitPulse.Domain.Tests;

public sealed class StopEventMessageTests
{
    // The bus fixture's trip (Fixtures/tracks/README.md) at its 12th stop.
    private static readonly VehicleStopEvent _arrival = new(
        "hsl", "18/165", "9841_20261030_Ke_1_0800", "9841", 0, new DateOnly(2026, 11, 4),
        "1020453", 12, StopEventKind.Arrival, new DateTimeOffset(2026, 11, 4, 8, 12, 30, 500, TimeSpan.Zero));

    [Fact]
    public void From_takes_the_vehicle_from_the_position_and_the_line_from_the_trip()
    {
        var at = _arrival.At;
        var position = new VehiclePosition("hsl", "18/165", "9841/0/20261104/08:00:00", "9841K", 60.2, 24.9, 0f, null, null, null, at);
        var trip = new ScheduledTrip("9841_20261030_Ke_1_0800", "9841", 0, 28800, new DateOnly(2026, 11, 4));
        var found = new StopEvent(StopEventKind.Arrival, new StopCall("1020453", 12, 5210.4, 3.1), at);

        Assert.Equal(_arrival, VehicleStopEvent.From(position, trip, found));
    }

    [Theory]
    [InlineData(StopEventKind.Arrival, "arrival")]
    [InlineData(StopEventKind.Departure, "departure")]
    [InlineData(StopEventKind.Passed, "passed")]
    public void Id_is_made_of_what_the_event_is_about(StopEventKind kind, string name)
    {
        Assert.Equal($"hsl/18/165/9841_20261030_Ke_1_0800/2026-11-04/12/{name}", (_arrival with { Kind = kind }).Id);
    }

    [Fact]
    public void Id_does_not_depend_on_when_the_event_was_placed()
    {
        Assert.Equal(_arrival.Id, (_arrival with { At = _arrival.At.AddSeconds(2) }).Id);
    }

    [Fact]
    public void Key_combines_feed_and_line()
    {
        Assert.Equal("hsl/9841", StopEventMessage.Key(_arrival));
    }

    [Fact]
    public void Value_is_camel_case_json_with_the_id()
    {
        var json = Encoding.UTF8.GetString(StopEventMessage.Value(_arrival));

        Assert.Equal(
            """{"feedId":"hsl","vehicleId":"18/165","tripId":"9841_20261030_Ke_1_0800","routeId":"9841","directionId":0,"serviceDate":"2026-11-04","stopId":"1020453","stopSequence":12,"kind":"Arrival","at":"2026-11-04T08:12:30.5+00:00","id":"hsl/18/165/9841_20261030_Ke_1_0800/2026-11-04/12/arrival"}""",
            json);
    }

    [Fact]
    public void Value_reads_back_to_the_same_event()
    {
        Assert.Equal(_arrival, StopEventMessage.Parse(StopEventMessage.Value(_arrival)));
    }
}
