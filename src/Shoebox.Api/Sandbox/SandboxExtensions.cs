public static class SandboxExtensions
{
    /// <summary>
    /// Telemetry for the Shoebox app itself.
    ///
    /// The simulated pods do NOT come through here. Each gets its own
    /// TracerProvider and its own Resource from
    /// <see cref="Shoebox.Api.Emit.PodTracerPool"/>, which is what lets one process
    /// present as many services. Sharing a single Resource was the old constraint
    /// that made one app permanently one pod.
    ///
    /// Everything is configured by the standard OTEL_EXPORTER_OTLP_* environment
    /// variables. No vendor name, no bespoke header and no product-specific setting
    /// appears anywhere in this file, so it runs against Jaeger, Tempo, Grafana,
    /// SigNoz, a Collector or anything else that speaks OTLP. The previous version
    /// hardcoded an Api-Key header from Otlp:ApiKey, which quietly locked the tool
    /// to one backend.
    /// </summary>
    public static void ConfigureOpenTelemetry(this WebApplicationBuilder builder)
    {
        var otlp = Shoebox.Api.Emit.PodTracerPool.OtlpConfigured;

        var resourceBuilder = ResourceBuilder.CreateDefault().AddService(
            "shoebox",
            typeof(Program).Namespace,
            (typeof(Program).Assembly?.GetName().Version ?? new Version(0, 1, 0)).ToString());

        builder.Services.AddLogging(options =>
        {
            options.ClearProviders();
            options.AddConsole();
            options.AddOpenTelemetry(loggerOptions =>
            {
                loggerOptions.SetResourceBuilder(resourceBuilder);
                if (otlp) loggerOptions.AddOtlpExporter();

                loggerOptions.IncludeFormattedMessage = true;
                loggerOptions.IncludeScopes = true;
                loggerOptions.ParseStateValues = true;
            });
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.SetResourceBuilder(resourceBuilder)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddProcessInstrumentation();
                if (otlp) metrics.AddOtlpExporter();
            })
            .WithTracing(tracing =>
            {
                tracing.SetResourceBuilder(resourceBuilder)
                    .AddSource(SandboxSources.DefaultActivitySource.Name)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
                if (otlp) tracing.AddOtlpExporter();
            });
    }
}
