using System.Text.Json.Serialization;

namespace TransitPulse.Ingestor.Feeds.Hsl;

/// <summary>
/// One HSL High-Frequency Positioning message: an object with a single event key. The subscription takes only
/// <c>VP</c> (vehicle position) events, so other keys deserialize to a <see langword="null"/> <see cref="VP"/>.
/// </summary>
/// <param name="VP">The vehicle position event, if the message is one.</param>
internal sealed record HfpMessage(HfpVehiclePosition? VP);

/// <summary>
/// The fields of a <c>VP</c> event the normalizer uses; the rest (<c>line</c>, <c>stop</c>, … whose JSON types vary)
/// are skipped. Every field is nullable: the feed sends <c>null</c> for values a vehicle does not know.
/// </summary>
/// <param name="Oper">Operator number; with <paramref name="Veh"/> identifies the vehicle.</param>
/// <param name="Veh">Vehicle number, unique within the operator.</param>
/// <param name="Tst">When the vehicle measured the position (UTC, <c>Z</c>).</param>
/// <param name="Spd">Speed in m/s.</param>
/// <param name="Hdg">Heading in degrees clockwise from north.</param>
/// <param name="Lat">WGS 84 latitude.</param>
/// <param name="Lon">WGS 84 longitude (JSON <c>long</c>).</param>
/// <param name="Dl">Offset from schedule in seconds, positive = ahead of schedule.</param>
/// <param name="Oday">Operating day of the journey, <c>yyyy-MM-dd</c>.</param>
/// <param name="Start">Scheduled start of the journey, <c>HH:mm</c> local time; after midnight it restarts at 00.</param>
/// <param name="Route">Route id, as in HSL GTFS.</param>
/// <param name="Dir">Direction, <c>1</c> or <c>2</c> (GTFS <c>direction_id</c> + 1).</param>
/// <param name="Jrn">Journey number; <c>9999</c> marks a placeholder journey (e.g. a parked train).</param>
/// <param name="Occu">Occupancy, 0–100: buses send only 0 or 100 (full), ferries a measured percentage.</param>
internal sealed record HfpVehiclePosition(
    int? Oper,
    int? Veh,
    DateTimeOffset? Tst,
    float? Spd,
    float? Hdg,
    double? Lat,
    [property: JsonPropertyName("long")] double? Lon,
    int? Dl,
    string? Oday,
    string? Start,
    string? Route,
    string? Dir,
    int? Jrn,
    int? Occu);

/// <summary>Source-generated deserializer: no runtime reflection, so it works in the Native AOT build.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HfpMessage))]
internal sealed partial class HfpJsonContext : JsonSerializerContext;
