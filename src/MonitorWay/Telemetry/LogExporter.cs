using System.Diagnostics;
using Kododo.MonitorWay.Memory;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace Kododo.MonitorWay.Telemetry;

/// <summary>
/// Keeps the log records of the OpenTelemetry SDK in the memory buffer. Records are pooled by the SDK, so everything
/// needed is copied, as text, before <see cref="Export"/> returns.
/// </summary>
internal sealed class LogExporter : BaseExporter<LogRecord>
{
    private const string OriginalFormat = "{OriginalFormat}";

    private readonly MemoryLogBuffer _buffer;
    private readonly MemoryErrorStore? _errors;
    private string? _serviceName;
    private bool _serviceNameRead;

    private readonly ServiceDirectory? _services;

    public LogExporter(MemoryLogBuffer buffer, MemoryErrorStore? errors = null, ServiceDirectory? services = null)
    {
        _services = services;
        _buffer = buffer;
        buffer.IsExportedTo = true;
        _errors = errors;
        if (errors is not null)
            errors.IsExportedTo = true;
    }

    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        using var scope = SuppressInstrumentationScope.Begin();
        var serviceName = ServiceName();
        _services?.Seen(serviceName, ServiceDirectory.Logs);
        foreach (var record in batch)
        {
            try
            {
                Append(record, serviceName);
            }
            catch
            {
                // One record that cannot be read must not cost the others, nor reach the application.
            }
        }
        return ExportResult.Success;
    }

    private void Append(LogRecord record, string? serviceName)
    {
        var category = record.CategoryName ?? "";
        if (EntryCapture.IsOwnCategory(category))
            return;

        var properties = new List<KeyValuePair<string, string?>>();
        if (record.EventId.Id != 0)
            properties.Add(new("EventId", record.EventId.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (!string.IsNullOrEmpty(record.EventId.Name))
            properties.Add(new("EventName", record.EventId.Name));

        string? template = null;
        if (record.Attributes is { } attributes)
        {
            foreach (var (key, value) in attributes)
            {
                if (key == OriginalFormat)
                    template = value as string;
                else
                    properties.Add(new(key, EntryCapture.Format(value)));
            }
        }

        record.ForEachScope(static (scope, list) =>
        {
            foreach (var (key, value) in scope)
            {
                // A scope that is not a list of values, e.g. a string, comes without a key.
                if (key != OriginalFormat)
                    list.Add(new(key.Length == 0 ? "Scope" : key, EntryCapture.Format(value)));
            }
        }, properties);

        template ??= record.Body;
        var message = record.FormattedMessage ?? record.Body ?? template ?? "";
        var exception = record.Exception;

        var stored = _buffer.Append(EntryCapture.Create(
            new DateTimeOffset(record.Timestamp, TimeSpan.Zero),
            record.LogLevel,
            category,
            message,
            template == message ? null : template,
            exception?.ToString(),
            properties,
            record.TraceId == default ? null : record.TraceId.ToHexString(),
            record.SpanId == default ? null : record.SpanId.ToHexString(),
            exception?.GetType().FullName,
            serviceName));

        // An exception of a request to the UI is dropped with its entry.
        if (exception is not null && stored is not null && _errors is not null)
        {
            _errors.Add(new ReportedException(
                stored.Timestamp ?? DateTimeOffset.UtcNow, exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message, stored.Exception, serviceName, LocalRoot(Activity.Current)?.DisplayName,
                stored.TraceId, stored.SpanId, stored.Id, category));
        }
    }

    /// <summary>
    /// The span the application started for the work it is doing, e.g. the incoming request. Logs are exported
    /// synchronously, so the current activity is the one the entry was written in.
    /// </summary>
    private static Activity? LocalRoot(Activity? activity)
    {
        while (activity?.Parent is { } parent)
            activity = parent;
        return activity;
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
