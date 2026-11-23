using System.Diagnostics.Metrics;

namespace TransitPulse.Worker;

/// <summary>Counters and lag for the position sink (stage 3.2).</summary>
internal sealed class PositionSinkMetrics
{
    /// <summary>Meter name; the host exports the meter named after the application.</summary>
    public const string MeterName = "TransitPulse.Worker";

    private readonly Counter<long> _written;
    private readonly Counter<long> _thinned;
    private readonly Counter<long> _postgresErrors;
    private readonly Counter<long> _redisErrors;
    private readonly Histogram<double> _lagTime;

    /// <summary>Creates the instruments.</summary>
    public PositionSinkMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _written = meter.CreateCounter<long>(
            "transitpulse.worker.positions_written", "{position}", "Thinned positions written to Timescale.");
        _thinned = meter.CreateCounter<long>(
            "transitpulse.worker.positions_thinned", "{position}", "Enriched positions skipped by the 10 s thinner.");
        _postgresErrors = meter.CreateCounter<long>(
            "transitpulse.worker.positions_postgres_errors", "{error}", "Failed Timescale batch writes.");
        _redisErrors = meter.CreateCounter<long>(
            "transitpulse.worker.positions_redis_errors", "{error}", "Failed Redis GEO batch writes.");
        _lagTime = meter.CreateHistogram(
            "transitpulse.worker.positions_lag_time",
            "s",
            "Time from the Kafka message timestamp to sink consume.",
            null,
            new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60, 300] });
    }

    /// <summary>Records how many rows Timescale accepted in a flush.</summary>
    public void Written(int count)
    {
        if (count > 0)
        {
            _written.Add(count);
        }
    }

    /// <summary>Records one position skipped by thinning.</summary>
    public void Thinned() => _thinned.Add(1);

    /// <summary>Records a Postgres flush failure.</summary>
    public void PostgresError() => _postgresErrors.Add(1);

    /// <summary>Records a Redis flush failure.</summary>
    public void RedisError() => _redisErrors.Add(1);

    /// <summary>Records consumer lag when the broker stamped the message.</summary>
    public void Lag(double? lagSeconds)
    {
        if (lagSeconds is { } seconds and >= 0)
        {
            _lagTime.Record(seconds);
        }
    }
}
