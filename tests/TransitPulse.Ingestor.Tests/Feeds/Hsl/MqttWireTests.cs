using System.Text;
using TransitPulse.Ingestor.Feeds.Hsl;

namespace TransitPulse.Ingestor.Tests.Feeds.Hsl;

public sealed class MqttWireTests
{
    [Fact]
    public void Connect_matches_the_spec_layout()
    {
        byte[] expected = [0x10, 13, 0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4, 0x02, 0, 60, 0, 1, (byte)'c'];

        Assert.Equal(expected, MqttWire.Connect("c", keepAliveSeconds: 60));
    }

    [Fact]
    public void Subscribe_matches_the_spec_layout()
    {
        byte[] expected = [0x82, 8, 0, 1, 0, 3, (byte)'a', (byte)'/', (byte)'#', 0];

        Assert.Equal(expected, MqttWire.Subscribe(1, "a/#"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]   // largest 1-byte remaining length
    [InlineData(128)]   // smallest 2-byte
    [InlineData(16_383)]
    [InlineData(16_384)] // smallest 3-byte
    public void Reads_publish_with_any_remaining_length_size(int payloadLength)
    {
        var payload = Enumerable.Range(0, payloadLength).Select(i => (byte)i).ToArray();
        var packet = PublishPacket("/hfp/v2/x", payload, qos: 0);

        Assert.True(MqttWire.TryReadPacket(packet, out var type, out var flags, out var body, out var length));
        Assert.Equal(MqttWire.Publish, type);
        Assert.Equal(packet.Length, length);
        Assert.Equal("/hfp/v2/x", MqttWire.ReadPublish(flags, body, out var read));
        Assert.Equal(payload, read.ToArray());
    }

    [Fact]
    public void Skips_packet_id_of_qos1_publish()
    {
        var packet = PublishPacket("t", [7, 8], qos: 1);

        Assert.True(MqttWire.TryReadPacket(packet, out _, out var flags, out var body, out _));
        Assert.Equal("t", MqttWire.ReadPublish(flags, body, out var payload));
        Assert.Equal([7, 8], payload.ToArray());
    }

    [Fact]
    public void Needs_more_bytes_for_every_cut_inside_a_packet()
    {
        // A packet split between two socket reads must never be read early or out of bounds.
        var packet = PublishPacket("topic", new byte[200], qos: 0);
        for (var cut = 0; cut < packet.Length; cut++)
        {
            Assert.False(MqttWire.TryReadPacket(packet.AsSpan(0, cut), out _, out _, out _, out _));
        }
    }

    [Fact]
    public void Reads_packets_one_after_another()
    {
        byte[] stream = [.. PublishPacket("a", [1], 0), 0xD0, 0x00, .. PublishPacket("b", [2], 0)];

        Assert.True(MqttWire.TryReadPacket(stream, out var first, out _, out _, out var length1));
        Assert.True(MqttWire.TryReadPacket(stream.AsSpan(length1), out var second, out _, out _, out var length2));
        Assert.True(MqttWire.TryReadPacket(stream.AsSpan(length1 + length2), out var third, out _, out _, out _));
        Assert.Equal([MqttWire.Publish, MqttWire.PingResp, MqttWire.Publish], [first, second, third]);
    }

    [Fact]
    public void Rejects_remaining_length_longer_than_four_bytes()
    {
        byte[] malformed = [0x30, 0x80, 0x80, 0x80, 0x80, 0x01];

        Assert.Throws<InvalidDataException>(() => MqttWire.TryReadPacket(malformed, out _, out _, out _, out _));
    }

    private static byte[] PublishPacket(string topic, byte[] payload, int qos)
    {
        var topicBytes = Encoding.UTF8.GetBytes(topic);
        byte[] body = [(byte)(topicBytes.Length >> 8), (byte)topicBytes.Length, .. topicBytes, .. (qos > 0 ? new byte[] { 0, 1 } : []), .. payload];
        var header = new List<byte> { (byte)(0x30 | (qos << 1)) };
        var remaining = body.Length;
        do
        {
            var digit = (byte)(remaining % 128);
            remaining /= 128;
            header.Add(remaining > 0 ? (byte)(digit | 0x80) : digit);
        }
        while (remaining > 0);

        return [.. header, .. body];
    }
}
