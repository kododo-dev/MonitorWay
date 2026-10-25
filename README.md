# MonitorWay

[![CI](https://github.com/kododo-dev/MonitorWay/actions/workflows/ci.yml/badge.svg)](https://github.com/kododo-dev/MonitorWay/actions/workflows/ci.yml)
[![Demo](https://img.shields.io/badge/demo-live-brightgreen)](https://kododo.dev/monitorway/demo)

The telemetry of your ASP.NET Core application in a panel served by the application itself. MonitorWay is an exporter of the OpenTelemetry SDK: it keeps what the application reports in memory and shows it in a web UI, with no collector, database or monitoring server to run. It works next to any other exporter, e.g. OTLP to Grafana or Jaeger.

This is a preview. Logs, traces, errors and metrics are there, in the UI and as tools for AI agents, from the application itself and from other services over OTLP (see [docs/architecture.md](docs/architecture.md)).

A live demo is available at [kododo.dev/monitorway/demo](https://kododo.dev/monitorway/demo).

## Features

- Logs from OpenTelemetry, kept in retention pools with their own limits (entries, bytes, age), so a flood of debug logs never pushes errors out.
- Log files: CLEF, JSON lines, the Serilog, NLog and log4net text layouts, your own Serilog output template, or any text. Files of gigabytes show their newest entries at once.
- Search in one line: `timeout -health category:Payments OrderId:1008 has:exception trace:9f58...`.
- Level filters with counts, time ranges, jump to a moment, an entry in its context, the whole request by trace id, and an overview bar showing where the errors are.
- Live tail, and views that can be sent as a link.
- Traces from OpenTelemetry, kept whole: failed and slow traces in pools of their own, so they outlive the quick ones. A waterfall of the spans with attributes, events and exceptions, and the logs of each span; from a log entry to its trace and back.
- Errors: the exceptions of the logs and the spans grouped by type and place in the code, with counts, an hourly trend, the stack trace and the latest occurrences linked to their traces and logs. An exception both logged and recorded on its span counts once.
- Metrics: counters, gauges and histograms as time series for a week, any of them charted by statistic (rates, averages, percentiles) and grouped by an attribute. An overview of the HTTP endpoints: requests, server errors and p50, p95 and p99 durations, per route; drag across a chart to see the traces of that time.
- Read-only tools for AI agents over the Model Context Protocol, for every signal.
- Other services, in any language, can send their telemetry over OTLP: a trace through several services is shown whole, and every view can be narrowed to one service.

## Packages

| Package | NuGet | Description |
|---|---|---|
| `Kododo.MonitorWay` | [![NuGet](https://img.shields.io/nuget/vpre/Kododo.MonitorWay)](https://www.nuget.org/packages/Kododo.MonitorWay) | Registration, OpenTelemetry exporters, memory stores of logs, traces, errors and metrics, log files, queries |
| `Kododo.MonitorWay.UI` | [![NuGet](https://img.shields.io/nuget/vpre/Kododo.MonitorWay.UI)](https://www.nuget.org/packages/Kododo.MonitorWay.UI) | Web UI in Way.Shell |
| `Kododo.MonitorWay.Mcp` | [![NuGet](https://img.shields.io/nuget/vpre/Kododo.MonitorWay.Mcp)](https://www.nuget.org/packages/Kododo.MonitorWay.Mcp) | Read-only tools for AI agents |
| `Kododo.MonitorWay.Otlp` | [![NuGet](https://img.shields.io/nuget/vpre/Kododo.MonitorWay.Otlp)](https://www.nuget.org/packages/Kododo.MonitorWay.Otlp) | OTLP/HTTP receiver for other services |
| `Kododo.MonitorWay.Core` | [![NuGet](https://img.shields.io/nuget/vpre/Kododo.MonitorWay.Core)](https://www.nuget.org/packages/Kododo.MonitorWay.Core) | Model (`LogEntry`, `TraceEntry`, `TraceSpan`, `ErrorGroup`, `MetricInstrument` and the queries) |

Targets `net8.0`, `net9.0` and `net10.0`.

## Quick start

```bash
dotnet add package Kododo.MonitorWay.UI --prerelease
dotnet add package OpenTelemetry.Extensions.Hosting
```

```csharp
builder.AddMonitorWay(x => x.AddUi());

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("shop"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddMonitorWay())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddMonitorWay())
    .WithLogging(l => l.AddMonitorWay());

app.UseMonitorWay("/monitor").RequireAuthorization("Admin");
```

Open `/monitor`. If the application already uses OpenTelemetry, add `AddMonitorWay()` to its tracing, metrics and logging next to the other exporters. The instrumentations are the usual OpenTelemetry packages (`OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime` and others); spans of your own `ActivitySource` need `AddSource("MyApp")`, and measurements of your own `Meter` need `AddMeter("MyApp")`.

`AddMonitorWay()` turns on the formatted message and the scopes in the OpenTelemetry logging options, which the log view needs. Which categories and levels are exported is set as for any OpenTelemetry logging, under the `OpenTelemetry` alias:

```json
"Logging": {
  "OpenTelemetry": { "LogLevel": { "Default": "Debug", "Microsoft.AspNetCore": "Warning" } }
}
```

Requests to the MonitorWay UI itself (and to the shell's scripts) are not kept, neither their logs nor their traces: the UI polls the server every few seconds. Set `AddUi(ui => ui.CaptureOwnRequests = true)` to keep them.

## Retention

Logs are kept in retention pools. A pool covers one or more levels and has its own limits; an entry is removed as soon as any limit is exceeded. Levels not assigned to a pool share the default pool. `MaxTotalBytes` always applies on top: above it the oldest entries of the lowest levels go first, so errors are removed last.

```csharp
builder.AddMonitorWay(x =>
{
    x.AddMemoryBuffer(m =>
    {
        m.MaxTotalBytes = 128 * 1024 * 1024;
        m.Pool(LogLevel.Error, LogLevel.Critical).KeepEntries(5_000).KeepFor(TimeSpan.FromDays(7));
        m.Pool(LogLevel.Warning).KeepFor(TimeSpan.FromHours(24));
        m.DefaultPool.MaxEntries = 50_000;
    });
    x.AddUi();
});
```

Without configuration, errors and critical entries get a pool of 1 000 entries and the other levels share 50 000; the total is limited to 64 MB. The same settings can come from configuration:

```json
"MonitorWay": {
  "Memory": {
    "MaxTotalBytes": "128MB",
    "Pools": [
      { "Levels": [ "Error", "Critical" ], "MaxEntries": 5000, "MaxAge": "7.00:00:00" },
      { "Levels": [ "Warning" ], "MaxAge": "1.00:00:00" }
    ]
  }
}
```

### Traces

A trace is kept whole, and the decision where to keep it is made when it is complete: its root span ended and a few seconds passed for late spans (`CompletionDelay`, 5 s). Traces with a failed span go to the `Errors` pool, those that took at least `SlowThreshold` (1 s) to `Slow`, the rest to `Other`. Each pool has its own limits; above `MaxTotalBytes` (64 MB) the `Other` traces go first and the failed ones last. Spans that come after a trace was stored are added to it.

```csharp
x.ConfigureTraces(t =>
{
    t.SlowThreshold = TimeSpan.FromMilliseconds(500);
    t.Errors.KeepTraces(5_000).KeepFor(TimeSpan.FromDays(1));
    t.Other.KeepTraces(20_000).KeepFor(TimeSpan.FromHours(1));
});
```

Without configuration the pools keep the last 2 000 failed, 2 000 slow and 10 000 other traces. From configuration:

```json
"MonitorWay": {
  "Traces": {
    "MaxTotalBytes": "128MB",
    "SlowThreshold": "00:00:00.500",
    "Errors": { "MaxTraces": 5000, "MaxAge": "1.00:00:00" },
    "Other": { "MaxTraces": 20000, "MaxAge": "01:00:00" }
  }
}
```

### Errors

Errors are the exceptions of the exported logs (`LogError(e, ...)` and any other level with an exception) and the `exception` events of the spans. Two exceptions are the same error when they have the same type and were thrown from the same place: the top frames of the application's own code, without line numbers. The message does not count, as it often carries ids. An exception that was both logged and recorded on its span in one trace is one occurrence; a span that failed without an exception is not an error. Exceptions of requests to the UI are left out, as their logs and traces are.

Each group keeps its count, an hourly trend for 7 days and its 50 latest occurrences. Up to 1 000 groups are kept, for 7 days since each was last seen:

```csharp
x.ConfigureErrors(e =>
{
    e.MaxGroups = 5_000;
    e.MaxOccurrences = 100;
    e.MaxAge = TimeSpan.FromDays(30);
});
```

or `"MonitorWay": { "Errors": { "MaxGroups": 5000, "MaxOccurrences": 100, "MaxAge": "30.00:00:00" } }`.

### Metrics

MonitorWay adds its own metric reader to OpenTelemetry, which collects every 10 seconds as deltas, independently of other readers. Every series (an instrument with one set of attribute values) is kept in three resolutions: 10 seconds for the last hour, 1 minute for the last day and 10 minutes for the last week. Counters keep their increase per period, gauges and up-down counters their average, minimum, maximum and last value, histograms their counts per bucket, so percentiles can be read for any period and for several series merged (e.g. all routes). Exponential histograms are not kept.

An attribute with many values (a user id, a raw path) creates a series for each. Above 500 series per instrument, or 64 MB for all of them, new series are counted rather than kept; the metrics view shows how many.

```csharp
x.ConfigureMetrics(m =>
{
    m.MaxSeriesPerInstrument = 1_000;
    m.MaxTotalBytes = 128 * 1024 * 1024;
});
```

or `"MonitorWay": { "Metrics": { "MaxSeriesPerInstrument": 1000, "MaxTotalBytes": "128MB", "ExportInterval": "00:00:10" } }`.

## Other services (OTLP)

`Kododo.MonitorWay.Otlp` receives OTLP over HTTP, so the services around the application, in any language, can send their traces, metrics and logs to its MonitorWay:

```csharp
app.MapMonitorWayOtlp("/monitor/otlp").RequireAuthorization("TelemetrySenders");
```

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=https://shop.example/monitor/otlp
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

Protobuf and JSON are accepted, optionally gzip-compressed. What arrives goes to the same memory as the application's own telemetry, within the same limits: a trace that passes through the application and other services is shown whole, exceptions become error groups, and the running totals most exporters send (cumulative temporality) are kept as increases. Services are told apart by `service.name`; with more than one, each view has a service filter (`service:warehouse` in a search). Outside the Development environment the receiver answers `403` until you decide who may send, as the UI does.

## Log files

```csharp
x.AddFiles("logs/*.clef");                       // format detected per file
x.AddFiles("/var/log/app/**/*.log");             // absolute paths and ** work too
x.AddFiles("logs/*.txt", f => f.UseTemplate(
    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));
```

The format is detected from the first lines of each file:

| Format | Written by |
|---|---|
| CLEF | Serilog `CompactJsonFormatter` |
| JSON lines | Serilog `JsonFormatter`, NLog `JsonLayout` and most structured loggers |
| Console | ASP.NET Core console output (`info: Category[0]` with the message on the next lines), e.g. from `docker logs` |
| Text layouts | the default file layouts of Serilog, NLog and log4net |
| Template | your own Serilog output template (`UseTemplate`); paste the template the application logs with |
| Text | anything else; levels and times are guessed |

Lines that do not start an entry belong to the previous one, so stack traces stay with their message. New files matching the pattern (rotation) appear automatically, growing files are followed, and a truncated or replaced file is read again. A template that writes only the time of day (`{Timestamp:HH:mm:ss}`) takes the date from the file name (`app-20261013.log`) or, failing that, from the day the file was last written.

Rotated files of one log, or the logs of a service's replicas, can be read as one source, merged by time. Give them the same name with `Describe` and set `Merge`:

```csharp
x.AddFiles("logs/app.log*", f =>
{
    f.Merge = true;
    f.Describe = path => new FileDescription("app");   // app.log, app.log.1, app.log.2 as one source "app"
});
```

Large files are not loaded: the newest entries are read from the end of the file at once, and an index of chunks of about 64 KB is built in the background on several threads, so filters skip what cannot match and text searches read the rest in parallel. On a 10 GB CLEF file with 66 million entries (NVMe, 16 GB RAM): newest entries in 0.03 s before any indexing, full index in 12 s, critical entries only in 0.07 s, a rare word at the very start of the file found in 9 s.

## The UI

The UI runs in [Way.Shell](https://github.com/kododo-dev/WayShell), the shell shared by the Kododo Way libraries: the layout, theme, language and session handling come from the shell, and with other Ways in the same app it can also be opened from the shell's panel. Configure the title, links and user with `services.AddWayShell(o => ...)`.

| | |
|---|---|
| Overview | The first page: requests, server errors and the p50, p95 and p99 durations of the HTTP endpoints in a range (15 minutes to 7 days), as charts and per route, with the latest errors. A route opens its traces; dragging across a chart opens the traces of that time. Made of the `http.server.request.duration` metric of ASP.NET Core. |
| Levels | Chips with counts and retention. With all levels shown, a click narrows to one level; then clicks add and remove levels. |
| Search | `timeout payment` all words, `"order 1008"` phrase, `-health` exclude, `category:Payments`, `exception:Timeout`, `has:exception`, `OrderId:1008` or `CustomerId:c-6*` property, `trace:9f58...` one request; `.*` switches to a regular expression |
| Time | Last 15 min to 7 days, a custom range, or jump to a moment without hiding other entries |
| Sources | Grouped and searchable. Click a source to show it; check several, or a group, to see them together, ordered by time. |
| Categories | The categories of the source with their counts: uncheck to hide, "only" to show just one. |
| Entries | Right-click a row: show in context, same request, only or hide this category, copy. In the details, the filter button next to a property adds it to the search. |
| Overview bar | The whole source next to the list: density, errors and warnings; click to jump there. |
| Traces | Newest first, following new ones live, with buttons for failed and slow traces. Search: `service:shop`, `name:GET*`, `status:error`, `duration:>500ms`, `kind:client`, `has:exception`, `http.route:/orders/{id}` or any attribute, plus words and phrases as in the logs. |
| A trace | The spans as a waterfall that can be folded, with exceptions marked on their span. A span shows its attributes, events (exceptions with the stack trace) and links; below are the logs of the trace, or of the selected span. "Show in logs" opens the logs with `trace:<id>`, and "Open trace" in a log entry leads back. |
| Errors | The latest or the most frequent first, with the operation, services, count and a trend of the last 24 hours. Search: `type:Timeout`, `service:shop`, `endpoint:POST*`, `trace:<id>`, plus words in the type, message and stack trace. |
| An error | The trend of the last 7 days, the frames it is recognized by, the stack trace with the application's frames marked, and the latest occurrences with their trace and logs. "Open error" leads here from a log entry or a span's exception. |
| Metrics | The instruments by meter, searchable. A chart of the chosen one: a statistic (sum or rate, average, last, minimum or maximum, percentiles), grouped by one of its attributes, in a range; drag across it to zoom in. Each view can be sent as a link. |

## AI agents (MCP)

`Kododo.MonitorWay.Mcp` offers the same API to AI agents as read-only tools, so an agent can look into the running application by itself: from the overview to a failing route, to the error group, to the trace of one occurrence and to the logs of that request.

| Signal | Tools (prefix `monitorway_`) |
|---|---|
| Overview | `get_service_overview`: requests, server errors and durations of the HTTP endpoints, per route |
| Logs | `get_sources`, `get_entries` (search and filters), `get_entry` (with the exception), `get_around`, `get_stats`, `get_categories` |
| Traces | `get_traces` (search), `get_trace` (all spans with attributes and events), `get_trace_stats` |
| Errors | `get_errors`, `get_error` (stack trace and occurrences with trace ids), `get_error_stats` |
| Metrics | `get_metrics`, `get_metric_series` (a statistic over time, grouped by an attribute), `get_metric_stats` |
| Services | `get_services`: the application and the services sending over OTLP |

```csharp
builder.Services.AddMcpServer().WithHttpTransport().WithMonitorWayTools();

app.MapMonitorWayMcp("/monitor/mcp").RequireAuthorization("Admin");
```

It needs `AddUi()`, which registers the handlers.

## Security

Telemetry often holds personal data and secrets. Outside the Development environment the UI, its API and the MCP endpoint answer `403` until you decide who may use them: `RequireAuthorization(...)` on what `UseMonitorWay()` and `MapMonitorWayMcp()` return, a fallback authorization policy, or `AllowAnonymous()` (for public demos only). The Way is marked as sensitive, so the shell's panel lists it only for users its own policy lets in. Files are addressed by id, never by path, and only files matching the configured patterns are readable. Regular expressions from users run in non-backtracking mode with a timeout.

## Running the demo

```bash
cd src/MonitorWay.UI/SPA && npm install && npm run build && cd ../../..
dotnet run --project src/MonitorWay.Demo.Web -f net10.0
```

Open `http://localhost:5080/monitor/`; an MCP client can connect to `http://localhost:5080/monitor/mcp`. The demo simulates a shop whose customers call it over HTTP, with a warehouse as a second service that sends its telemetry over OTLP: it records traces (cache, database, stock, coupons and a payment provider that is sometimes slow or fails, with their exceptions), metrics (ASP.NET Core, the runtime and the shop's own) and logs through OpenTelemetry, writes CLEF and text log files, and generates a week of history (`Demo:HistoryMegabytes`, `Demo:TextHistoryMegabytes`).

The SPA is embedded into `Kododo.MonitorWay.UI`, so build it before `dotnet build`. During UI development `npm run dev` runs the module in Way.Shell's dev host and sends the API to a running demo; `npm run lint` and `npm test` check the frontend. Browser scenarios: `node src/MonitorWay.Demo.Web/e2e/check.mjs http://localhost:5080` for the logs, `traces.mjs` for the traces, `errors.mjs` for the errors, `metrics.mjs` for the overview and the metrics and `services.mjs` for the second service.

## Benchmarks

```bash
cd src/MonitorWay.Benchmarks
dotnet run -c Release -- --filter * --inProcess                 # BenchmarkDotNet: logging, indexing, reading
dotnet run -c Release -- bigfile /tmp/big.clef 10               # one pass over a generated 10 GB file
```

## Architecture

See [docs/architecture.md](docs/architecture.md).

## License

MIT
