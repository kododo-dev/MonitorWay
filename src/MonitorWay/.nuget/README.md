# Kododo.MonitorWay

The telemetry of your ASP.NET Core application in a panel served by the application itself. MonitorWay is an exporter of the OpenTelemetry SDK: it keeps the logs, traces and metrics in memory, each with its own retention, groups the exceptions, and reads log files of any size, for the [Kododo.MonitorWay.UI](https://www.nuget.org/packages/Kododo.MonitorWay.UI) web UI. No collector, database or monitoring server to run, and it works next to any other exporter.

## Install

```bash
dotnet add package Kododo.MonitorWay.UI --prerelease
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Instrumentation.AspNetCore
```

## Setup

```csharp
builder.AddMonitorWay(x =>
{
    x.AddMemoryBuffer(m => m.Pool(LogLevel.Error, LogLevel.Critical).KeepEntries(5_000).KeepFor(TimeSpan.FromDays(7)));
    x.ConfigureTraces(t => t.Errors.KeepFor(TimeSpan.FromDays(1)));
    x.AddFiles("logs/*.clef");
    x.AddUi();
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("shop"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddMonitorWay())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddMonitorWay())
    .WithLogging(l => l.AddMonitorWay());

app.UseMonitorWay("/monitor").RequireAuthorization("Admin");
```

- Logs in retention pools with their own limits (entries, bytes, age), so a flood of debug logs never pushes errors out.
- Traces kept whole; failed and slow ones in pools of their own, so they outlive the quick ones.
- Errors: the exceptions of the logs and the spans, grouped by type and place in the code, counted once when both report one.
- Metrics as time series for a week, with percentiles of histograms for any period.
- Files: CLEF, JSON lines, Serilog, NLog and log4net text layouts or your own output template; files of gigabytes open at once.

Other services can send their telemetry too, with [Kododo.MonitorWay.Otlp](https://www.nuget.org/packages/Kododo.MonitorWay.Otlp).

Full documentation: [github.com/kododo-dev/MonitorWay](https://github.com/kododo-dev/MonitorWay)
