using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// Same namespace as the host builder, the Aspire convention: AddServiceDefaults() is found without an extra using.
namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Cross-cutting setup shared by every Transit Pulse service: logs, metrics and traces through OpenTelemetry.
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>
    /// Adds OpenTelemetry logging, metrics and tracing. Telemetry is exported over OTLP only when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is configured, as Aspire does locally.
    /// </summary>
    /// <param name="builder">The service's host builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var telemetry = builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddMeter(builder.Environment.ApplicationName)
                .AddRuntimeInstrumentation()
                .AddHttpClientInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddHttpClientInstrumentation());

        // Without an endpoint (the collector server) there is nowhere to send telemetry: skip the exporter
        // instead of paying for retries against a missing receiver.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }

        return builder;
    }
}
