# Technology Stack

## Backend (.NET)

| Technology | Version | Purpose |
|------------|---------|---------|
| .NET | 9.0 | Runtime framework |
| ASP.NET Core | 9.0 | Web framework |
| Minimal APIs | - | API architecture pattern |

### OpenTelemetry Packages

**Core:**
- `OpenTelemetry` v1.12.0
- `OpenTelemetry.Extensions.Hosting` v1.12.0
- `OpenTelemetry.Exporter.OpenTelemetryProtocol` v1.12.0
- `OpenTelemetry.Exporter.Console` v1.12.0
- `OpenTelemetry.Exporter.Zipkin` v1.12.0

**Trace Instrumentation:**
- `OpenTelemetry.Instrumentation.AspNetCore` v1.12.0
- `OpenTelemetry.Instrumentation.Http` v1.12.0
- `OpenTelemetry.Instrumentation.SqlClient` v1.6.0-beta.3
- `OpenTelemetry.Instrumentation.StackExchangeRedis` v1.0.0-rc9.12

**Metrics Instrumentation:**
- `OpenTelemetry.Instrumentation.Runtime` v1.12.0
- `OpenTelemetry.Instrumentation.Process` v0.5.0-beta.3

### Data Access
- `System.Data.SqlClient` v4.9.0 - SQL Server connectivity
- `Microsoft.Extensions.Caching.StackExchangeRedis` v9.0.5 - Redis distributed cache
- `StackExchange.Redis` - Redis client (transitive)

### Supporting Libraries
- `Polly` v8.5.2 - Resilience and transient-fault handling
- `Swashbuckle.AspNetCore` v8.1.4 - Swagger/OpenAPI documentation

## Frontend (Angular)

| Technology | Version | Purpose |
|------------|---------|---------|
| Angular | 20.0.0 | Frontend framework |
| Angular CLI | 20.0.1 | Build tooling |
| TypeScript | 5.8.3 | Language |
| RxJS | ~7.8.0 | Reactive programming |
| Zone.js | ~0.15.0 | Change detection |

### Development Tools
- `karma` v6.4.0 - Test runner
- `jasmine` v5.1.0 - Testing framework
- `typescript-eslint` v8.19.0 - Linting

## Infrastructure

| Component | Purpose |
|-----------|---------|
| SQL Server | Database operations demo |
| Redis | Caching operations demo |
| OTLP Collector | Telemetry ingestion (otlp.deepcube.ai) |
| Immersive APM | Telemetry visualization |

## Build & Runtime

| Tool | Purpose |
|------|---------|
| npm | Frontend package management |
| dotnet CLI | Backend build and run |
| Node.js | Frontend development server |
| Kestrel | ASP.NET Core web server |
