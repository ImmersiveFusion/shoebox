public static class SandboxExtensions
{
    public static void ConfigureOpenTelemetry(this WebApplicationBuilder webApplicationBuilder)
    {
        var resourceBuilder = ResourceBuilder.CreateDefault().AddService("api", typeof(Program).Namespace,
            (typeof(Program).Assembly?.GetName().Version ?? new Version(0, 1, 0)).ToString());

        void ConfigureExporter(OtlpExporterOptions otlpOptions)
        {
            otlpOptions.Endpoint = new Uri(webApplicationBuilder.Configuration.GetValue<string>("Otlp:Endpoint")!);
            otlpOptions.Headers = $"Api-Key={webApplicationBuilder.Configuration.GetValue<string>("Otlp:ApiKey")}";
        }

        webApplicationBuilder.Services.AddLogging(options =>
        {
            options.ClearProviders();
            options.AddConsole();
            options.AddOpenTelemetry(loggerOptions =>
            {
                loggerOptions
                    .SetResourceBuilder(resourceBuilder)
                    .AddOtlpExporter(ConfigureExporter)
                    .AddConsoleExporter()
                    ;

                loggerOptions.IncludeFormattedMessage = true;
                loggerOptions.IncludeScopes = true;
                loggerOptions.ParseStateValues = true;
            });
        });

        webApplicationBuilder.Services.AddOpenTelemetry()
            .WithMetrics(meterProviderBuilder => meterProviderBuilder
                .SetResourceBuilder(resourceBuilder)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddProcessInstrumentation()
                .AddOtlpExporter(ConfigureExporter))
            .WithTracing(tracerProviderBuilder =>
            {
                tracerProviderBuilder
                .SetResourceBuilder(resourceBuilder)
                .AddSource(SandboxSources.DefaultActivitySource.Name);

                // Register all saga service sources so their spans are exported
                foreach (var sourceName in SandboxSources.SagaServiceSourceNames)
                {
                    tracerProviderBuilder.AddSource(sourceName);
                }

                tracerProviderBuilder
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSqlClientInstrumentation(options =>
                {
                    options.RecordException = true;
                    // db.query.text, db.query.summary and db.stored_procedure.name are
                    // emitted by default since the SqlClient instrumentation stabilized.
                    // The SetDbStatementFor* opt-ins were removed because the attributes
                    // they gated are now standard.
                })
                .AddRedisInstrumentation()
                .AddOtlpExporter(ConfigureExporter)
                .AddConsoleExporter();
            });
    }
}