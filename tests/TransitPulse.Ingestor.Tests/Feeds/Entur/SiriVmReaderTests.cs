using System.Text;
using System.Xml;
using TransitPulse.Ingestor.Feeds.Entur;

namespace TransitPulse.Ingestor.Tests.Feeds.Entur;

public sealed class SiriVmReaderTests
{
    // A real record (bus 30, Bergen, 2026-10-23), except that the stop location differs from the vehicle location.
    private const string Activity = """
        <VehicleActivity><RecordedAtTime>2026-10-23T08:59:50+02:00</RecordedAtTime><ValidUntilTime>2026-10-23T14:59:50+02:00</ValidUntilTime>
        <ProgressBetweenStops><LinkDistance>703</LinkDistance><Percentage>0.00</Percentage></ProgressBetweenStops>
        <MonitoredVehicleJourney><LineRef>SKY:Line:30</LineRef><FramedVehicleJourneyRef><DataFrameRef>2026-10-23</DataFrameRef>
        <DatedVehicleJourneyRef>SKY:ServiceJourney:30-208199-20940214</DatedVehicleJourneyRef></FramedVehicleJourneyRef>
        <VehicleMode>bus</VehicleMode><PublishedLineName>30</PublishedLineName><OperatorRef>SKY:Operator:44</OperatorRef>
        <OriginRef>NSR:Quay:53418</OriginRef><OriginName>Åsane terminal C</OriginName><DestinationRef>NSR:Quay:55926</DestinationRef>
        <DestinationName>Tertnes</DestinationName><Monitored>true</Monitored><InCongestion>false</InCongestion><DataSource>SKY</DataSource>
        <VehicleLocation><Longitude>5.30651173554361</Longitude><Latitude>60.4569782037288</Latitude></VehicleLocation>
        <Bearing>271.0</Bearing><Occupancy>seatsAvailable</Occupancy><Delay>-PT10S</Delay><VehicleStatus>inProgress</VehicleStatus>
        <VehicleRef>3350447527</VehicleRef><MonitoredCall><StopPointRef>NSR:Quay:53561</StopPointRef><Order>4</Order>
        <StopPointName>Botnane, Tertnesvegen</StopPointName><VehicleAtStop>true</VehicleAtStop>
        <VehicleLocationAtStop><Longitude>5.3</Longitude><Latitude>60.4</Latitude></VehicleLocationAtStop>
        <DestinationDisplay>Tertnes</DestinationDisplay></MonitoredCall><IsCompleteStopSequence>false</IsCompleteStopSequence>
        </MonitoredVehicleJourney></VehicleActivity>
        """;

    private static List<SiriVehicleActivity> Read(params string[] activities) =>
        [.. SiriVmReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(SiriVmSamples.Response(activities))))];

    [Fact]
    public void Real_record_yields_every_used_field()
    {
        var activity = Assert.Single(Read(Activity));

        Assert.Equal(new SiriVehicleActivity(
            "2026-10-23T08:59:50+02:00", "3350447527", "SKY:Line:30", "SKY:ServiceJourney:30-208199-20940214",
            "60.4569782037288", "5.30651173554361", "271.0", "-PT10S", "seatsAvailable", "inProgress"), activity);
    }

    [Fact]
    public void Location_comes_from_the_vehicle_not_from_the_stop_read_later()
    {
        var activity = Assert.Single(Read(Activity));

        Assert.Equal(("60.4569782037288", "5.30651173554361"), (activity.Latitude, activity.Longitude));
    }

    [Fact]
    public void Absent_elements_are_null()
    {
        // Ferries come without VehicleMode, VehicleStatus, Occupancy and Bearing.
        const string ferry = """
            <VehicleActivity><RecordedAtTime>2026-10-23T09:00:01.776+02:00</RecordedAtTime><MonitoredVehicleJourney>
            <LineRef>SKY:Line:3001</LineRef><Monitored>true</Monitored>
            <VehicleLocation><Longitude>5.1</Longitude><Latitude>60.3</Latitude></VehicleLocation>
            <Delay>PT1M</Delay><VehicleRef>257112000</VehicleRef></MonitoredVehicleJourney></VehicleActivity>
            """;

        var activity = Assert.Single(Read(ferry));

        Assert.Equal(new SiriVehicleActivity(
            "2026-10-23T09:00:01.776+02:00", "257112000", "SKY:Line:3001", null, "60.3", "5.1", null, "PT1M", null, null), activity);
    }

    [Fact]
    public void Elements_with_known_names_inside_skipped_elements_are_ignored()
    {
        var withNestedDelay = Activity.Replace("<Order>4</Order>", "<Order>4</Order><Delay>PT1H</Delay>", StringComparison.Ordinal);

        Assert.Equal("-PT10S", Assert.Single(Read(withNestedDelay)).Delay);
    }

    [Fact]
    public void Records_are_returned_in_document_order()
    {
        var second = Activity.Replace("3350447527", "3350447528", StringComparison.Ordinal);

        Assert.Equal(["3350447527", "3350447528"], Read(Activity, second).Select(a => a.VehicleRef));
    }

    [Fact]
    public void Empty_elements_do_not_shift_the_reader()
    {
        const string emptyLocation = """
            <VehicleActivity><MonitoredVehicleJourney><VehicleLocation/><Delay>PT1M</Delay>
            <VehicleRef>1</VehicleRef></MonitoredVehicleJourney></VehicleActivity>
            """;

        var activities = Read("<VehicleActivity/>", emptyLocation, Activity);

        Assert.Equal(new SiriVehicleActivity(null, null, null, null, null, null, null, null, null, null), activities[0]);
        Assert.Equal(("1", null, "PT1M"), (activities[1].VehicleRef, activities[1].Latitude, activities[1].Delay));
        Assert.Equal("3350447527", activities[2].VehicleRef);
    }

    [Fact]
    public void Response_without_records_yields_nothing()
    {
        Assert.Empty(Read());
    }

    [Fact]
    public void Truncated_response_throws_xml_exception()
    {
        var truncated = SiriVmSamples.Response(Activity)[..^200];

        Assert.Throws<XmlException>(() => SiriVmReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(truncated))).ToList());
    }
}
