namespace Kododo.MonitorWay.Core.Model;

/// <summary>How the values of an instrument are aggregated, as in OpenTelemetry.</summary>
public enum MetricKind
{
    /// <summary>A count that only grows, e.g. requests handled; kept as the increase per period.</summary>
    Counter,

    /// <summary>A count that goes up and down, e.g. requests in progress; kept as its value.</summary>
    UpDownCounter,

    /// <summary>A measured value, e.g. memory in use; kept as its value.</summary>
    Gauge,

    /// <summary>A distribution, e.g. request durations; kept as counts per bucket, for percentiles.</summary>
    Histogram,
}

/// <summary>An instrument the application reports measurements of.</summary>
/// <param name="Meter">Name of the meter, e.g. <c>Microsoft.AspNetCore.Hosting</c>.</param>
/// <param name="Name">Name of the instrument, e.g. <c>http.server.request.duration</c>.</param>
/// <param name="Unit">Unit as in OpenTelemetry (UCUM), e.g. <c>s</c>, <c>By</c> or <c>{request}</c>.</param>
public sealed record MetricInstrument(string Meter, string Name, string? Unit, string? Description, MetricKind Kind)
{
    /// <summary>Identifies the instrument among those of all meters.</summary>
    public string Key => $"{Meter}/{Name}";
}
