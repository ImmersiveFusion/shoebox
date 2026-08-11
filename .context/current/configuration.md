# Configuration Guide

## Prerequisites

- **Node.js** (LTS version) - For Angular development
- **npm** - Package manager for frontend
- **.NET 9 SDK** - For backend development
- **SQL Server** - Database operations (Azure SQL or local)
- **Redis** - Cache operations (Azure Redis or local)
- **OTLP-compatible backend** - For telemetry (e.g., Immersive APM)

## Backend Configuration

### appsettings.json (Production)

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Otlp": {
    "Endpoint": "https://otlp.deepcube.ai",
    "ApiKey": "your-api-key-here"
  }
}
```

### appsettings.Development.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Otlp": {
    "Endpoint": "https://localhost:7017",
    "ApiKey": "development-key"
  }
}
```

### launchSettings.json

Contains connection strings and launch profiles:

```json
{
  "profiles": {
    "http": {
      "commandName": "Project",
      "launchBrowser": true,
      "launchUrl": "swagger",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      },
      "applicationUrl": "http://localhost:5168"
    },
    "https": {
      "commandName": "Project",
      "launchBrowser": true,
      "launchUrl": "swagger",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "ConnectionStrings:Sql:Closed": "Server=...",
        "ConnectionStrings:Sql:Open": "Server=invalid;...",
        "ConnectionStrings:Redis:Closed": "...",
        "ConnectionStrings:Redis:Open": "invalid:6380,..."
      },
      "applicationUrl": "https://localhost:7263;http://localhost:5168"
    }
  }
}
```

## Frontend Configuration

> **SP-074 note (IAPM to DeepCube rename).** The `grid.iapm.app` host in the two snippets below is
> deliberately NOT flipped to `deepcube.ai`. It has no verified DNS or certificate mapping in the
> cutover set, unlike `otlp`, `portal`, `azure`, `my`, and `demo` (which becomes `chaos`, not
> `demo`). Flipping it by pattern would produce a host that does not serve. Same class as
> `bridge.iapm.app`. Separately, these two snippets are already stale against the real
> `environment.ts`, which uses `apiUri` and `gitHubUrl` rather than `apiBaseUrl` and `apm.gridUrl`.

### environment.ts (Production)

```typescript
export const environment = {
  production: true,
  apiBaseUrl: `${window.location.protocol}//${window.location.hostname}`,
  apm: {
    gridUrl: 'https://grid.iapm.app',
    facets: {
      sandboxId: 'facet_sandbox.id'
    }
  }
};
```

### environment.development.ts

```typescript
export const environment = {
  production: false,
  apiBaseUrl: 'https://localhost:7263',
  apm: {
    gridUrl: 'https://grid.iapm.app',
    facets: {
      sandboxId: 'facet_sandbox.id'
    }
  }
};
```

## Connection Strings

### SQL Server

**Closed (Normal):**
```
Server=your-server.database.windows.net;Database=YourDb;User Id=user;Password=pass;Encrypt=True;
```

**Open (Failure Simulation):**
```
Server=invalid;Database=db;User Id=user;Password=pass;Connect Timeout=1;
```

### Redis

**Closed (Normal):**
```
your-redis.redis.cache.windows.net:6380,password=...,ssl=True,abortConnect=False
```

**Open (Failure Simulation):**
```
invalid:6380,password=...,ssl=True,abortConnect=False,connectTimeout=1000
```

## Running Locally

### Backend

```bash
cd src/Example.Api
dotnet run
```

Or with specific profile:
```bash
dotnet run --launch-profile https
```

### Frontend (Development)

```bash
cd src/Example.Spa
npm install
ng serve
```

The Angular dev server runs on http://localhost:4200 and proxies API calls to the backend.

### Frontend (Production Build)

```bash
cd src/Example.Spa
ng build --configuration production
```

Built files are output to `dist/` and should be copied to `Example.Api/wwwroot/`.

## Environment Variables

Override configuration via environment variables:

| Variable | Description |
|----------|-------------|
| `ASPNETCORE_ENVIRONMENT` | Development / Production |
| `ConnectionStrings:Sql:Closed` | Valid SQL connection |
| `ConnectionStrings:Sql:Open` | Invalid SQL connection for failures |
| `ConnectionStrings:Redis:Closed` | Valid Redis connection |
| `ConnectionStrings:Redis:Open` | Invalid Redis connection for failures |
| `Otlp:Endpoint` | OTLP exporter endpoint |
| `Otlp:ApiKey` | API key for OTLP backend |

## CORS Configuration

The API is configured to allow frontend development:

```csharp
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});
```

## Swagger/OpenAPI

Available at `/swagger` when running in Development mode.
