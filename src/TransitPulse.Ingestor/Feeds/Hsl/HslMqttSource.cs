using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Feeds.Hsl;

/// <summary>
/// HSL vehicle positions pushed over MQTT (about 1 message per vehicle per second). Every message is passed on
/// unchanged with its topic, which carries mode, route, direction and next stop.
/// A connection that drops after working for a while is re-opened at once, so a drop costs about a second of data;
/// failing to connect, or dropping right after connecting, ends the run and the pipeline retries after its delay.
/// </summary>
/// <remarks>
/// A minimal MQTT 3.1.1 client (<see cref="MqttWire"/>) instead of MQTTnet: on the 1 vCPU collector server MQTTnet
/// received ~200 messages/s of the ~780/s HSL sends, the broker's queue for us grew and the connection dropped every
/// ~20 s; a plain read loop over large blocks keeps up (measured 2026-10-17).
/// </remarks>
/// <param name="options">Broker and topic settings.</param>
/// <param name="timeProvider">Clock for receive times, pings and the silence check.</param>
/// <param name="logger">Feed log.</param>
/// <param name="openTransport">
/// Opens the byte stream to the broker; TCP + TLS when <see langword="null"/>. Tests pass plain loopback TCP to drive
/// the MQTT client against a fake broker.
/// </param>
internal sealed partial class HslMqttSource(
    IOptions<HslMqttOptions> options,
    TimeProvider timeProvider,
    ILogger<HslMqttSource> logger,
    Func<HslMqttOptions, CancellationToken, Task<Stream>>? openTransport = null) : IFeedSource
{
    private const ushort KeepAliveSeconds = 60;

    // HFP messages are about 0.5 KB; the buffer only has to hold the largest single packet plus one read.
    private const int BufferSize = 256 * 1024;

    // Pinging at half the keep-alive leaves the broker's 1.5x grace period untouched by network jitter.
    private static readonly TimeSpan _pingInterval = TimeSpan.FromSeconds(KeepAliveSeconds / 2);

    // Shorter-lived connections count as a failure, so a broker that keeps dropping us is not hammered in a loop.
    private static readonly TimeSpan _healthyConnection = TimeSpan.FromSeconds(10);

    private static readonly byte[] _pingReq = MqttWire.PingReq.ToArray();

    private readonly Func<HslMqttOptions, CancellationToken, Task<Stream>> _openTransport = openTransport ?? OpenTlsAsync;

    /// <inheritdoc />
    public string FeedId => "hsl";

    /// <inheritdoc />
    public TimeSpan? MaxSilence => options.Value.MaxSilence;

    /// <inheritdoc />
    public async Task RunAsync(ChannelWriter<RawRecord> output, CancellationToken cancellationToken)
    {
        while (true)
        {
            var started = timeProvider.GetTimestamp();
            var reason = await RunConnectionAsync(output, cancellationToken);
            var lasted = timeProvider.GetElapsedTime(started);
            if (lasted < _healthyConnection)
            {
                throw new IOException($"HSL MQTT connection lasted only {lasted:g}: {reason}");
            }

            LogReconnecting(reason, lasted);
        }
    }

    /// <summary>Connects, subscribes and forwards messages until the connection drops or goes silent.</summary>
    /// <returns>Why the connection ended.</returns>
    private async Task<string> RunConnectionAsync(ChannelWriter<RawRecord> output, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var tls = await ConnectAsync(settings, cancellationToken);

        var buffer = new byte[BufferSize];
        var filled = 0;
        var records = new List<RawRecord>();
        var lastPing = timeProvider.GetTimestamp();
        var lastMessage = timeProvider.GetTimestamp();

        // Re-armed before every read: bounds how long a dead socket can block the read.
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            readTimeout.CancelAfter(settings.SilenceTimeout);
            int read;
            try
            {
                read = await tls.ReadAsync(buffer.AsMemory(filled), readTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return $"nothing received for {settings.SilenceTimeout:g}";
            }
            catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A reset or a broken socket ends the connection like a close does: RunAsync judges it by how long it lasted.
                return $"connection lost: {ex.Message}";
            }

            if (read == 0)
            {
                return "closed by the broker";
            }

            filled += read;
            var consumed = ReadPackets(buffer.AsSpan(0, filled), timeProvider.GetUtcNow(), records);
            buffer.AsSpan(consumed, filled - consumed).CopyTo(buffer);
            filled -= consumed;
            if (filled == buffer.Length)
            {
                throw new InvalidDataException($"An MQTT packet is larger than the {BufferSize}-byte receive buffer.");
            }

            if (records.Count > 0)
            {
                lastMessage = timeProvider.GetTimestamp();
                foreach (var record in records)
                {
                    // Waits when the bounded channel is full, which stops reading the socket: backpressure to the broker.
                    await output.WriteAsync(record, cancellationToken);
                }

                records.Clear();
            }
            else if (timeProvider.GetElapsedTime(lastMessage) > settings.SilenceTimeout)
            {
                // Pings still answered but no data: the subscription is gone.
                return $"no message for {settings.SilenceTimeout:g}";
            }

            if (timeProvider.GetElapsedTime(lastPing) >= _pingInterval)
            {
                // A peer that stopped reading fills the send buffer, and then even a 2-byte write never completes.
                readTimeout.CancelAfter(settings.SilenceTimeout);
                try
                {
                    await tls.WriteAsync(_pingReq, readTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return $"ping not sent within {settings.SilenceTimeout:g}";
                }
                catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    return $"connection lost while pinging: {ex.Message}";
                }

                lastPing = timeProvider.GetTimestamp();
            }
        }
    }

    /// <summary>Opens TCP and TLS to the broker.</summary>
    /// <returns>The TLS stream; disposing it closes the socket too.</returns>
    private static async Task<Stream> OpenTlsAsync(HslMqttOptions settings, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        SslStream? tls = null;
        try
        {
            await socket.ConnectAsync(settings.Host, settings.Port, cancellationToken);
            tls = new SslStream(new NetworkStream(socket, ownsSocket: true));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = settings.Host }, cancellationToken);
            return tls;
        }
        catch
        {
            if (tls is not null)
            {
                await tls.DisposeAsync();
            }
            else
            {
                socket.Dispose();
            }

            throw;
        }
    }

    /// <summary>Opens the transport and subscribes, all within <see cref="HslMqttOptions.ConnectTimeout"/>.</summary>
    /// <returns>The stream, subscription sent; the caller owns it.</returns>
    /// <exception cref="TimeoutException">The setup did not finish in time; the pipeline retries after its delay.</exception>
    private async Task<Stream> ConnectAsync(HslMqttOptions settings, CancellationToken cancellationToken)
    {
        // The read loop's silence check starts only after this setup, so the setup needs a deadline of its own.
        using var setupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setupTimeout.CancelAfter(settings.ConnectTimeout);
        Stream? stream = null;
        try
        {
            stream = await _openTransport(settings, setupTimeout.Token);

            // Clean session with a random id: HFP is QoS 0, so there is nothing for the broker to keep between connections.
            await stream.WriteAsync(MqttWire.Connect($"transitpulse-{Guid.NewGuid():N}", KeepAliveSeconds), setupTimeout.Token);
            await stream.WriteAsync(MqttWire.Subscribe(1, settings.Topic), setupTimeout.Token);
            return stream;
        }
        catch (Exception ex)
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }

            // Thrown, not returned as a reason: a setup that hangs for the whole timeout must not count as a healthy
            // connection and be retried at once in a loop.
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"HSL MQTT connection to {settings.Host}:{settings.Port} was not set up within {settings.ConnectTimeout:g}.", ex);
            }

            throw;
        }
    }

    /// <summary>Consumes every complete packet at the start of <paramref name="data"/>.</summary>
    /// <returns>Bytes consumed; an incomplete packet at the end stays for the next read.</returns>
    private int ReadPackets(ReadOnlySpan<byte> data, DateTimeOffset receivedAt, List<RawRecord> records)
    {
        var consumed = 0;
        while (MqttWire.TryReadPacket(data[consumed..], out var type, out var flags, out var body, out var length))
        {
            consumed += length;
            switch (type)
            {
                case MqttWire.Publish:
                    var topic = MqttWire.ReadPublish(flags, body, out var payload);
                    records.Add(new RawRecord(FeedId, receivedAt, payload.ToArray(), topic));
                    break;
                case MqttWire.ConnAck when body.Length < 2 || body[1] != 0:
                    throw new IOException($"HSL MQTT broker refused the connection (CONNACK code {(body.Length < 2 ? -1 : body[1])}).");
                case MqttWire.SubAck when body.Length < 3 || body[2] == 0x80:
                    throw new IOException($"HSL MQTT broker refused the subscription to {options.Value.Topic}.");
                case MqttWire.SubAck:
                    LogSubscribed(options.Value.Host, options.Value.Topic);
                    break;
            }
        }

        return consumed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscribed to {Topic} on {Host}")]
    private partial void LogSubscribed(string host, string topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HSL MQTT connection ended after {Lasted}: {Reason}; reconnecting now")]
    private partial void LogReconnecting(string reason, TimeSpan lasted);
}
