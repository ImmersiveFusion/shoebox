using System.Net;
using Shoebox.Api.Emit;
using Shoebox.Api.Run;
using Shoebox.Api.Topology;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddCors();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<SandboxMiddleware>();
builder.Services.AddHttpContextAccessor();

// The pool is a singleton because a TracerProvider owns an exporter and a batch
// queue. Snowglobe runs this same pattern at 28 services and 59 pods, so the cost
// of many providers is settled by evidence rather than argument.
builder.Services.AddSingleton<PodTracerPool>();
builder.Services.AddSingleton<TopologyRunner>();

builder.Services.AddSpaStaticFiles(configuration =>
{
    configuration.RootPath = "wwwroot";
});

builder.ConfigureOpenTelemetry();

var app = builder.Build();

// Whether a visitor may name their own OTLP destination. On where the operator is
// the visitor, off on anything public unless SHOEBOX_ALLOW_CLIENT_OTLP says
// otherwise. See OtlpTarget.ClientTargetsAllowed for why a hosted instance is a
// different question from a local one.
var isDevelopment = app.Environment.IsDevelopment();
var clientTargetsAllowed = OtlpTarget.ClientTargetsAllowed(isDevelopment);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseCors(options =>
    {
        options.AllowAnyOrigin();
        options.AllowAnyMethod();
        options.AllowAnyHeader();
    });
}
else
{
    app.UseSpaStaticFiles();
}

app.UseHttpsRedirection();
app.UseMiddleware<SandboxMiddleware>();

app.UseExceptionHandler(options =>
{
    options.Run(async context =>
    {
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "unhandled error" });
    });
});

// Every visitor gets an isolated sandbox keyed by a GUID. This is what makes a
// public, no-account, no-install tool sustainable: one shared instance serves
// everyone, isolated logically rather than by provisioning. It survives the
// rewrite unchanged in principle.
app.MapPost("/sandbox", () => Results.Ok(new { sandboxId = Guid.NewGuid().ToString("N") }))
   .WithName("CreateSandbox");

// Parse only. Lets the UI render and report problems without emitting anything,
// because nothing moves until the user presses fire.
app.MapPost("/topology/parse", (DiagramRequest request) =>
{
    var graph = MermaidParser.Parse(request.Diagram);
    return Results.Ok(new
    {
        pods = graph.Pods.Select(p => new
        {
            id = p.Id,
            label = p.Label,
            serviceName = p.ServiceName,
            kind = p.Kind.ToString().ToLowerInvariant(),
            replicas = p.Replicas,
            pinnedInstance = p.PinnedInstance,
        }),
        calls = graph.Calls.Select(c => new
        {
            from = c.FromId,
            to = c.ToId,
            broken = c.Broken,
            brokenInstances = c.BrokenInstances,
            reason = c.FailureReason,
        }),
        entry = graph.Entry?.Id,
        notes = graph.Notes,
    });
}).WithName("ParseTopology");

// Fire exactly one request through the diagram. The user holds the trigger and
// nothing moves otherwise, which is why the lesson works: you fired one request,
// you know its path, and you know what you broke.
app.MapPost("/run", (RunRequest request, TopologyRunner runner, HttpRequest http) =>
{
    var graph = MermaidParser.Parse(request.Diagram);
    var runIndex = request.RunIndex <= 0 ? 1 : request.RunIndex;
    var sandboxId = http.GetSandboxId();

    // A run can bring its own destination, the way Snowglobe takes -endpoint and
    // -headers. Ignored outright when the operator has not allowed it, rather than
    // half-honored: a request that quietly went somewhere other than where it said
    // would be worse than one that plainly did not.
    var wantsOwnTarget = !string.IsNullOrWhiteSpace(request.Endpoint) || !string.IsNullOrWhiteSpace(request.Headers);
    if (wantsOwnTarget && clientTargetsAllowed)
    {
        var target = OtlpTarget.Resolve(request.Endpoint, request.Headers, out var parseError);
        if (target is null)
        {
            return Results.BadRequest(new { error = parseError ?? "endpoint could not be resolved" });
        }

        if (!OtlpTarget.IsReachableTarget(target.Endpoint, isDevelopment, out var reachError))
        {
            return Results.BadRequest(new { error = reachError });
        }

        // Disposing is what flushes, so the spans are gone before the response is,
        // which is why a person sees them land while still looking at the page.
        using var scope = PodTracerPool.ScopeFor(target);
        return Results.Ok(runner.Run(graph, runIndex, sandboxId, scope));
    }

    return Results.Ok(runner.Run(graph, runIndex, sandboxId));
}).WithName("Run");

// Says whether telemetry is actually going anywhere, so a user who sees no traces
// can tell an unconfigured endpoint from a broken diagram, and whether they are
// allowed to name their own destination.
app.MapGet("/otlp/status", () =>
{
    var target = PodTracerPool.Resolve();
    return Results.Ok(new
    {
        configured = target is not null,
        endpoint = target?.Endpoint.ToString(),
        clientConfigurable = clientTargetsAllowed,
        hint = "set OTEL_EXPORTER_OTLP_ENDPOINT to any OTLP backend: Jaeger, Tempo, Grafana, SigNoz, or a Collector",
    });
}).WithName("OtlpStatus");

app.MapFallbackToFile("index.html");

app.Run();

public record DiagramRequest(string Diagram);

/// <summary>
/// Endpoint and Headers are the same two knobs Snowglobe exposes as -endpoint and
/// -headers, in the same formats: a URL (or a bare host:port) and
/// "key=value,key2=value2". Omitted means the server's own target, which is the only
/// behaviour there has ever been.
/// </summary>
public record RunRequest(string Diagram, int RunIndex, string? Endpoint = null, string? Headers = null);
