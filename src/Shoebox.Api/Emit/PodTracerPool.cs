using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Shoebox.Api.Topology;

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
    /// <summary>
    /// One entry per pod: its source and the provider that exports it, built once.
    ///
    /// Lazy, because <c>GetOrAdd</c> may run its factory more than once under a race
    /// and keep only one result; building the provider inside the factory leaked the
    /// loser, a whole TracerProvider with its exporter. The Lazy is cheap to lose,
    /// and only the one that is kept is ever built.
    /// </summary>
    private readonly ConcurrentDictionary<(string Service, string InstanceId), Lazy<PodEmitter>> _pods = new();
    private readonly string _hostName = Environment.MachineName;

    private sealed record PodEmitter(ActivitySource Source, TracerProvider Provider);
    private readonly OtlpTarget? _target;

    public PodTracerPool(OtlpTarget? target) => _target = target;

    /// <summary>Where this instance exports, or null when it is not configured to.</summary>
    public OtlpTarget? Target => _target;

    /// <summary>
    /// Returns the ActivitySource for one pod, creating its provider on first use.
    /// Replicas of the same service share service.name and differ by
    /// service.instance.id, which is what OpenTelemetry defines that attribute for.
    /// </summary>
    public ActivitySource For(string serviceName, int instance, int? revision = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = (serviceName, Pod.InstanceIdOf(serviceName, instance, revision));
        var pod = _pods.GetOrAdd(key, k => new Lazy<PodEmitter>(() =>
        {
            // The ActivitySource name is the instance id so each pod's provider can
            // subscribe to exactly its own spans and nobody else's. A pod name ends in
            // two fixed-length parts after the service, so two services can never
            // produce the same one; the key carries the service anyway, so that a
            // future change to the name format cannot quietly merge two pods.
            //
            // The revision is not in the key because it is already in the instance
            // id: the template hash is drawn from it, so two revisions of a service
            // are two pods, each with its own service.version.
            var source = new ActivitySource(k.Item2);
            try
            {
                return new PodEmitter(source, BuildProvider(k.Item1, k.Item2, source.Name, revision));
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return pod.Value.Source;
        }
        catch
        {
            // A Lazy keeps the exception it was built with, so a pod that failed to
            // build once would fail on every run after. Drop it, so the next run
            // builds it again, the way the factory did before it was made Lazy.
            _pods.TryRemove(new KeyValuePair<(string, string), Lazy<PodEmitter>>(key, pod));
            throw;
        }
    }

    private volatile bool _disposed;

    /// <summary>
    /// The Resource one pod exports under, or null when that pod has not emitted yet.
    /// Resource attributes (service.name, service.version, service.instance.id) ride
    /// the export, not the span, so this is the only place they can be read back.
    /// It is read from the pod's provider (<c>GetResource()</c>), the Resource its
    /// exporter attaches to every batch; nothing is kept for it.
    /// </summary>
    internal Resource? ResourceOf(string serviceName, int instance, int? revision = null)
    {
        var instanceId = Pod.InstanceIdOf(serviceName, instance, revision);
        return _pods.TryGetValue((serviceName, instanceId), out var pod) && pod.IsValueCreated
            ? pod.Value.Provider.GetResource()
            : null;
    }

    private TracerProvider BuildProvider(string serviceName, string instanceId, string sourceName, int? revision)
    {
        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName, serviceVersion: Pod.VersionOf(revision))
            .AddAttributes(new KeyValuePair<string, object>[]
            {
                new("service.instance.id", instanceId),
                new("host.name", _hostName),
            });

        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .SetSampler(new DeclaredFullSampler())
            .AddSource(sourceName);

        // Vendor neutral by construction. Endpoint and headers are the standard OTLP
        // ones, resolved from configuration the way Snowglobe resolves its flags, so
        // pointing this at Jaeger, Tempo, Grafana, SigNoz, a Collector or anything
        // else is a matter of configuration and never code.
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

    public void Dispose()
    {
        // No new pods after this; a For racing it throws rather than building a
        // provider nothing will dispose.
        _disposed = true;

        // Providers first, so each flushes what its source emitted, then the sources.
        foreach (var pod in _pods.Values.Where(p => p.IsValueCreated))
        {
            pod.Value.Provider.Dispose();
        }

        foreach (var pod in _pods.Values.Where(p => p.IsValueCreated))
        {
            pod.Value.Source.Dispose();
        }

        _pods.Clear();
    }
}
