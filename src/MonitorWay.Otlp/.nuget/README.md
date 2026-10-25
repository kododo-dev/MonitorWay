# Kododo.MonitorWay.Otlp

An OTLP/HTTP receiver for [MonitorWay](https://github.com/kododo-dev/MonitorWay): other services, in any language, send their traces, metrics and logs to the application's MonitorWay, which shows them next to its own. A trace that passes through several services is shown whole, and every view can be narrowed to one service.

```csharp
builder.AddMonitorWay(x => x.AddUi());

app.UseMonitorWay("/monitor").RequireAuthorization("Admin");
app.MapMonitorWayOtlp("/monitor/otlp").RequireAuthorization("TelemetrySenders");
```

Point the senders at it like at any OTLP endpoint over HTTP, in protobuf or JSON:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=https://shop.example/monitor/otlp
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

| Endpoint | Content |
|---|---|
| `{path}/v1/traces` | spans, with their events (exceptions become errors) and links |
| `{path}/v1/logs` | log records, with their attributes, trace context and exceptions |
| `{path}/v1/metrics` | sums, gauges and histograms, as deltas or running totals |

Bodies may be gzip-compressed, up to 32 MB. Exponential histograms and summaries are not kept. Services are told apart by `service.name`.

Outside the Development environment the endpoints answer `403` until you decide who may send: `RequireAuthorization(...)`, `AllowAnonymous()` (for a network only trusted services reach) or a fallback authorization policy. Anyone who can send can fill the memory or plant misleading data.

The OTLP messages are generated from [opentelemetry-proto](https://github.com/open-telemetry/opentelemetry-proto) v1.11.0 (Apache License 2.0, included in the package).
