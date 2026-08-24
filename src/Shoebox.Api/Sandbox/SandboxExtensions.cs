public static class SandboxExtensions
{
    /// <summary>
    /// Console logging, and nothing else.
    ///
    /// Shoebox emits no telemetry of its own. It used to: an AspNetCore server
    /// span for POST /run, HttpClient spans, runtime and process metrics, all
    /// under a resource called "shoebox". Every one of those went to the same
    /// endpoint as the simulation, so a person who pasted a four service diagram
    /// and looked at their backend found five services, and the extra one was the
    /// tool they were using.
    ///
    /// That is worse than clutter in something whose entire job is teaching
    /// people to read a trace. The diagram is supposed to be the whole state, and
    /// that has to be true of the telemetry too or the lesson has a lie in it.
    ///
    /// So the only OTLP that leaves this process now comes from
    /// <see cref="Shoebox.Api.Emit.PodTracerPool"/>, one provider per simulated
    /// pod, each with its own Resource. Operating this thing is a job for logs and
    /// an HTTP status code.
    /// </summary>
    public static void ConfigureOpenTelemetry(this WebApplicationBuilder builder, Shoebox.Api.Emit.OtlpTarget? target)
    {
        builder.Services.AddLogging(options =>
        {
            options.ClearProviders();
            options.AddConsole();
        });
    }
}
