# Kododo.MonitorWay.UI

Web UI of [Kododo.MonitorWay](https://www.nuget.org/packages/Kododo.MonitorWay): the application's logs, traces, errors and metrics in the browser, served by the application itself, in [Way.Shell](https://www.nuget.org/packages/Kododo.Way.Shell).

## Install

```bash
dotnet add package Kododo.MonitorWay.UI --prerelease
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Instrumentation.AspNetCore
```

## Setup

```csharp
builder.AddMonitorWay(x => x.AddUi());
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddMonitorWay())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddMonitorWay())
    .WithLogging(l => l.AddMonitorWay());

app.UseMonitorWay("/monitor").RequireAuthorization("Admin");
```

Outside the Development environment the UI answers `403` until you decide who may use it: `RequireAuthorization(...)`, a fallback authorization policy, or `AllowAnonymous()` for public demos. Configure the shell's title, links and user with `services.AddWayShell(o => ...)`.

- Overview: requests, server errors and p50, p95 and p99 durations of the HTTP endpoints, per route; drag across a chart to see the traces of that time.
- Logs: live tail, levels, search (`timeout -health category:Payments OrderId:1008 has:exception trace:...`), time ranges, context of an entry, the whole request by trace id.
- Traces: a searchable list (`service:shop status:error duration:>500ms`) and a waterfall with attributes, events and the logs of each span.
- Errors: exceptions grouped, with trends, stack traces and the trace and logs of each occurrence.
- Metrics: any instrument charted by statistic, grouped by an attribute.
- Every view can be sent as a link, and narrowed to one service when others send over OTLP.

Full documentation: [github.com/kododo-dev/MonitorWay](https://github.com/kododo-dev/MonitorWay)
