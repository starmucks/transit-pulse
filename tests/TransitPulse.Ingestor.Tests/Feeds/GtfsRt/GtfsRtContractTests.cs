using System.Globalization;
using System.Text;
using TransitPulse.Ingestor.Feeds.GtfsRt;
using TransitPulse.Ingestor.Kafka;
using TransitPulse.Ingestor.Pipeline;
using TransitPulse.Ingestor.Tests.Normalization;
using Rt = TransitRealtime;

namespace TransitPulse.Ingestor.Tests.Feeds.GtfsRt;

/// <summary>A trimmed archived MBTA snapshot against reviewed expected positions (see Fixtures/mbta/README.md).</summary>
public sealed class GtfsRtContractTests
{
    private static readonly string _directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mbta");

    [Fact]
    public void Archived_snapshot_normalizes_to_the_expected_positions()
    {
        var payload = File.ReadAllBytes(Path.Combine(_directory, "vp.pb"));
        var lines = File.ReadAllLines(Path.Combine(_directory, "vp.expected.jsonl"));
        var expected = lines.Where(line => line != "null").Select(line => PositionMessage.Parse(Encoding.UTF8.GetBytes(line)));
        var record = new RawRecord(
            "mbta-vp", DateTimeOffset.Parse("2026-10-26T00:00:03.443Z", CultureInfo.InvariantCulture), payload, Topic: null);

        var positions = new GtfsRtPositionNormalizer("mbta", "mbta-vp").Normalize(record, new DropLog());

        // One line per entity: an entity added to the fixture without an expectation fails here.
        Assert.Equal(lines.Length, Rt.FeedMessage.Parser.ParseFrom(payload).Entity.Count);
        Assert.Equal(expected, positions);
    }
}
