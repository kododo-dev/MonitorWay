# Kododo.MonitorWay.Mcp

Lets AI agents read the telemetry of [MonitorWay](https://github.com/kododo-dev/MonitorWay) over the [Model Context Protocol](https://modelcontextprotocol.io), with the same API the web UI uses. An agent can look into the running application by itself: from the overview to a failing route, to the error group, to the trace of one occurrence and to the logs of that request. The tools only read:

| Tool | What it does |
|---|---|
| `monitorway_get_service_overview` | Requests, server errors and durations of the HTTP endpoints, per route; the place to start |
| `monitorway_get_services` | The services telemetry came from: the application and those sending over OTLP |
| `monitorway_get_sources` | The logs in memory and the log files, with their ids |
| `monitorway_get_entries` | Entries of a source with MonitorWay's search, levels, categories and time range; pages with cursors |
| `monitorway_get_entry` | One entry in full: exception, properties, trace id |
| `monitorway_get_around` | The entries around an entry or a moment |
| `monitorway_get_stats` | Counts per level, file size and format |
| `monitorway_get_categories` | Categories with their counts |
| `monitorway_get_traces` | Traces, newest first, with a search (`service:`, `status:error`, `duration:>500ms`, attributes) |
| `monitorway_get_trace` | One trace with all its spans, attributes and events |
| `monitorway_get_trace_stats` | Whether traces are collected and how many are kept |
| `monitorway_get_errors` | Exceptions grouped by type and place in the code, with counts and trends |
| `monitorway_get_error` | One group: stack trace and latest occurrences with their trace ids |
| `monitorway_get_error_stats` | Whether errors are collected and how many are kept |
| `monitorway_get_metrics` | The metric instruments with their units and attributes |
| `monitorway_get_metric_series` | A statistic of an instrument over time, grouped by an attribute |
| `monitorway_get_metric_stats` | Whether metrics are collected and how many series are kept |

```csharp
builder.AddMonitorWay(x => x.AddUi());
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddMonitorWay())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddMonitorWay())
    .WithLogging(l => l.AddMonitorWay());
builder.Services.AddMcpServer().WithHttpTransport().WithMonitorWayTools();

app.UseMonitorWay("/monitor").RequireAuthorization("Admin");
app.MapMonitorWayMcp("/monitor/mcp").RequireAuthorization("Admin");
```

Like the UI, outside the Development environment the endpoint answers `403` until authorization is required on it, a fallback policy is set or `ShellOptions.AllowAnonymous` is set. Telemetry often holds personal data and secrets: an agent with these tools can read all of it.
