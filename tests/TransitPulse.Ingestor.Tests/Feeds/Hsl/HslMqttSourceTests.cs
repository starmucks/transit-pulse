using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
            FeedId = "hsl",
            Topics = [VpTopic],
            Host = IPAddress.Loopback.ToString(),
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            ConnectTimeout = TimeSpan.FromMilliseconds(300),
        };
        var source = new HslMqttSource(settings, TimeProvider.System, NullLogger<HslMqttSource>.Instance);

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
    public async Task Records_carry_the_feed_id_of_a_subscription_to_several_filters()
    {
        using var broker = new FakeBroker();
        var output = Channel.CreateUnbounded<RawRecord>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        string[] topics = ["/hfp/v2/journey/ongoing/ars/#", "/hfp/v2/journey/ongoing/dep/#"];
        var run = broker.CreateSource(feedId: "hsl-stop", topics: topics).RunAsync(output.Writer, stop.Token);

        using var client = await broker.AcceptAsync();
        await client.SendAsync(ConnAck(0), Ct);
        await client.SendAsync(new byte[] { 0x90, 0x04, 0x00, 0x01, 0x00, 0x00 }, Ct);
        await client.SendAsync(Publish("/hfp/v2/journey/ongoing/ars/bus/0018/00992", """{"ARS":{"veh":992}}"""), Ct);

        var record = await output.Reader.ReadAsync(Ct).AsTask().WaitAsync(_timeout, Ct);
        Assert.Equal("hsl-stop", record.FeedId);
        Assert.Equal("/hfp/v2/journey/ongoing/ars/bus/0018/00992", record.Topic);

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
    public async Task Refused_subscription_ends_the_run_with_an_error_naming_the_filter()
    {
        using var broker = new FakeBroker();
        var run = broker.CreateSource().RunAsync(Channel.CreateUnbounded<RawRecord>().Writer, Ct);

        using var client = await broker.AcceptAsync();
        await client.SendAsync(ConnAck(0), Ct);
        await client.SendAsync(new byte[] { 0x90, 0x03, 0x00, 0x01, 0x80 }, Ct);

        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(_timeout, Ct));
        Assert.Contains("refused the subscription to /hfp/v2/journey/ongoing/vp/#", error.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task Connection_reset_after_working_for_a_while_reconnects_at_once()
    {
        using var broker = new FakeBroker();
        var time = new FakeTimeProvider();
        var output = Channel.CreateUnbounded<RawRecord>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = broker.CreateSource(time).RunAsync(output.Writer, stop.Token);

        using (var first = await broker.AcceptAsync())
        {
            byte[] hello = [.. ConnAck(0), .. _subAck, .. Publish("/t", "a")];
            await first.SendAsync(hello, Ct);
            await output.Reader.ReadAsync(Ct).AsTask().WaitAsync(_timeout, Ct);
            time.Advance(TimeSpan.FromSeconds(11));

            // Linger 0 makes the close send a reset instead of FIN, as a broker or a network device dropping us does.
            first.LingerState = new LingerOption(true, 0);
        }

        // Without an immediate reconnect the source fails and nothing connects within the timeout.
        using var second = await broker.AcceptAsync();
        byte[] again = [.. ConnAck(0), .. _subAck, .. Publish("/t", "b")];
        await second.SendAsync(again, Ct);
        var record = await output.Reader.ReadAsync(Ct).AsTask().WaitAsync(_timeout, Ct);
        Assert.Equal("b", Encoding.UTF8.GetString(record.Payload));
        Assert.False(run.IsCompleted);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Each_connection_of_a_split_feed_subscribes_to_its_own_filters_under_the_feed_id()
    {
        using var broker = new FakeBroker();
        var output = Channel.CreateUnbounded<RawRecord>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = broker.CreateSource(connections: [["/vp/bus/#"], ["/vp/tram/#"]]).RunAsync(output.Writer, stop.Token);

        using var one = await broker.AcceptAsync();
        using var two = await broker.AcceptAsync();
        await one.SendAsync((byte[])[.. ConnAck(0), .. _subAck, .. Publish("/vp/one", "1")], Ct);
        await two.SendAsync((byte[])[.. ConnAck(0), .. _subAck, .. Publish("/vp/two", "2")], Ct);

        var records = new[]
        {
            await output.Reader.ReadAsync(Ct).AsTask().WaitAsync(_timeout, Ct),
            await output.Reader.ReadAsync(Ct).AsTask().WaitAsync(_timeout, Ct),
        };
        Assert.Equal(["/vp/bus/#", "/vp/tram/#"], broker.Subscriptions.Select(filters => Assert.Single(filters)).Order());
        Assert.Equal(["/vp/one", "/vp/two"], records.Select(record => record.Topic).Order());
        Assert.All(records, record => Assert.Equal("hsl", record.FeedId));

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task A_drop_after_working_for_a_while_reconnects_only_that_connection_of_a_split_feed()
    {
        using var broker = new FakeBroker();
        var time = new FakeTimeProvider();
        var output = Channel.CreateUnbounded<RawRecord>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = broker.CreateSource(time, connections: [["/vp/bus/#"], ["/vp/tram/#"]]).RunAsync(output.Writer, stop.Token);

        using var kept = await broker.AcceptAsync();
        using (var dropped = await broker.AcceptAsync())
        {
            await kept.SendAsync((byte[])[.. ConnAck(0), .. _subAck, .. Publish("/t", "a")], Ct);
            await dropped.SendAsync((byte[])[.. ConnAck(0), .. _subAck, .. Publish("/t", "b")], Ct);
            await output.Reader.ReadAsync(Ct).AsTask().WaitAsync(_timeout, Ct);
            await output.Reader.ReadAsync(Ct).AsTask().WaitAsync(_timeout, Ct);
            time.Advance(TimeSpan.FromSeconds(11));
            dropped.LingerState = new LingerOption(true, 0);
        }

        // Only the dropped connection comes back, with its own filter; the kept one still delivers.
        using var again = await broker.AcceptAsync();
        Assert.Equal(broker.Subscriptions[1], broker.Subscriptions[2]);
        await again.SendAsync((byte[])[.. ConnAck(0), .. _subAck, .. Publish("/t", "c")], Ct);
        await kept.SendAsync(Publish("/t", "d"), Ct);
        string[] payloads =
        [
            Encoding.UTF8.GetString((await output.Reader.ReadAsync(Ct).AsTask().WaitAsync(_timeout, Ct)).Payload),
            Encoding.UTF8.GetString((await output.Reader.ReadAsync(Ct).AsTask().WaitAsync(_timeout, Ct)).Payload),
        ];
        Assert.Equal(["c", "d"], payloads.Order());
        Assert.False(run.IsCompleted);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task One_connection_failing_to_set_up_closes_the_others_and_ends_the_run()
    {
        using var broker = new FakeBroker();
        var run = broker.CreateSource(connections: [["/vp/bus/#"], ["/vp/tram/#"]]).RunAsync(Channel.CreateUnbounded<RawRecord>().Writer, Ct);

        using var working = await broker.AcceptAsync();
        using var refused = await broker.AcceptAsync();
        await working.SendAsync((byte[])[.. ConnAck(0), .. _subAck], Ct);
        await refused.SendAsync(ConnAck(5), Ct);

        // The pipeline restarts the whole feed after its delay, so no connection may be left open behind it.
        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(_timeout, Ct));
        Assert.Contains("CONNACK code 5", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await working.ReceiveAsync(new byte[16], Ct).AsTask().WaitAsync(_timeout, Ct));
    }

    private const string VpTopic = "/hfp/v2/journey/ongoing/vp/#";

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

        /// <summary>The filters of each SUBSCRIBE read by <see cref="AcceptAsync"/>, in accept order.</summary>
        public List<string[]> Subscriptions { get; } = [];

        public HslMqttSource CreateSource(
            TimeProvider? time = null, string feedId = "hsl", string[]? topics = null, string[][]? connections = null)
        {
            var settings = new HslMqttOptions
            {
                FeedId = feedId,
                Topics = connections is null ? topics ?? [VpTopic] : [],
                Connections = [.. (connections ?? []).Select(filters => new HslMqttConnectionOptions { Topics = filters })],
                Host = IPAddress.Loopback.ToString(),
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
                ConnectTimeout = _timeout,
                SilenceTimeout = _timeout,
            };
            return new HslMqttSource(settings, time ?? TimeProvider.System, NullLogger<HslMqttSource>.Instance, OpenTcpAsync);
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

            Subscriptions.Add(SubscribedFilters(buffer.AsSpan(0, filled)));
            return client;
        }

        /// <summary>The filters of the SUBSCRIBE that follows CONNECT: packet id, then length-prefixed filter and QoS each.</summary>
        private static string[] SubscribedFilters(ReadOnlySpan<byte> data)
        {
            MqttWire.TryReadPacket(data, out _, out _, out _, out var connectLength);
            MqttWire.TryReadPacket(data[connectLength..], out _, out _, out var body, out _);
            var filters = new List<string>();
            for (var at = 2; at < body.Length; at += 3 + ((body[at] << 8) | body[at + 1]))
            {
                filters.Add(Encoding.UTF8.GetString(body.Slice(at + 2, (body[at] << 8) | body[at + 1])));
            }

            return [.. filters];
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
