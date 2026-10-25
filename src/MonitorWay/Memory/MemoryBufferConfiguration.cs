using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Memory;

/// <summary>
/// Reads <see cref="MemoryBufferOptions"/> from configuration. Sizes accept units (<c>"64MB"</c>),
/// which the default binder cannot parse into a <see cref="long"/>.
/// </summary>
internal static class MemoryBufferConfiguration
{
    public static void Bind(IConfigurationSection section, MemoryBufferOptions options)
    {
        if (section["MaxTotalBytes"] is { } maxTotal)
            options.MaxTotalBytes = ParseSize(maxTotal, section.GetSection("MaxTotalBytes").Path);

        var defaultPool = section.GetSection("DefaultPool");
        if (defaultPool.Exists())
            BindLimits(defaultPool, options.DefaultPool);

        foreach (var poolSection in section.GetSection("Pools").GetChildren())
        {
            var pool = new RetentionPoolOptions();
            foreach (var levelSection in poolSection.GetSection("Levels").GetChildren())
            {
                if (!Enum.TryParse<LogLevel>(levelSection.Value, ignoreCase: true, out var level))
                    throw new InvalidOperationException($"'{levelSection.Value}' at {levelSection.Path} is not a log level.");
                pool.Levels.Add(level);
            }
            BindLimits(poolSection, pool);
            options.Pools.Add(pool);
        }
    }

    private static void BindLimits(IConfigurationSection section, RetentionPoolOptions pool)
    {
        if (section["MaxEntries"] is { } maxEntries)
            pool.MaxEntries = int.Parse(maxEntries, CultureInfo.InvariantCulture);
        if (section["MaxBytes"] is { } maxBytes)
            pool.MaxBytes = ParseSize(maxBytes, section.GetSection("MaxBytes").Path);
        if (section["MaxAge"] is { } maxAge)
            pool.MaxAge = TimeSpan.Parse(maxAge, CultureInfo.InvariantCulture);
    }

    internal static long ParseSize(string value, string path)
    {
        var text = value.Trim();
        var unitStart = text.Length;
        while (unitStart > 0 && char.IsLetter(text[unitStart - 1]))
            unitStart--;

        var multiplier = text[unitStart..].ToUpperInvariant() switch
        {
            "" or "B" => 1L,
            "KB" => 1024L,
            "MB" => 1024L * 1024,
            "GB" => 1024L * 1024 * 1024,
            _ => throw new InvalidOperationException($"'{value}' at {path} is not a size. Use a number of bytes or a KB, MB or GB suffix."),
        };

        if (!decimal.TryParse(text[..unitStart].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            throw new InvalidOperationException($"'{value}' at {path} is not a size. Use a number of bytes or a KB, MB or GB suffix.");

        return (long)(number * multiplier);
    }
}
