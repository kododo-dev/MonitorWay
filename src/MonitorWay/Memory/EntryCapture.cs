using System.Collections;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Memory;

/// <summary>
/// Turns what OpenTelemetry hands over into a <see cref="PendingEntry"/>: values become text right away and the size
/// of the entry is estimated for the retention limits.
/// </summary>
internal static class EntryCapture
{
    private static readonly string[] OwnNamespaces =
        ["Kododo.MonitorWay.Core.", "Kododo.MonitorWay.Files.", "Kododo.MonitorWay.Memory.", "Kododo.MonitorWay.Telemetry.",
         "Kododo.MonitorWay.Querying.", "Kododo.MonitorWay.Sources.", "Kododo.MonitorWay.UI."];

    /// <summary>MonitorWay's own categories are not captured, so reading the buffer cannot feed it.</summary>
    public static bool IsOwnCategory(string category) =>
        Array.Exists(OwnNamespaces, ns => category.StartsWith(ns, StringComparison.Ordinal));

    /// <summary>Converts a value to text right away, so the buffer never holds references to application objects.</summary>
    public static string? Format(object? value) => value switch
    {
        null => null,
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable items => string.Join(", ", items.Cast<object?>().Select(Format)),
        _ => value.ToString(),
    };

    public static PendingEntry Create(
        DateTimeOffset timestamp, LogLevel level, string category, string message, string? template, string? exception,
        List<KeyValuePair<string, string?>> properties, string? traceId, string? spanId,
        string? exceptionType = null, string? serviceName = null) =>
        new(timestamp, level, category, message, template, exception, properties, traceId, spanId,
            EstimateSize(category, message, template, exception, properties), exceptionType, serviceName);

    private static int EstimateSize(string category, string message, string? template, string? exception,
        List<KeyValuePair<string, string?>> properties)
    {
        const int EntryOverhead = 160;
        const int PropertyOverhead = 48;
        long size = EntryOverhead +
            (category.Length + message.Length + (template?.Length ?? 0) + (exception?.Length ?? 0)) * sizeof(char);
        foreach (var (key, value) in properties)
            size += PropertyOverhead + (key.Length + (value?.Length ?? 0)) * sizeof(char);
        return (int)Math.Min(size, int.MaxValue);
    }
}

/// <summary>
/// Request paths whose logs and traces are not kept: those of the MonitorWay UI itself, which polls the server every
/// few seconds and would otherwise fill the memory with its own requests.
/// </summary>
/// <remarks>
/// Entries logged while handling a request carry its path in the <c>RequestPath</c> property of the ASP.NET Core
/// request scope; MonitorWay turns on scopes in the OpenTelemetry logging options for that. Traces are recognized by
/// the <c>url.path</c> attribute of their server span.
/// </remarks>
internal sealed class IgnoredRequests
{
    private volatile string[] _paths = [];

    public void Add(string path)
    {
        var normalized = "/" + path.Trim('/');
        if (normalized == "/")
            return; // Mounted at the root, every request would match.
        lock (this)
            _paths = [.. _paths, normalized];
    }

    public bool Matches(IReadOnlyList<KeyValuePair<string, string?>> properties)
    {
        var paths = _paths;
        if (paths.Length == 0)
            return false;
        foreach (var (key, value) in properties)
        {
            if (key == "RequestPath" && MatchesPath(value))
                return true;
        }
        return false;
    }

    /// <summary>Whether a request path is one of the ignored paths or below one.</summary>
    public bool MatchesPath(string? value)
    {
        if (value is null)
            return false;
        foreach (var path in _paths)
        {
            if (value.StartsWith(path, StringComparison.OrdinalIgnoreCase) &&
                (value.Length == path.Length || value[path.Length] == '/'))
                return true;
        }
        return false;
    }
}
