using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Sources;

/// <summary>Entry counts along a source, from <see cref="Start"/> (inclusive) to <see cref="End"/> (exclusive), in id space.</summary>
internal sealed record Overview(long Start, long End, OverviewBucket[] Buckets);

/// <param name="Errors">Error and Critical entries.</param>
internal readonly record struct OverviewBucket(long Total, long Warnings, long Errors)
{
    public OverviewBucket Add(LogLevel level, long count) => level switch
    {
        LogLevel.Warning => this with { Total = Total + count, Warnings = Warnings + count },
        LogLevel.Error or LogLevel.Critical => this with { Total = Total + count, Errors = Errors + count },
        _ => this with { Total = Total + count },
    };
}
