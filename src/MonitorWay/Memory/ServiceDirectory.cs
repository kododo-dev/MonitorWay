namespace Kododo.MonitorWay.Memory;

/// <summary>A service that sent telemetry, with what it sent.</summary>
internal sealed record ServiceInfo(string Name, IReadOnlyList<string> Signals, DateTimeOffset LastSeen);

/// <summary>
/// The services MonitorWay received telemetry from: the application itself (its <c>service.name</c>) and, with the
/// OTLP receiver, any other. The UI offers them as a filter.
/// </summary>
internal sealed class ServiceDirectory(TimeProvider time)
{
    public const string Logs = "logs";
    public const string Traces = "traces";
    public const string Metrics = "metrics";

    /// <summary>More are not listed; a name per instance (with an id in it) would grow without end.</summary>
    private const int MaxServices = 200;

    private readonly object _lock = new();
    private readonly Dictionary<string, (HashSet<string> Signals, DateTimeOffset LastSeen)> _services = [];

    public void Seen(string? service, string signal)
    {
        if (string.IsNullOrEmpty(service))
            return;
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (!_services.TryGetValue(service, out var entry))
            {
                if (_services.Count >= MaxServices)
                    return;
                entry = ([], now);
            }
            entry.Signals.Add(signal);
            _services[service] = (entry.Signals, now);
        }
    }

    public IReadOnlyList<ServiceInfo> List()
    {
        lock (_lock)
        {
            return _services
                .Select(s => new ServiceInfo(s.Key, s.Value.Signals.Order(StringComparer.Ordinal).ToArray(), s.Value.LastSeen))
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
