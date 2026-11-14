using System.Reflection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SmeDemo.Auth.Observability;

/// <summary>
/// Composition root for traces, metrics and structured console logging.
///
/// This mirrors <c>src/SmeDemo.Api/Observability/Telemetry.cs</c> because the
/// two hosts are independent deployments: auth has no project reference to the
/// API layers and must not gain one just to share a few composition lines.
///
/// The demo ships no collector, so exporters are opt-in: nothing is exported
/// unless an OTLP endpoint is configured, and the OTLP defaults are never used
/// to guess a local <c>localhost:4317</c> endpoint. Instrumentation itself is
/// always registered (it is cheap and makes the request id correlation work),
/// but with no exporter the collected signals are simply dropped.
/// </summary>
public static class Telemetry
{
    public const string ServiceName = "sme-demo-auth";

    /// <summary>
    /// Adds the OpenTelemetry tracing and metrics pipelines for this host.
    /// </summary>
    public static void AddSmeDemoTelemetry(this WebApplicationBuilder builder, string serviceName)
    {
        var otlpEndpoint = ReadSetting(builder, "OTEL_EXPORTER_OTLP_ENDPOINT");
        var otlpTracesEndpoint = ReadSetting(builder, "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT");
        var consoleExport = string.Equals(
            ReadSetting(builder, "OTEL_CONSOLE_EXPORT"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(CreateResource(builder, serviceName))
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        // Health probes run on a timer in orchestrated
                        // deployments; tracing them adds noise without telling
                        // you anything about a user request.
                        options.Filter = context => !IsHealthPath(context.Request.Path);
                    })
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation(options =>
                    {
                        // 1.21.0-beta.1 replaced SetDbStatementForText with
                        // mandatory query-text sanitization. This demo still
                        // must not emit SQL text at all, so strip both the old
                        // (db.statement) and new (db.query.text) attributes in
                        // the enrichment callback, which runs before export.
                        // Query parameters are off by default as well.
                        options.EnrichWithIDbCommand = (activity, _) =>
                        {
                            activity.SetTag("db.statement", null);
                            activity.SetTag("db.query.text", null);
                            activity.SetTag("db.stored_procedure.name", null);
                        };
                    })
                    // Kept in step with the API so a shared deployment recipe
                    // sees the same source list in both hosts.
                    .AddSource("Azure.Storage.Blobs")
                    .AddSource("Azure.Data.Tables");

                if (otlpTracesEndpoint is not null || otlpEndpoint is not null)
                {
                    // A traces-specific endpoint wins over the generic one,
                    // matching the OpenTelemetry environment-variable spec.
                    tracing.AddOtlpExporter(options =>
                        options.Endpoint = new Uri(otlpTracesEndpoint ?? otlpEndpoint!, UriKind.Absolute));
                }

                if (consoleExport)
                {
                    tracing.AddConsoleExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(CreateResource(builder, serviceName))
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                // Metrics only follow the generic endpoint: a
                // traces-specific URL must not silently send metrics to the
                // default localhost:4317 collector.
                if (otlpEndpoint is not null)
                {
                    metrics.AddOtlpExporter(options =>
                        options.Endpoint = new Uri(otlpEndpoint, UriKind.Absolute));
                }

                if (consoleExport)
                {
                    metrics.AddConsoleExporter();
                }
            });
    }

    /// <summary>
    /// Keeps the default simple console formatter unless <c>LOG_FORMAT</c> is
    /// <c>json</c>. Trace and span ids are tracked in both cases, so a JSON
    /// scope can be joined to the trace even when the console text is simple.
    /// </summary>
    public static void AddSmeDemoLogging(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<LoggerFactoryOptions>(options =>
        {
            options.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId;
        });

        var logFormat = ReadSetting(builder, "LOG_FORMAT");
        if (!string.Equals(logFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });
    }

    private static ResourceBuilder CreateResource(WebApplicationBuilder builder, string serviceName)
    {
        var version = typeof(Telemetry).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

        // SourceLink appends "+<sha>" to the informational version; a resource
        // attribute is not the place for build metadata.
        var metadataSeparator = version.IndexOf('+', StringComparison.Ordinal);
        if (metadataSeparator >= 0)
        {
            version = version[..metadataSeparator];
        }

        var instanceId = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            instanceId = Guid.NewGuid().ToString("N");
        }

        return ResourceBuilder.CreateDefault()
            .AddService(
                serviceName,
                serviceVersion: version,
                autoGenerateServiceInstanceId: false,
                serviceInstanceId: instanceId)
            .AddAttributes(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["deployment.environment.name"] = builder.Environment.EnvironmentName.ToLowerInvariant(),
            });
    }

    private static bool IsHealthPath(PathString path) =>
        path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/readyz", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a host setting (environment variables and test <c>UseSetting</c>
    /// values both land in configuration). Blank values count as unset.
    /// </summary>
    private static string? ReadSetting(WebApplicationBuilder builder, string name)
    {
        var value = builder.Configuration[name];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
