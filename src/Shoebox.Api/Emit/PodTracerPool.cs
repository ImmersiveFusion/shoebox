using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Shoebox.Api.Emit;

/// <summary>
/// One TracerProvider per simulated pod, each carrying its own Resource.
///
/// This mirrors Snowglobe's cmd/snowglobe/main.go, which is the reference
/// implementation for the family: a pool keyed by service name that appends one
/// provider per instance, with service.instance.id distinguishing replicas. That
/// tool runs the pattern at 28 services and 59 pods, so the cost of many
/// providers is answered by evidence rather than argument.
///
/// It is also what removes the old constraint. Sharing one Resource across the
/// whole process meant one ASP.NET app could only ever be one service. A Resource
/// per provider is exactly how one process presents as fifty pods.
/// </summary>
public sealed class PodTracerPool : IDisposable
{
    private readonly ConcurrentDictionary<string, ActivitySource> _sources = new();
    private readonly List<TracerProvider> _providers = new();
    private readonly object _gate = new();
    private readonly string _hostName = Environment.MachineName;

    /// <summary>
    /// Returns the ActivitySource for one pod, creating its provider on first use.
    /// Replicas of the same service share service.name and differ by
    /// service.instance.id, which is what OpenTelemetry defines that attribute for.
    /// </summary>
    public ActivitySource For(string serviceName, int instance)
    {
        var instanceId = $"{serviceName}-{instance}";
        return _sources.GetOrAdd(instanceId, id =>
        {
            // The ActivitySource name is the instance id so each pod's provider can
            // subscribe to exactly its own spans and nobody else's.
            var source = new ActivitySource(id);
            var provider = BuildProvider(serviceName, id);
            lock (_gate)
            {
                _providers.Add(provider);
            }

            return source;
        });
    }

    private TracerProvider BuildProvider(string serviceName, string instanceId)
    {
        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName)
            .AddAttributes(new KeyValuePair<string, object>[]
            {
                new("service.instance.id", instanceId),
                new("host.name", _hostName),
            });

        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(instanceId);

        // Vendor neutral by construction. The OTLP exporter is configured entirely
        // by the standard OTEL_EXPORTER_OTLP_* environment variables, the same way
        // sos-beacon does it, so pointing this at Jaeger, Tempo, Grafana, SigNoz, a
        // Collector or anything else is a matter of configuration and never code.
        //
        // Absent an endpoint the exporter is left off rather than defaulted to
        // localhost, so an unconfigured instance emits nothing instead of retrying
        // against a port nobody is listening on.
        if (OtlpConfigured)
        {
            builder.AddOtlpExporter();
        }

        return builder.Build();
    }

    /// <summary>
    /// True when a standard OTLP endpoint is configured. No product name appears
    /// anywhere in this decision, which is the point.
    /// </summary>
    public static bool OtlpConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"));

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var provider in _providers)
            {
                provider.Dispose();
            }

            _providers.Clear();
        }

        foreach (var source in _sources.Values)
        {
            source.Dispose();
        }

        _sources.Clear();
    }
}
