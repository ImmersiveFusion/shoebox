using System.Net;
using System.Threading.RateLimiting;
using Shoebox.Api.Emit;
using Shoebox.Api.Run;
using Shoebox.Api.Share;
using Shoebox.Api.Topology;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddCors();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<ShoeboxMiddleware>();
builder.Services.AddHttpContextAccessor();

// ── Throttling ───────────────────────────────────────────────────────────────
//
// Two layers, because the obvious one is forgeable. A shoebox id is minted by
// asking for one, so a limit keyed on it alone is evaded by asking again; the
// second layer keys on where the request came from, which is what actually costs
// an abuser something. Minting itself is limited for the same reason.
//
// Neither is abuse defence. A distributed slam is a job for the edge, and this
// runs inside the app. What these stop is the ordinary way a public tool falls
// over now: an agent in a loop, firing because nothing told it not to. The
// instruction set at /llms.txt states these numbers, and a 429 restates them, so
// a well-behaved caller can pace itself instead of discovering the limit by
// hitting it.
//
// One run per sender per five minutes, sustained -- but five in a row, because
// replica selection is deterministic round robin on runIndex and the documented
// way to see "broken on #3" of five is to fire five times. A strict one-in-five-
// minutes would turn that lesson into a twenty-five minute errand. Change
// RunBurst to 1 if the sustained rate should also be the instantaneous one.
var runPeriod = TimeSpan.FromMinutes(5);
const int RunBurst = 5;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = (int)HttpStatusCode.TooManyRequests;

    // A 429 that only says "429" teaches nothing. This one says how long to wait,
    // in a header for machines and a body for whoever is reading the response.
    options.OnRejected = async (context, token) =>
    {
        var wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? retryAfter
            : runPeriod;

        context.HttpContext.Response.Headers.RetryAfter =
            ((int)Math.Ceiling(wait.TotalSeconds)).ToString();

        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            error = "slow down",
            retryAfterSeconds = (int)Math.Ceiling(wait.TotalSeconds),
            limits = new
            {
                run = $"{RunBurst} in a row, then one every {runPeriod.TotalMinutes:0} minutes, per shoebox",
                source = "twice that from one address, however many shoeboxes it mints",
                shoebox = $"{RunBurst} new shoeboxes, then one every {runPeriod.TotalMinutes:0} minutes, per address",
                parse = "60 a minute per address, and it emits nothing",
            },
            hint = "https://shoebox.deepcube.ai/llms.txt explains the pacing, and /topology/parse is free",
        }, token);
    };

    // Chained, not four separate endpoint policies. RequireRateLimiting does not
    // stack: calling it twice on one endpoint replaces the first policy rather than
    // applying both, which was measured here -- a shoebox got six runs out of a
    // five-token bucket because only the second policy was live. A chained global
    // limiter is the shape that actually applies more than one rule to a request.
    //
    // Every rule opts out by path, so the SPA and everything else pass through
    // untouched. Static files are served earlier in the pipeline anyway and never
    // reach this.
    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(

        // Per sender. Falls back to the source when no shoebox id was sent, so a
        // caller cannot dodge this one by simply omitting the parameter.
        PartitionedRateLimiter.Create<HttpContext, string>(http =>
            IsPostTo(http, "/run")
                ? RateLimitPartition.GetTokenBucketLimiter(
                    http.Request.GetShoeboxId() is { Length: > 0 } id ? $"shoebox:{id}" : SourceKey(http),
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = RunBurst,
                        TokensPerPeriod = 1,
                        ReplenishmentPeriod = runPeriod,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    })
                : RateLimitPartition.GetNoLimiter<string>("not-a-run")),

        // Per source, across every shoebox it holds. Twice the sender allowance, so
        // two colleagues behind one office address are not fighting each other, and
        // minting a fresh shoebox per run buys one more allowance rather than an
        // unlimited supply of them.
        PartitionedRateLimiter.Create<HttpContext, string>(http =>
            IsPostTo(http, "/run")
                ? RateLimitPartition.GetTokenBucketLimiter(
                    SourceKey(http),
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = RunBurst * 2,
                        TokensPerPeriod = 2,
                        ReplenishmentPeriod = runPeriod,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    })
                : RateLimitPartition.GetNoLimiter<string>("not-a-run")),

        // Minting. The lever that makes the per-sender limit mean anything.
        PartitionedRateLimiter.Create<HttpContext, string>(http =>
            IsPostTo(http, "/shoebox")
                ? RateLimitPartition.GetTokenBucketLimiter(
                    SourceKey(http),
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = RunBurst,
                        TokensPerPeriod = 1,
                        ReplenishmentPeriod = runPeriod,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    })
                : RateLimitPartition.GetNoLimiter<string>("not-a-mint")),

        // Parsing emits nothing and costs a regex pass, so it stays generous: it is
        // the endpoint a careful caller uses to check itself before firing, and
        // punishing that would teach exactly the wrong habit.
        PartitionedRateLimiter.Create<HttpContext, string>(http =>
            IsPostTo(http, "/topology/parse") || IsPostTo(http, "/share")
                ? RateLimitPartition.GetFixedWindowLimiter(
                    SourceKey(http),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    })
                : RateLimitPartition.GetNoLimiter<string>("not-a-parse")));
});

// Where a request came from, as a partition key. X-Forwarded-For first, because
// behind App Service the connection address is the load balancer and keying on it
// would put every visitor in one bucket. The port has to come off: App Service
// appends one and it changes per connection, so keeping it would give every
// request its own partition and silently disable the limit.
static bool IsPostTo(HttpContext http, string path) =>
    HttpMethods.IsPost(http.Request.Method)
    && http.Request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase);

static string SourceKey(HttpContext http)
{
    var forwarded = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    var candidate = string.IsNullOrWhiteSpace(forwarded)
        ? http.Connection.RemoteIpAddress?.ToString()
        : forwarded.Split(',')[0].Trim();

    if (string.IsNullOrWhiteSpace(candidate)) return "source:unknown";

    if (IPEndPoint.TryParse(candidate, out var endpoint)) return $"source:{endpoint.Address}";

    return $"source:{candidate}";
}

// Where this instance exports, decided once at startup from configuration. A run
// never carries a destination: the operator sets it, exactly as the operator sets
// Snowglobe's -endpoint and -headers.
var otlpTarget = OtlpTarget.FromConfiguration(builder.Configuration, out var otlpConfigError);
if (otlpConfigError is not null)
{
    // A misconfigured endpoint is refused loudly at startup rather than quietly
    // exporting nowhere for the life of the deployment.
    throw new InvalidOperationException($"Otlp:Endpoint is not usable: {otlpConfigError}");
}

// The pool is a singleton because a TracerProvider owns an exporter and a batch
// queue. Snowglobe runs this same pattern at 28 services and 59 pods, so the cost
// of many providers is settled by evidence rather than argument.
builder.Services.AddSingleton(new PodTracerPool(otlpTarget));
builder.Services.AddSingleton<TopologyRunner>();

builder.Services.AddSpaStaticFiles(configuration =>
{
    configuration.RootPath = "wwwroot";
});

builder.ConfigureOpenTelemetry(otlpTarget);

var app = builder.Build();

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
app.UseRateLimiter();
app.UseMiddleware<ShoeboxMiddleware>();

app.UseExceptionHandler(options =>
{
    options.Run(async context =>
    {
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "unhandled error" });
    });
});

// Every visitor gets an isolated shoebox keyed by a GUID. This is what makes a
// public, no-account, no-install tool sustainable: one shared instance serves
// everyone, isolated logically rather than by provisioning. It survives the
// rewrite unchanged in principle.
app.MapPost("/shoebox", () => Results.Ok(new { shoeboxId = Guid.NewGuid().ToString("N") }))
   .WithName("CreateShoebox");

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

        // Cycles belong here more than anywhere else. This is the endpoint the
        // agent contract sends people to before firing something they did not
        // write themselves, and it was returning an empty notes array on diagrams
        // that go on to emit tens of thousands of spans.
        notes = graph.Notes.Concat(graph.CycleNotes),
        cyclicPods = graph.CyclicPods,
    });
}).WithName("ParseTopology");

// Fire exactly one request through the diagram. The user holds the trigger and
// nothing moves otherwise, which is why the lesson works: you fired one request,
// you know its path, and you know what you broke.
app.MapPost("/run", (RunRequest request, TopologyRunner runner, HttpRequest http) =>
{
    var graph = MermaidParser.Parse(request.Diagram);
    var result = runner.Run(graph, request.RunIndex <= 0 ? 1 : request.RunIndex, http.GetShoeboxId());
    return Results.Ok(result);
}).WithName("Run");

// A link somebody can click, because the callers who most need one cannot build it: the fragment is
// deflate-raw then base64url, and a model cannot deflate. Emits nothing, so it is paced with /parse
// rather than with /run.
//
// The origin comes from the request rather than configuration, so a link handed out by a local
// instance points at that instance. X-Forwarded-Proto first: behind App Service the inbound scheme
// is http and a link built from it would hand somebody an http URL for an https site.
app.MapPost("/share", (ShareRequest request, HttpRequest http) =>
{
    var scheme = http.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? http.Scheme;
    var url = ShareLink.For($"{scheme}://{http.Host}", request.Diagram, request.ShoeboxId);

    return Results.Ok(new
    {
        url,
        tooLongForSomeClients = url.Length > ShareLink.LengthWarning,
        hint = url.Length > ShareLink.LengthWarning
            ? "over ~8000 characters; mail clients and chat apps may truncate it. Shrink the diagram."
            : null,
    });
}).WithName("Share");

// Says whether telemetry is actually going anywhere, so a user who sees no traces
// can tell an unconfigured endpoint from a broken diagram.
app.MapGet("/otlp/status", (PodTracerPool pool) => Results.Ok(new
{
    configured = pool.Target is not null,
    endpoint = pool.Target?.Endpoint.ToString(),
    hint = "set Otlp:Endpoint, or OTEL_EXPORTER_OTLP_ENDPOINT, to any OTLP backend: Jaeger, Tempo, Grafana, SigNoz, or a Collector",
})).WithName("OtlpStatus");

// The instruction set for models: how to mint a shoebox, what the diagram language
// means, how to fire, how to read what came back, and how fast it may be asked.
//
// Served by the API rather than shipped as a front-end asset, deliberately. The
// SPA build is not copied into wwwroot yet, so an asset would 404 on a deployed
// instance; and this documents the endpoints, which is a thing that rots unless it
// lives beside them. text/plain so a person clicking the link reads it instead of
// downloading it.
var instructionsPath = Path.Combine(AppContext.BaseDirectory, "llms.txt");
var instructions = File.Exists(instructionsPath) ? File.ReadAllText(instructionsPath) : null;

app.MapGet("/llms.txt", () => instructions is null
        ? Results.NotFound()
        : Results.Text(instructions, "text/plain; charset=utf-8"))
   .WithName("Instructions");

app.MapFallbackToFile("index.html");

app.Run();

public record DiagramRequest(string Diagram);

public record RunRequest(string Diagram, int RunIndex);

public record ShareRequest(string Diagram, string? ShoeboxId);
