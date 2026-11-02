using System.Globalization;
using Microsoft.Extensions.Options;
using TransitPulse.Ingestor.Pipeline;

namespace TransitPulse.Ingestor.Replay;

/// <summary>Registers replay mode's only feed source, <see cref="ReplaySource"/>, and its settings.</summary>
internal static class ReplayRegistration
{
    /// <summary>Adds the replay settings, validated at startup, and the replay source.</summary>
    /// <param name="services">Service collection to add to.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddReplay(this IServiceCollection services)
    {
        // At startup, not when the pipeline starts the source: a typo in a feed id would otherwise look like an empty archive.
        services.AddOptions<ReplayOptions>().BindConfiguration(ReplayOptions.SectionName)
            .PostConfigure<IConfiguration>((options, configuration) =>
            {
                // The binder reads a time without an offset in the machine's time zone, so the same settings would
                // replay different hours on the development PC and on the server. Without an offset, it is UTC.
                var section = configuration.GetSection(ReplayOptions.SectionName);
                if (section[nameof(ReplayOptions.From)] is { Length: > 0 } from)
                {
                    options.From = ParseUtc(from);
                }

                if (section[nameof(ReplayOptions.To)] is { Length: > 0 } to)
                {
                    options.To = ParseUtc(to);
                }
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ReplayOptions>, ReplayOptionsValidator>();
        services.AddSingleton<ReplaySource>();
        return services.AddSingleton<IFeedSource>(provider => provider.GetRequiredService<ReplaySource>());
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
