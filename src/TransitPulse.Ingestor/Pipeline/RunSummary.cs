using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace TransitPulse.Ingestor.Pipeline;

/// <summary>
/// Totals of a run, logged per feed when the host stops: records received, positions published, and positions and
/// records dropped by reason. Full and replay runs log the same lines, so a replay can be checked against the live
/// run it repeats. It listens to the exported counters, so the totals and the dashboards cannot disagree.
/// </summary>
/// <param name="meterFactory">The host's meter factory; only meters it created are counted.</param>
/// <param name="logger">Receives the totals.</param>
/// <remarks>
/// Register it before the pipeline: hosted services stop in reverse order, so it logs after the pipeline has drained.
/// </remarks>
internal sealed partial class RunSummary(IMeterFactory meterFactory, ILogger<RunSummary> logger) : IHostedService, IDisposable
{
    private const string Records = "transitpulse.feed.records";
    private const string Accepted = "transitpulse.positions.accepted";
    private const string Dropped = "transitpulse.positions.dropped";
    private const string Skipped = "transitpulse.positions.records_skipped";

    // Measurements arrive on whichever thread records them: the pipeline's reader and the publishing loop.
    private readonly ConcurrentDictionary<(string Feed, string Instrument, string Reason), StrongBox<long>> _totals = new();
    private MeterListener? _listener;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                // Only this process's meters: tests create meters of the same name side by side.
                if (ReferenceEquals(instrument.Meter.Scope, meterFactory) && instrument.Name is Records or Accepted or Dropped or Skipped)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Dispose();
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return Task.CompletedTask;
        }

        foreach (var feed in _totals.Keys.Select(key => key.Feed).Distinct().Order(StringComparer.Ordinal))
        {
            var records = Total(feed, Records);
            var positions = Total(feed, Accepted);
            var dropped = Reasons(feed, Dropped);
            var skipped = Reasons(feed, Skipped);
            LogTotals(feed, records, positions, dropped, skipped);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _listener?.Dispose();

    private void OnMeasurement(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        string? feed = null;
        var reason = "";
        foreach (var tag in tags)
        {
            switch (tag.Key)
            {
                case "feed.id":
                    feed = tag.Value as string;
                    break;
                case "reason":
                    reason = tag.Value as string ?? "";
                    break;
            }
        }

        if (feed is not null)
        {
            Interlocked.Add(ref _totals.GetOrAdd((feed, instrument.Name, reason), static _ => new StrongBox<long>()).Value, value);
        }
    }

    private long Total(string feed, string instrument) =>
        _totals.Where(total => total.Key.Feed == feed && total.Key.Instrument == instrument).Sum(total => total.Value.Value);

    // "duplicate 1897220, stale 12" ordered by reason, or "none".
    private string Reasons(string feed, string instrument)
    {
        var reasons = _totals.Where(total => total.Key.Feed == feed && total.Key.Instrument == instrument)
            .OrderBy(total => total.Key.Reason, StringComparer.Ordinal)
            .Select(total => string.Create(CultureInfo.InvariantCulture, $"{total.Key.Reason} {total.Value.Value}"))
            .ToList();
        return reasons.Count == 0 ? "none" : string.Join(", ", reasons);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Run totals {Feed}: {Records} records, {Positions} positions; dropped {Dropped}; skipped {Skipped}")]
    private partial void LogTotals(string feed, long records, long positions, string dropped, string skipped);
}
