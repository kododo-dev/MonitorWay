using System.Diagnostics;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Memory;
using OpenTelemetry;
using OpenTelemetry.Resources;

namespace Kododo.MonitorWay.Telemetry;

/// <summary>
/// Keeps the spans of the OpenTelemetry SDK in the trace store. Everything needed is copied, as text, before
/// <see cref="Export"/> returns: the store must not hold the activities, which the application may still reference.
/// </summary>
internal sealed class TraceExporter : BaseExporter<Activity>
{
    // Status as a tag, set by instrumentations from before Activity.SetStatus; the status itself is read from it.
    private const string StatusCodeTag = "otel.status_code";
    private const string StatusDescriptionTag = "otel.status_description";

    private readonly MemoryTraceStore _store;
    private string? _serviceName;
    private bool _serviceNameRead;

    private readonly ServiceDirectory? _services;

    public TraceExporter(MemoryTraceStore store, ServiceDirectory? services = null)
    {
        _services = services;
        _store = store;
        store.IsExportedTo = true;
        if (store.Errors is not null)
            store.Errors.IsExportedTo = true;
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        using var scope = SuppressInstrumentationScope.Begin();
        var serviceName = ServiceName();
        _services?.Seen(serviceName, ServiceDirectory.Traces);
        foreach (var activity in batch)
        {
            try
            {
                var (span, size) = Capture(activity, serviceName);
                _store.Add(span, size);
            }
            catch
            {
                // One span that cannot be read must not cost the others, nor reach the application.
            }
        }
        return ExportResult.Success;
    }

    internal static (TraceSpan Span, int Size) Capture(Activity activity, string? serviceName)
    {
        const int SpanOverhead = 240;
        const int ItemOverhead = 48;
        long size = SpanOverhead + activity.DisplayName.Length * sizeof(char);

        var status = activity.Status switch
        {
            ActivityStatusCode.Error => TraceSpanStatus.Error,
            ActivityStatusCode.Ok => TraceSpanStatus.Ok,
            _ => TraceSpanStatus.Unset,
        };
        var statusDescription = activity.StatusDescription;

        var attributes = new List<KeyValuePair<string, string?>>();
        foreach (var (key, value) in activity.TagObjects)
        {
            var text = EntryCapture.Format(value);
            if (key == StatusCodeTag)
            {
                if (status == TraceSpanStatus.Unset && text is not null)
                    status = text.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ? TraceSpanStatus.Error
                        : text.Equals("OK", StringComparison.OrdinalIgnoreCase) ? TraceSpanStatus.Ok : TraceSpanStatus.Unset;
                continue;
            }
            if (key == StatusDescriptionTag)
            {
                statusDescription ??= text;
                continue;
            }
            attributes.Add(new(key, text));
            size += ItemOverhead + (key.Length + (text?.Length ?? 0)) * sizeof(char);
        }

        var events = new List<SpanEvent>();
        foreach (var e in activity.Events)
        {
            var eventAttributes = new List<KeyValuePair<string, string?>>();
            foreach (var (key, value) in e.Tags)
            {
                var text = EntryCapture.Format(value);
                eventAttributes.Add(new(key, text));
                size += ItemOverhead + (key.Length + (text?.Length ?? 0)) * sizeof(char);
            }
            events.Add(new SpanEvent(e.Name, e.Timestamp, eventAttributes));
            size += ItemOverhead + e.Name.Length * sizeof(char);
        }

        var links = new List<SpanLink>();
        foreach (var link in activity.Links)
        {
            links.Add(new SpanLink(link.Context.TraceId.ToHexString(), link.Context.SpanId.ToHexString()));
            size += ItemOverhead + 48 * sizeof(char);
        }

        var span = new TraceSpan(
            activity.TraceId.ToHexString(),
            activity.SpanId.ToHexString(),
            activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
            activity.DisplayName,
            activity.Kind switch
            {
                ActivityKind.Server => TraceSpanKind.Server,
                ActivityKind.Client => TraceSpanKind.Client,
                ActivityKind.Producer => TraceSpanKind.Producer,
                ActivityKind.Consumer => TraceSpanKind.Consumer,
                _ => TraceSpanKind.Internal,
            },
            new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero),
            activity.Duration,
            status,
            statusDescription,
            serviceName,
            activity.Source.Name,
            attributes,
            events,
            links,
            activity.HasRemoteParent);
        return (span, (int)Math.Min(size, int.MaxValue));
    }

    /// <summary><c>service.name</c> of the provider's resource, read once it is known.</summary>
    private string? ServiceName()
    {
        if (_serviceNameRead)
            return _serviceName;
        if (ParentProvider is null)
            return null;

        _serviceName = ParentProvider.GetResource().Attributes
            .FirstOrDefault(a => a.Key == "service.name").Value as string;
        _serviceNameRead = true;
        return _serviceName;
    }
}
