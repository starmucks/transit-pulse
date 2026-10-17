using System.Threading.Channels;

namespace TransitPulse.Ingestor.Pipeline;

/// <summary>
/// A transit data feed that delivers raw messages, whether it pushes them (MQTT) or is polled (HTTP).
/// </summary>
public interface IFeedSource
{
    /// <summary>Stable feed identifier, written into every <see cref="RawRecord"/>.</summary>
    string FeedId { get; }

    /// <summary>
    /// Receives messages and writes them to <paramref name="output"/> until <paramref name="cancellationToken"/> is cancelled.
    /// Writes must pass the token: when the channel is full they wait, and cancellation is what releases them on shutdown.
    /// Throwing ends this run; the pipeline logs the error and starts the source again after a delay.
    /// </summary>
    /// <param name="output">Bounded channel shared by all feeds.</param>
    /// <param name="cancellationToken">Signals shutdown.</param>
    /// <returns>A task that completes when the source stops.</returns>
    Task RunAsync(ChannelWriter<RawRecord> output, CancellationToken cancellationToken);
}
