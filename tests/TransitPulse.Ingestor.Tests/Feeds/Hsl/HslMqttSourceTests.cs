using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TransitPulse.Ingestor.Feeds.Hsl;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Tests.Feeds.Hsl;

public sealed class HslMqttSourceTests
{
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
}
