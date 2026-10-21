using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TransitPulse.Ingestor.Feeds.Hsl;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Feeds.Hsl;

public sealed class HslMqttSourceTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Connection_setup_that_never_completes_fails_with_a_timeout()
    {
        // The kernel completes the TCP handshake from the listen backlog, but nobody answers the TLS ClientHello:
        // the same picture as a broker or network stalled mid-handshake.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var settings = new HslMqttOptions
        {
            Host = IPAddress.Loopback.ToString(),
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            ConnectTimeout = TimeSpan.FromMilliseconds(300),
        };
        var source = new HslMqttSource(Options.Create(settings), TimeProvider.System, NullLogger<HslMqttSource>.Instance);

        // Bounded so that a missing timeout fails the test with a cancellation instead of hanging the run.
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(TimeSpan.FromSeconds(10));
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => source.RunAsync(Channel.CreateUnbounded<RawRecord>().Writer, guard.Token));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Took {elapsed.Elapsed}");
    }

    [Fact]
    public async Task Message_split_between_reads_is_passed_on_whole_with_its_topic()
    {
        using var broker = new FakeBroker();
        var output = Channel.CreateUnbounded<RawRecord>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = broker.CreateSource().RunAsync(output.Writer, stop.Token);

        using var client = await broker.AcceptAsync();
        await client.SendAsync(ConnAck(0), Ct);
        await client.SendAsync(_subAck, Ct);
        var publish = Publish("/hfp/v2/journey/ongoing/vp/bus/0022/01234", """{"VP":{"veh":1234}}""");
        await client.SendAsync(publish.AsMemory(0, 5), Ct);

        // Gives the source time to read the first part alone, so the packet has to be reassembled across reads.
        await Task.Delay(100, Ct);
        await client.SendAsync(publish.AsMemory(5), Ct);

        var record = await output.Reader.ReadAsync(Ct).AsTask().WaitAsync(_timeout, Ct);
        Assert.Equal("hsl", record.FeedId);
        Assert.Equal("/hfp/v2/journey/ongoing/vp/bus/0022/01234", record.Topic);
        Assert.Equal("""{"VP":{"veh":1234}}""", Encoding.UTF8.GetString(record.Payload));

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Refused_connection_ends_the_run_with_an_error()
    {
        using var broker = new FakeBroker();
        var run = broker.CreateSource().RunAsync(Channel.CreateUnbounded<RawRecord>().Writer, Ct);

        using var client = await broker.AcceptAsync();
        await client.SendAsync(ConnAck(5), Ct);

        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(_timeout, Ct));
        Assert.Contains("CONNACK code 5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connection_closed_right_after_connecting_ends_the_run_with_an_error()
    {
        using var broker = new FakeBroker();
        var run = broker.CreateSource().RunAsync(Channel.CreateUnbounded<RawRecord>().Writer, Ct);

        using (var client = await broker.AcceptAsync())
        {
            await client.SendAsync(ConnAck(0), Ct);
            await client.SendAsync(_subAck, Ct);
            client.Shutdown(SocketShutdown.Both);
        }

        // A connection that did not last 10 s counts as a failure: the pipeline retries after its delay.
        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(_timeout, Ct));
        Assert.Contains("closed by the broker", error.Message, StringComparison.Ordinal);
    }

    private static readonly byte[] _subAck = [0x90, 0x03, 0x00, 0x01, 0x00];

    private static byte[] ConnAck(byte code) => [0x20, 0x02, 0x00, code];

    /// <summary>A QoS 0 PUBLISH; the test messages are short enough for a one-byte remaining length.</summary>
    private static byte[] Publish(string topic, string payload)
    {
        var topicBytes = Encoding.UTF8.GetBytes(topic);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var remaining = 2 + topicBytes.Length + payloadBytes.Length;
        return [0x30, (byte)remaining, (byte)(topicBytes.Length >> 8), (byte)topicBytes.Length, .. topicBytes, .. payloadBytes];
    }

    /// <summary>A broker on loopback without TLS; the test scripts what it sends.</summary>
    private sealed class FakeBroker : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public FakeBroker() => _listener.Start();

        public HslMqttSource CreateSource(TimeProvider? time = null)
        {
            var settings = new HslMqttOptions
            {
                Host = IPAddress.Loopback.ToString(),
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
                ConnectTimeout = _timeout,
                SilenceTimeout = _timeout,
            };
            return new HslMqttSource(Options.Create(settings), time ?? TimeProvider.System, NullLogger<HslMqttSource>.Instance, OpenTcpAsync);
        }

        /// <summary>Accepts the source's connection and reads its CONNECT and SUBSCRIBE.</summary>
        /// <remarks>Read so that closing the socket later sends FIN: unread incoming bytes would turn the close into a reset.</remarks>
        public async Task<Socket> AcceptAsync()
        {
            var client = await _listener.AcceptSocketAsync(Ct).AsTask().WaitAsync(_timeout, Ct);
            var buffer = new byte[1024];
            var filled = 0;
            while (CountPackets(buffer.AsSpan(0, filled)) < 2)
            {
                var read = await client.ReceiveAsync(buffer.AsMemory(filled), Ct).AsTask().WaitAsync(_timeout, Ct);
                Assert.NotEqual(0, read);
                filled += read;
            }

            return client;
        }

        public void Dispose() => _listener.Dispose();

        private static async Task<Stream> OpenTcpAsync(HslMqttOptions settings, CancellationToken cancellationToken)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(settings.Host, settings.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }

        private static int CountPackets(ReadOnlySpan<byte> data)
        {
            var count = 0;
            while (MqttWire.TryReadPacket(data, out _, out _, out _, out var length))
            {
                data = data[length..];
                count++;
            }

            return count;
        }
    }
}
