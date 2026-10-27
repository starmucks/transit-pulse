using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using TransitPulse.Domain;
using TransitPulse.Ingestor.Feeds.Hsl;
using TransitPulse.Ingestor.Kafka;
using TransitPulse.Ingestor.Pipeline;
using TransitPulse.Ingestor.Tests.Normalization;

namespace TransitPulse.Ingestor.Tests.Feeds.Hsl;

/// <summary>Unchanged archived HFP messages against reviewed expected positions (see Fixtures/hsl/README.md).</summary>
public sealed class HslContractTests
{
    private static readonly string _directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hsl");
    private static readonly string[] _messages = File.ReadAllLines(Path.Combine(_directory, "vp.jsonl"));
    private static readonly string[] _expected = File.ReadAllLines(Path.Combine(_directory, "vp.expected.jsonl"));

    private static readonly string[] _cases = [.. _messages.Select(line => (string)JsonNode.Parse(line)!["case"]!)];

    public static TheoryData<string> Cases => [.. _cases];

    [Fact]
    public void Every_message_has_an_expected_position_and_a_unique_case()
    {
        Assert.Equal(_messages.Length, _expected.Length);
        // Lines are found by case name: a repeated name would leave the second line untested.
        Assert.Equal(_cases.Length, _cases.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Archived_message_normalizes_to_the_expected_position(string name)
    {
        var line = Array.FindIndex(_messages, m => (string)JsonNode.Parse(m)!["case"]! == name);
        var message = JsonNode.Parse(_messages[line])!;
        var record = new RawRecord(
            HslPositionNormalizer.FeedId,
            DateTimeOffset.Parse((string)message["receivedAt"]!, CultureInfo.InvariantCulture),
            Encoding.UTF8.GetBytes((string)message["payload"]!),
            (string)message["topic"]!);
        VehiclePosition? expected = _expected[line] == "null" ? null : PositionMessage.Parse(Encoding.UTF8.GetBytes(_expected[line]));

        var drops = new DropLog();
        var positions = new HslPositionNormalizer().Normalize(record, drops);

        Assert.Equal(expected, Assert.Single(positions.DefaultIfEmpty()));
        // A message without a position is counted with a reason: nothing is dropped silently.
        Assert.Equal(expected is null ? 1 : 0, drops.Reasons.Count);
    }
}
