using Microsoft.Extensions.Options;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Replay;

/// <summary>What to replay from the archive and how fast, configuration section <c>Replay</c>.</summary>
internal sealed class ReplayOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Replay";

    /// <summary>Fastest paced replay; the brief's load target is x10 (x50 for the heaviest test).</summary>
    public const double MaxSpeed = 50;

    /// <summary>Start of the replayed range, inclusive, compared with each record's receive time.</summary>
    public DateTimeOffset From { get; set; }

    /// <summary>End of the replayed range, exclusive.</summary>
    public DateTimeOffset To { get; set; }

    /// <summary>Feeds to replay, e.g. <c>hsl</c>, <c>entur-sky-vm</c>, <c>mbta-vp</c>.</summary>
    public List<string> Feeds { get; set; } = [];

    /// <summary>
    /// Pace relative to the recording: 1 is real time, 10 is ten times faster; 0 sends records as fast as the
    /// pipeline takes them, limited only by its bounded channel.
    /// </summary>
    public double Speed { get; set; } = 1;
}

/// <summary>Rejects a replay that would read nothing, read a feed twice or run at an unsupported pace.</summary>
internal sealed class ReplayOptionsValidator : IValidateOptions<ReplayOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ReplayOptions options)
    {
        var errors = new List<string>();
        if (options.To <= options.From)
        {
            errors.Add("Replay:To must be later than Replay:From.");
        }

        // Written as a range check so that NaN fails too.
        if (!(options.Speed >= 0 && options.Speed <= ReplayOptions.MaxSpeed))
        {
            errors.Add($"Replay:Speed must be 0 (as fast as possible) or up to {ReplayOptions.MaxSpeed}.");
        }

        if (options.Feeds.Count == 0)
        {
            errors.Add("Replay:Feeds must name at least one feed.");
        }

        errors.AddRange(options.Feeds.Where(feed => !FeedIds.IsValid(feed)).Select(feed => $"Replay:Feeds '{feed}' is invalid; use {FeedIds.Rule}."));

        // A feed listed twice would be read twice and every record replayed twice.
        errors.AddRange(options.Feeds.GroupBy(feed => feed, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => $"Replay:Feeds lists '{group.Key}' more than once."));

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
