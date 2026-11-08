using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using TransitPulse.Domain;

namespace TransitPulse.Processor.Tests;

public sealed class ProcessorMetricsTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    private IMeterFactory Factory => _services.GetRequiredService<IMeterFactory>();

    public void Dispose() => _services.Dispose();

    [Theory]
    [InlineData(nameof(MessageVerdict.Accepted), "accepted")]
    [InlineData(nameof(MessageVerdict.OutOfOrder), "out_of_order")]
    [InlineData(nameof(MessageVerdict.ImpossibleJump), "impossible_jump")]
    [InlineData(nameof(MessageVerdict.Unreadable), "unreadable")]
    public void Each_verdict_is_counted_under_its_own_name(string verdictName, string name)
    {
        var verdict = Enum.Parse<MessageVerdict>(verdictName);
        using var messages = new MetricCollector<long>(Factory, ProcessorMetrics.MeterName, "transitpulse.processor.messages");
        var metrics = new ProcessorMetrics(Factory);

        metrics.Message(verdict, null);

        var measurement = Assert.Single(messages.GetMeasurementSnapshot());
        Assert.Equal(1, measurement.Value);
        Assert.Equal(name, measurement.Tags["verdict"]);
        Assert.Equal(1, metrics.Total(verdict));
    }

    // The tags are arrays indexed by the enum: a value added without its tag would throw inside the loop and stop it.
    [Fact]
    public void Every_verdict_and_trip_result_has_a_tag_of_its_own()
    {
        using var messages = new MetricCollector<long>(Factory, ProcessorMetrics.MeterName, "transitpulse.processor.messages");
        using var trips = new MetricCollector<long>(Factory, ProcessorMetrics.MeterName, "transitpulse.processor.trips");
        var metrics = new ProcessorMetrics(Factory);
        var verdicts = Enum.GetValues<MessageVerdict>();
        var results = Enum.GetValues<TripResolution>();

        foreach (var verdict in verdicts)
        {
            metrics.Message(verdict, null);
        }

        foreach (var result in results)
        {
            metrics.Trip(result);
        }

        Assert.Equal(verdicts.Length, messages.GetMeasurementSnapshot().Select(m => m.Tags["verdict"]).Distinct().Count());
        Assert.Equal(results.Length, trips.GetMeasurementSnapshot().Select(m => m.Tags["result"]).Distinct().Count());
    }

    [Fact]
    public void Every_route_status_has_a_tag_and_only_placed_positions_record_an_offset()
    {
        using var routes = new MetricCollector<long>(Factory, ProcessorMetrics.MeterName, "transitpulse.processor.route");
        using var offsets = new MetricCollector<double>(Factory, ProcessorMetrics.MeterName, "transitpulse.processor.route_offset");
        var metrics = new ProcessorMetrics(Factory);
        var statuses = Enum.GetValues<RouteMatchStatus>();

        foreach (var status in statuses)
        {
            metrics.Route(new RouteMatch(status, 0, 12));
        }

        Assert.Equal(statuses.Length, routes.GetMeasurementSnapshot().Select(m => m.Tags["status"]).Distinct().Count());
        Assert.Equal([12.0, 12.0, 12.0], offsets.GetMeasurementSnapshot().Select(m => m.Value));
        Assert.Equal(1, metrics.Total(RouteMatchStatus.OffRoute));
        Assert.Equal(statuses.Length, metrics.RouteMatches);
    }

    [Theory]
    [InlineData(nameof(TripResolution.Found), "found")]
    [InlineData(nameof(TripResolution.NoDescriptor), "no_descriptor")]
    [InlineData(nameof(TripResolution.Malformed), "malformed")]
    [InlineData(nameof(TripResolution.DayNotLoaded), "day_not_loaded")]
    [InlineData(nameof(TripResolution.RouteUnknown), "route_unknown")]
    [InlineData(nameof(TripResolution.NoDeparture), "no_departure")]
    [InlineData(nameof(TripResolution.Ambiguous), "ambiguous")]
    [InlineData(nameof(TripResolution.NoSchedule), "no_schedule")]
    public void Each_trip_result_is_counted_under_its_own_name(string resultName, string name)
    {
        var result = Enum.Parse<TripResolution>(resultName);
        using var trips = new MetricCollector<long>(Factory, ProcessorMetrics.MeterName, "transitpulse.processor.trips");
        var metrics = new ProcessorMetrics(Factory);

        metrics.Trip(result);
        metrics.Trip(TripResolution.Found);

        Assert.Equal(name, trips.GetMeasurementSnapshot()[0].Tags["result"]);
        Assert.Equal(result == TripResolution.Found ? 2 : 1, metrics.Total(result));
        Assert.Equal(2, metrics.TripLookups);
    }

    [Fact]
    public void Lag_time_is_recorded_when_known_and_never_negative()
    {
        using var lagTime = new MetricCollector<double>(Factory, ProcessorMetrics.MeterName, "transitpulse.processor.lag_time");
        var metrics = new ProcessorMetrics(Factory);

        metrics.Message(MessageVerdict.Accepted, 1.5);
        metrics.Message(MessageVerdict.Accepted, -0.2);
        metrics.Message(MessageVerdict.Accepted, null);

        Assert.Equal([1.5, 0], lagTime.GetMeasurementSnapshot().Select(m => m.Value));
    }

    [Fact]
    public void Lag_shows_the_partitions_of_the_last_update_only()
    {
        using var lag = new MetricCollector<long>(Factory, ProcessorMetrics.MeterName, "transitpulse.processor.lag");
        var metrics = new ProcessorMetrics(Factory);

        metrics.SetLag([(0, 10), (3, 20)]);
        metrics.SetLag([(3, 5)]);
        lag.RecordObservableInstruments();

        var measurement = Assert.Single(lag.GetMeasurementSnapshot());
        Assert.Equal(5, measurement.Value);
        Assert.Equal(3, measurement.Tags["partition"]);
    }
}
