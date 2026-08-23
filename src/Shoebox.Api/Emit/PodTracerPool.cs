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
    private readonly PodScope _default = new(Resolve(), scopeSuffix: null);

    /// <summary>
    /// The ActivitySource for one pod on the server's own target, created on first
    /// use and kept. Replicas of the same service share service.name and differ by
    /// service.instance.id, which is what OpenTelemetry defines that attribute for.
    /// </summary>
    public ActivitySource For(string serviceName, int instance) => _default.For(serviceName, instance);

    /// <summary>
    /// A short-lived set of providers pointing somewhere else, for one run.
    ///
    /// Deliberately not cached. A visitor-supplied endpoint is attacker-controlled
    /// input, so a cache keyed by it is an unbounded cache keyed by a stranger, which
    /// is a memory leak with extra steps in a service that takes no signup. Building
    /// per run costs a handful of providers per click, and disposing flushes them,
    /// which also means the traces land while the person is still looking.
    /// </summary>
    public static PodScope ScopeFor(OtlpTarget target) =>
        new(target, scopeSuffix: Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// The server's own target, if it has one. Null leaves the exporter off rather
    /// than defaulting to localhost, so an unconfigured instance emits nothing
    /// instead of retrying against a port nobody is listening on.
    /// </summary>
    public static OtlpTarget? Resolve() => OtlpTarget.Resolve(null, null, out _);

    /// <summary>
    /// True when the server itself has an OTLP endpoint. No product name appears
    /// anywhere in this decision, which is the point.
    /// </summary>
    public static bool OtlpConfigured => Resolve() is not null;

    public void Dispose() => _default.Dispose();
}

/// <summary>
/// The providers and sources for one export target. The default one lives as long as
/// the process; a visitor-supplied one lives for a single run.
/// </summary>
public sealed class PodScope : IDisposable
{
    private readonly ConcurrentDictionary<string, ActivitySource> _sources = new();
    private readonly List<TracerProvider> _providers = new();
    private readonly object _gate = new();
    private readonly string _hostName = Environment.MachineName;
    private readonly OtlpTarget? _target;

    /// <summary>
    /// Appended to the ActivitySource name so two scopes cannot subscribe to each
    /// other's spans: OpenTelemetry matches sources by exact name, so without this a
    /// run aimed at somebody's own Collector would also export through the server's
    /// provider. It surfaces in telemetry as otel.scope.name, and only on runs that
    /// carried their own endpoint. The default scope leaves it off, so the ordinary
    /// case stays clean.
    /// </summary>
    private readonly string? _scopeSuffix;

    internal PodScope(OtlpTarget? target, string? scopeSuffix)
    {
        _target = target;
        _scopeSuffix = scopeSuffix;
    }

    public ActivitySource For(string serviceName, int instance)
    {
        var instanceId = $"{serviceName}-{instance}";
        var sourceName = _scopeSuffix is null ? instanceId : $"{instanceId}#{_scopeSuffix}";

        return _sources.GetOrAdd(sourceName, name =>
        {
            var source = new ActivitySource(name);
            var provider = BuildProvider(serviceName, instanceId, name);
            lock (_gate)
            {
                _providers.Add(provider);
            }

            return source;
        });
    }

    private TracerProvider BuildProvider(string serviceName, string instanceId, string sourceName)
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
            .AddSource(sourceName);

        // Vendor neutral by construction. Endpoint and headers are the standard OTLP
        // ones, resolved the way Snowglobe resolves them, so pointing this at Jaeger,
        // Tempo, Grafana, SigNoz, a Collector or anything else is a matter of
        // configuration and never code.
        if (_target is not null)
        {
            builder.AddOtlpExporter(options =>
            {
                options.Endpoint = _target.Endpoint;
                if (_target.Headers.Length > 0) options.Headers = _target.Headers;
            });
        }

        return builder.Build();
    }

    /// <summary>
    /// Flushes before disposing, so a run that owns its providers actually exports
    /// rather than dropping a batch queue on the floor.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var provider in _providers)
            {
                try
                {
                    provider.ForceFlush(5000);
                }
                catch
                {
                    // An unreachable endpoint must not take the request down with it.
                    // The run already happened; the export is best effort.
                }

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
