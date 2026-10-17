using System.Buffers;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MQTTnet;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Feeds.Hsl;

/// <summary>
/// HSL vehicle positions pushed over MQTT (about 1 message per vehicle per second). Every message is passed on
/// unchanged with its topic, which carries mode, route, direction and next stop.
/// A connection that drops after working for a while is re-opened at once, so a drop costs about a second of data;
/// failing to connect, or dropping right after connecting, ends the run and the pipeline retries after its delay.
/// </summary>
/// <param name="options">Broker and topic settings.</param>
/// <param name="timeProvider">Clock for receive times and the silence check.</param>
/// <param name="logger">Feed log.</param>
internal sealed partial class HslMqttSource(
    IOptions<HslMqttOptions> options,
    TimeProvider timeProvider,
    ILogger<HslMqttSource> logger) : IFeedSource
{
    private static readonly TimeSpan _disconnectTimeout = TimeSpan.FromSeconds(5);

    // Shorter-lived connections count as a failure, so a broker that keeps dropping us is not hammered in a loop.
    private static readonly TimeSpan _healthyConnection = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public string FeedId => "hsl";

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
        using var client = new MqttClientFactory().CreateMqttClient();

        var disconnected = new TaskCompletionSource<MqttClientDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DisconnectedAsync += e =>
        {
            disconnected.TrySetResult(e);
            return Task.CompletedTask;
        };

        var lastMessage = timeProvider.GetTimestamp();

        // Awaiting the bounded channel here holds MQTTnet's receive loop, so a slow sink slows reading from the socket.
        client.ApplicationMessageReceivedAsync += async e =>
        {
            Volatile.Write(ref lastMessage, timeProvider.GetTimestamp());
            var message = e.ApplicationMessage;
            var record = new RawRecord(FeedId, timeProvider.GetUtcNow(), message.Payload.ToArray(), message.Topic);
            try
            {
                await output.WriteAsync(record, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down: messages still arriving are dropped.
            }
        };

        // Clean session with a random id: HFP is QoS 0, so there is nothing for the broker to keep between connections.
        var clientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithTlsOptions(tls => tls.UseTls())
            .WithClientId($"transitpulse-{Guid.NewGuid():N}")
            .WithCleanSession()
            .Build();

        await client.ConnectAsync(clientOptions, cancellationToken);
        await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(settings.Topic).Build(), cancellationToken);
        LogSubscribed(settings.Host, settings.Topic);
        Volatile.Write(ref lastMessage, timeProvider.GetTimestamp());

        try
        {
            using var check = new PeriodicTimer(settings.SilenceTimeout / 6, timeProvider);
            while (true)
            {
                var tick = check.WaitForNextTickAsync(cancellationToken).AsTask();
                await Task.WhenAny(disconnected.Task, tick);
                if (disconnected.Task.IsCompleted)
                {
                    var lost = await disconnected.Task;
                    return $"disconnected ({lost.Reason}{(lost.Exception is null ? "" : $", {lost.Exception.Message}")})";
                }

                await tick; // throws on shutdown
                var silence = timeProvider.GetElapsedTime(Volatile.Read(ref lastMessage));
                if (silence > settings.SilenceTimeout)
                {
                    return $"no message for {silence:g}";
                }
            }
        }
        finally
        {
            if (client.IsConnected)
            {
                using var timeout = new CancellationTokenSource(_disconnectTimeout);
                await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), timeout.Token);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscribed to {Topic} on {Host}")]
    private partial void LogSubscribed(string host, string topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HSL MQTT connection ended after {Lasted}: {Reason}; reconnecting now")]
    private partial void LogReconnecting(string reason, TimeSpan lasted);
}
