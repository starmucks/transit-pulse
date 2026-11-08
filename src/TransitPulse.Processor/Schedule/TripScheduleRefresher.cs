using Microsoft.Extensions.Options;
using TransitPulse.Infrastructure.Gtfs;

namespace TransitPulse.Processor.Schedule;

/// <summary>
/// Keeps <see cref="CurrentTripSchedule"/> up to date: today ± 1 in the feed's time zone, read again when the day or the
/// active snapshot changes. Yesterday holds the night's trips of the previous service day, and tomorrow lets the change of
/// day go unnoticed for up to one interval without any position falling outside the schedule.
/// </summary>
/// <remarks>
/// A failed read never stops the stream: the old schedule stays, or positions go on as "no schedule" until a read succeeds.
/// </remarks>
/// <param name="reader">Reads schedules from the database.</param>
/// <param name="schedule">Receives each new schedule.</param>
/// <param name="options">Feed, time zone and interval.</param>
/// <param name="timeProvider">Clock for today and the interval.</param>
/// <param name="logger">Log for loads and failures.</param>
internal sealed partial class TripScheduleRefresher(
    TripScheduleReader reader,
    CurrentTripSchedule schedule,
    IOptions<TripScheduleOptions> options,
    TimeProvider timeProvider,
    ILogger<TripScheduleRefresher> logger) : BackgroundService
{
    private readonly TripScheduleOptions _options = options.Value;
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    // Completed when the first read ends, whatever its outcome.
    private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // What the current schedule was read for. Touched by one refresh at a time.
    private int? _loadedSnapshot;
    private DateOnly? _loadedToday;

    /// <inheritdoc />
    /// <remarks>
    /// Waits for the first read, at most <see cref="TripScheduleOptions.StartupWait"/>: hosted services start one after
    /// another, so the loop that starts next does not spend its first messages without a schedule, yet a slow database
    /// delays the stream by no more than that. A read still running goes on in the background.
    /// </remarks>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        try
        {
            await _firstRead.Task.WaitAsync(_options.StartupWait, timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            LogStillReading(_options.FeedId, _options.StartupWait);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RefreshAsync(stoppingToken);
        }
        finally
        {
            _firstRead.TrySetResult();
        }

        using var timer = new PeriodicTimer(_options.RefreshInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(stoppingToken);
        }
    }

    /// <summary>One check: reads the schedule if the day or the active snapshot changed. Failures are logged, never thrown.</summary>
    /// <param name="cancellationToken">Stops the read; this alone ends the method with an exception.</param>
    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var today = Today();
            var active = await reader.ActiveSnapshotAsync(_options.FeedId, cancellationToken);
            if (active is null)
            {
                LogNoSnapshot(_options.FeedId);
            }
            else if (active != _loadedSnapshot || today != _loadedToday)
            {
                await LoadAsync(today, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Database away, or the Worker has not created the schema yet: the next tick tries again.
            LogFailed(ex, _options.FeedId);
        }
    }

    private async Task LoadAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var start = timeProvider.GetTimestamp();
        var loaded = await reader.ReadAsync(_options.FeedId, [today.AddDays(-1), today, today.AddDays(1)], cancellationToken);
        if (loaded is null)
        {
            LogNoSnapshot(_options.FeedId);
            return;
        }

        schedule.Replace(loaded.Schedule);
        _loadedSnapshot = loaded.SnapshotId;
        _loadedToday = today;
        var elapsed = timeProvider.GetElapsedTime(start);
        LogLoaded(_options.FeedId, loaded.SnapshotId, today, loaded.Schedule.RunCount, elapsed);
    }

    private DateOnly Today() => _options.ServiceDate
        ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), _timeZone).DateTime);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule of {Feed}: snapshot {Snapshot}, {Today} ± 1 day, {Runs} runs, read in {Elapsed}")]
    private partial void LogLoaded(string feed, int snapshot, DateOnly today, int runs, TimeSpan elapsed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No active snapshot of {Feed} yet: positions go on without a trip")]
    private partial void LogNoSnapshot(string feed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Schedule of {Feed} still being read after {Wait}: positions start without a trip")]
    private partial void LogStillReading(string feed, TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the schedule of {Feed}; will try again")]
    private partial void LogFailed(Exception exception, string feed);
}
