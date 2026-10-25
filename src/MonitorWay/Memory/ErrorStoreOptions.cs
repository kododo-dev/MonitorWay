using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Kododo.MonitorWay.Memory;

/// <summary>
/// Limits of the error groups kept in memory. A group is small (counters and its latest occurrences), so the limits
/// are counts rather than bytes.
/// </summary>
public sealed class ErrorStoreOptions
{
    /// <summary>Groups kept at most; above it the group seen least recently is removed. Default: 1 000.</summary>
    public int MaxGroups { get; set; } = 1_000;

    /// <summary>Latest occurrences kept per group; the count covers all of them. Default: 50.</summary>
    public int MaxOccurrences { get; set; } = 50;

    /// <summary>A group not seen for this long is removed. Default: 7 days.</summary>
    public TimeSpan? MaxAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Reads the options from configuration (<c>MonitorWay:Errors</c>).</summary>
    internal static void Bind(IConfigurationSection section, ErrorStoreOptions options)
    {
        if (section["MaxGroups"] is { } maxGroups)
            options.MaxGroups = int.Parse(maxGroups, CultureInfo.InvariantCulture);
        if (section["MaxOccurrences"] is { } maxOccurrences)
            options.MaxOccurrences = int.Parse(maxOccurrences, CultureInfo.InvariantCulture);
        if (section["MaxAge"] is { } maxAge)
            options.MaxAge = TimeSpan.Parse(maxAge, CultureInfo.InvariantCulture);
    }

    internal void Validate()
    {
        if (MaxGroups <= 0 || MaxOccurrences <= 0)
            throw new InvalidOperationException("MonitorWay: MaxGroups and MaxOccurrences of the errors must be positive.");
        if (MaxAge <= TimeSpan.Zero)
            throw new InvalidOperationException("MonitorWay: MaxAge of the errors must be positive.");
    }
}
