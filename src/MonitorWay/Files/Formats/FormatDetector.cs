namespace Kododo.MonitorWay.Files.Formats;

/// <summary>Chooses the format of a file from its first lines.</summary>
internal static class FormatDetector
{
    private const int SampleLines = 200;

    /// <summary>Default layouts of the common .NET logging libraries.</summary>
    internal static readonly (string Name, string Template)[] Presets =
    [
        ("serilog", "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"),
        ("nlog", "{Timestamp:yyyy-MM-dd HH:mm:ss.ffff}|{Level}|{SourceContext}|{Message}{NewLine}{Exception}"),
        ("log4net", "{Timestamp:yyyy-MM-dd HH:mm:ss,fff} [{ThreadId}] {Level,-5} {SourceContext} - {Message}{NewLine}{Exception}"),
    ];

    private static readonly Lazy<TemplateFormat[]> PresetFormats =
        new(() => Presets.Select(p => new TemplateFormat(p.Template, p.Name)).ToArray());

    public static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// The format of a file starting with <paramref name="sample"/>, or <c>null</c> when the sample has no
    /// complete line yet. <paramref name="error"/> is set for files that cannot be read as logs.
    /// </summary>
    /// <param name="defaultDate">The date for a configured template whose timestamp has only the time of day.</param>
    public static ILogFormat? Detect(ReadOnlySpan<byte> sample, FileSourceOptions options, out string? error, DateOnly? defaultDate = null)
    {
        error = null;
        if (sample.StartsWith(Bom))
            sample = sample[Bom.Length..];
        if (sample.IndexOf((byte)0) >= 0)
        {
            error = "The file looks binary, not like a text log.";
            return null;
        }

        // Docker's json-file wraps every line of a container's output; the application's format is detected inside.
        if (options.Format == LogFileFormat.Docker || options.Format == LogFileFormat.Auto && DockerFormat.Recognizes(FirstLine(sample)))
        {
            var unwrapped = DockerFormat.UnwrapSample(sample);
            if (unwrapped.Length == 0)
                return null;
            var inner = Detect(unwrapped, options.InnerOptions(), out error, defaultDate);
            return inner is null ? null : new DockerFormat(inner);
        }

        switch (options.Format)
        {
            case LogFileFormat.Clef: return ClefFormat.Instance;
            case LogFileFormat.Json: return JsonLinesFormat.Instance;
            case LogFileFormat.Text: return PlainTextFormat.Instance;
            case LogFileFormat.Console: return ConsoleFormat.Instance;
            case LogFileFormat.Template when options.Template is { } template:
                try
                {
                    return new TemplateFormat(template, defaultDate: defaultDate);
                }
                catch (ArgumentException e)
                {
                    error = $"The template cannot be used: {e.Message}";
                    return null;
                }
        }

        var lines = CompleteLines(sample);
        if (lines.Count == 0)
            return null;

        var first = lines[0];
        if (Clef.IsClef(first))
            return ClefFormat.Instance;
        if (JsonLinesFormat.Recognizes(first))
            return JsonLinesFormat.Instance;
        if (ConsoleFormat.Instance.IsEntryStart(first))
            return ConsoleFormat.Instance;

        // The template whose entries start on most of the sampled lines, provided the file starts with an entry.
        ILogFormat best = PlainTextFormat.Instance;
        var bestStarts = 0;
        foreach (var format in PresetFormats.Value)
        {
            if (!format.IsEntryStart(first))
                continue;
            var starts = 0;
            foreach (var line in lines)
            {
                if (format.IsEntryStart(line))
                    starts++;
            }
            if (starts > bestStarts)
            {
                best = format;
                bestStarts = starts;
            }
        }
        return best;
    }

    /// <summary>The first complete line of the sample, or empty.</summary>
    private static ReadOnlySpan<byte> FirstLine(ReadOnlySpan<byte> sample)
    {
        var end = sample.IndexOf((byte)'\n');
        return end < 0 ? default : sample[..end];
    }

    /// <summary>Non-blank complete lines of the sample (the last line may be cut off).</summary>
    private static List<byte[]> CompleteLines(ReadOnlySpan<byte> sample)
    {
        var lines = new List<byte[]>();
        while (lines.Count < SampleLines)
        {
            var end = sample.IndexOf((byte)'\n');
            if (end < 0)
                break;
            var line = sample[..end].TrimEnd("\r"u8);
            if (!line.Trim(" \t"u8).IsEmpty)
                lines.Add(line.ToArray());
            sample = sample[(end + 1)..];
        }
        return lines;
    }
}
