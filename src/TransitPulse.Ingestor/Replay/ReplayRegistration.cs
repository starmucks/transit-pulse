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
        services.AddOptions<ReplayOptions>().BindConfiguration(ReplayOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ReplayOptions>, ReplayOptionsValidator>();
        services.AddSingleton<ReplaySource>();
        return services.AddSingleton<IFeedSource>(provider => provider.GetRequiredService<ReplaySource>());
    }
}
