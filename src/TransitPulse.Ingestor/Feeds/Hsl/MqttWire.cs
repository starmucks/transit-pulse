using System.Buffers.Binary;
using System.Text;

namespace TransitPulse.Ingestor.Feeds.Hsl;

/// <summary>
/// The small subset of MQTT 3.1.1 a QoS 0 subscriber needs: CONNECT, SUBSCRIBE and PINGREQ out; CONNACK, SUBACK,
/// PUBLISH and PINGRESP in. Pure functions over byte buffers, so the wire format is unit-tested without a broker.
/// Spec: https://docs.oasis-open.org/mqtt/mqtt/v3.1.1/os/mqtt-v3.1.1-os.html
/// </summary>
internal static class MqttWire
{
    /// <summary>Control packet types (high nibble of the first byte).</summary>
    public const byte ConnAck = 2, Publish = 3, SubAck = 9, PingResp = 13;

    /// <summary>PINGREQ: keeps the broker from closing a connection on which the client sends nothing else.</summary>
    public static ReadOnlySpan<byte> PingReq => [0xC0, 0x00];

    /// <summary>CONNECT with a clean session and no credentials.</summary>
    /// <param name="clientId">Client identifier, unique per connection.</param>
    /// <param name="keepAliveSeconds">Interval within which the client promises to send a packet.</param>
    public static byte[] Connect(string clientId, ushort keepAliveSeconds)
    {
        var body = new List<byte>();
        AddString(body, "MQTT");
        body.Add(4);    // protocol level 3.1.1
        body.Add(0x02); // clean session
        body.Add((byte)(keepAliveSeconds >> 8));
        body.Add((byte)keepAliveSeconds);
        AddString(body, clientId);
        return Packet(0x10, body);
    }

    /// <summary>SUBSCRIBE to one topic filter at QoS 0.</summary>
    /// <param name="packetId">Non-zero packet identifier echoed in SUBACK.</param>
    /// <param name="topicFilter">Topic filter, wildcards allowed.</param>
    public static byte[] Subscribe(ushort packetId, string topicFilter)
    {
        var body = new List<byte> { (byte)(packetId >> 8), (byte)packetId };
        AddString(body, topicFilter);
        body.Add(0); // requested QoS
        return Packet(0x82, body);
    }

    /// <summary>Finds the first complete packet at the start of <paramref name="buffer"/>.</summary>
    /// <param name="buffer">Received bytes not yet consumed.</param>
    /// <param name="type">Packet type.</param>
    /// <param name="flags">Low nibble of the first byte (QoS and flags for PUBLISH).</param>
    /// <param name="body">Variable header and payload.</param>
    /// <param name="length">Total bytes the packet occupies, to consume.</param>
    /// <returns><see langword="false"/> when more bytes are needed.</returns>
    /// <exception cref="InvalidDataException">The remaining-length field is longer than 4 bytes.</exception>
    public static bool TryReadPacket(ReadOnlySpan<byte> buffer, out byte type, out byte flags, out ReadOnlySpan<byte> body, out int length)
    {
        type = flags = 0;
        body = default;
        length = 0;
        if (buffer.Length < 2)
        {
            return false;
        }

        // Remaining length: 7 bits per byte, low bits first, high bit means "more bytes follow" (at most 4 bytes).
        var remaining = 0;
        var i = 1;
        for (var shift = 0; ; shift += 7, i++)
        {
            if (i > 4)
            {
                throw new InvalidDataException("MQTT remaining length is longer than 4 bytes.");
            }

            if (i >= buffer.Length)
            {
                return false;
            }

            remaining |= (buffer[i] & 0x7F) << shift;
            if ((buffer[i] & 0x80) == 0)
            {
                break;
            }
        }

        var headerLength = i + 1;
        if (buffer.Length - headerLength < remaining)
        {
            return false;
        }

        type = (byte)(buffer[0] >> 4);
        flags = (byte)(buffer[0] & 0x0F);
        body = buffer.Slice(headerLength, remaining);
        length = headerLength + remaining;
        return true;
    }

    /// <summary>Splits a PUBLISH body into topic and payload; a packet identifier (QoS 1 and 2) is skipped.</summary>
    /// <param name="flags">Flags from <see cref="TryReadPacket"/>.</param>
    /// <param name="body">Body from <see cref="TryReadPacket"/>.</param>
    /// <param name="payload">Message bytes.</param>
    /// <returns>The topic.</returns>
    public static string ReadPublish(byte flags, ReadOnlySpan<byte> body, out ReadOnlySpan<byte> payload)
    {
        var topicLength = BinaryPrimitives.ReadUInt16BigEndian(body);
        var topic = Encoding.UTF8.GetString(body.Slice(2, topicLength));
        var qos = (flags >> 1) & 0x03;
        payload = body[(2 + topicLength + (qos > 0 ? 2 : 0))..];
        return topic;
    }

    private static void AddString(List<byte> body, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        body.Add((byte)(bytes.Length >> 8));
        body.Add((byte)bytes.Length);
        body.AddRange(bytes);
    }

    private static byte[] Packet(byte firstByte, List<byte> body)
    {
        var packet = new List<byte>(body.Count + 5) { firstByte };
        var remaining = body.Count;
        do
        {
            var digit = (byte)(remaining % 128);
            remaining /= 128;
            packet.Add(remaining > 0 ? (byte)(digit | 0x80) : digit);
        }
        while (remaining > 0);

        packet.AddRange(body);
        return [.. packet];
    }
}
