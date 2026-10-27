using System.Xml;

namespace TransitPulse.Ingestor.Feeds.Entur;

/// <summary>
/// The fields of one SIRI-VM <c>VehicleActivity</c> the normalizer uses, as the text the feed sent (or
/// <see langword="null"/> when the element is absent). Values are parsed by the normalizer, which decides per field
/// what a bad value means.
/// </summary>
/// <param name="RecordedAtTime">When the position was measured, ISO 8601 with a local offset.</param>
/// <param name="VehicleRef">Vehicle id, unique within the dataset.</param>
/// <param name="LineRef">Line id, e.g. <c>SKY:Line:300</c>.</param>
/// <param name="DatedVehicleJourneyRef">Journey id, e.g. <c>SKY:ServiceJourney:300-209126-20438024</c>.</param>
/// <param name="Latitude">WGS 84 latitude from <c>VehicleLocation</c>.</param>
/// <param name="Longitude">WGS 84 longitude from <c>VehicleLocation</c>.</param>
/// <param name="Bearing">Heading in degrees clockwise from north.</param>
/// <param name="Delay">Deviation from schedule, ISO 8601 duration, negative when early (<c>-PT7M25S</c>).</param>
/// <param name="Occupancy">SIRI occupancy, e.g. <c>seatsAvailable</c>.</param>
/// <param name="VehicleStatus">Journey progress, e.g. <c>assigned</c>, <c>inProgress</c>.</param>
internal sealed record SiriVehicleActivity(
    string? RecordedAtTime,
    string? VehicleRef,
    string? LineRef,
    string? DatedVehicleJourneyRef,
    string? Latitude,
    string? Longitude,
    string? Bearing,
    string? Delay,
    string? Occupancy,
    string? VehicleStatus);

/// <summary>
/// Streaming reader of SIRI-VM responses: responses reach several MB, so the document is never loaded as a tree.
/// </summary>
internal static class SiriVmReader
{
    private const string SiriNamespace = "http://www.siri.org.uk/siri";

    private static readonly XmlReaderSettings _settings = new()
    {
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        DtdProcessing = DtdProcessing.Prohibit,
    };

    /// <summary>Reads every <c>VehicleActivity</c> of a response, in document order.</summary>
    /// <param name="xml">The response body; read lazily while the result is enumerated.</param>
    /// <exception cref="XmlException">
    /// The body is not well-formed XML, carries a DTD, or a used field has child elements, which SIRI 2.0 does not
    /// allow (thrown during enumeration).
    /// </exception>
    public static IEnumerable<SiriVehicleActivity> Read(Stream xml)
    {
        using var reader = XmlReader.Create(xml, _settings);
        while (reader.ReadToFollowing("VehicleActivity", SiriNamespace))
        {
            // Disposing the subtree reader leaves the outer one on </VehicleActivity>, ready for the next search.
            using var activity = reader.ReadSubtree();
            yield return ReadActivity(activity);
        }
    }

    private static SiriVehicleActivity ReadActivity(XmlReader reader)
    {
        string? recordedAt = null, vehicle = null, line = null, journey = null, latitude = null, longitude = null;
        string? bearing = null, delay = null, occupancy = null, status = null;

        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            // Only these containers are entered; every other element is skipped with its children. So Latitude is
            // reached only inside VehicleLocation, never inside VehicleLocationAtStop (5% of records carry both).
            switch (reader.LocalName)
            {
                case "VehicleActivity" or "MonitoredVehicleJourney" or "FramedVehicleJourneyRef" or "VehicleLocation":
                    reader.Read();
                    break;
                // ReadElementContentAsString moves past the end tag, so the loop must not call Read() again.
                case "RecordedAtTime": recordedAt = reader.ReadElementContentAsString(); break;
                case "VehicleRef": vehicle = reader.ReadElementContentAsString(); break;
                case "LineRef": line = reader.ReadElementContentAsString(); break;
                case "DatedVehicleJourneyRef": journey = reader.ReadElementContentAsString(); break;
                case "Latitude": latitude = reader.ReadElementContentAsString(); break;
                case "Longitude": longitude = reader.ReadElementContentAsString(); break;
                case "Bearing": bearing = reader.ReadElementContentAsString(); break;
                case "Delay": delay = reader.ReadElementContentAsString(); break;
                case "Occupancy": occupancy = reader.ReadElementContentAsString(); break;
                case "VehicleStatus": status = reader.ReadElementContentAsString(); break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new SiriVehicleActivity(recordedAt, vehicle, line, journey, latitude, longitude, bearing, delay, occupancy, status);
    }
}
