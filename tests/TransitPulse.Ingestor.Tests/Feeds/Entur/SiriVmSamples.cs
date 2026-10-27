namespace TransitPulse.Ingestor.Tests.Feeds.Entur;

/// <summary>Builds SIRI-VM responses around hand-picked <c>VehicleActivity</c> elements.</summary>
internal static class SiriVmSamples
{
    /// <summary>A response as Entur sends it (envelope of 2026-10-23), with the given records in its delivery.</summary>
    public static string Response(params string[] activities) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Siri xmlns="http://www.siri.org.uk/siri" xmlns:ns2="http://www.ifopt.org.uk/acsb" version="2.0"><ServiceDelivery>
        <ResponseTimestamp>2026-10-23T08:59:56.552794536+02:00</ResponseTimestamp><ProducerRef>ENT</ProducerRef>
        <MoreData>false</MoreData><VehicleMonitoringDelivery version="2.0">{string.Concat(activities)}</VehicleMonitoringDelivery>
        </ServiceDelivery></Siri>
        """;
}
