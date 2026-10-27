using System.Globalization;
using System.Text;
using TransitPulse.Ingestor.Feeds.Entur;
using TransitPulse.Ingestor.Kafka;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Feeds.Entur;

/// <summary>Trimmed archived SIRI-VM responses against reviewed expected positions (see Fixtures/entur/README.md).</summary>
public sealed class SiriVmContractTests
{
    private static readonly string _directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "entur");

    [Theory]
    [InlineData("vm-full", "2026-10-24T20:49:37.798Z")]
    [InlineData("vm-delta", "2026-10-23T11:05:20.157Z")]
    public void Archived_response_normalizes_to_the_expected_positions(string name, string receivedAt)
    {
        var payload = File.ReadAllBytes(Path.Combine(_directory, $"{name}.xml"));
        var lines = File.ReadAllLines(Path.Combine(_directory, $"{name}.expected.jsonl"));
        var expected = lines.Where(line => line != "null").Select(line => PositionMessage.Parse(Encoding.UTF8.GetBytes(line)));
        var record = new RawRecord(
            SiriVmPositionNormalizer.RawFeedId, DateTimeOffset.Parse(receivedAt, CultureInfo.InvariantCulture), payload, Topic: null);

        var positions = new SiriVmPositionNormalizer().Normalize(record);

        // One line per record: a record added to the fixture without an expectation fails here.
        Assert.Equal(lines.Length, SiriVmReader.Read(new MemoryStream(payload)).Count());
        Assert.Equal(expected, positions);
    }
}
