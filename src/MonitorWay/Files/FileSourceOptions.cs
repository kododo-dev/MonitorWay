namespace Kododo.MonitorWay.Files;

/// <summary>
/// Log files MonitorWay reads, selected by a glob pattern.
/// </summary>
public sealed class FileSourceOptions
{
    /// <summary>
    /// Glob pattern of the files, e.g. <c>logs/*.clef</c> or <c>/var/log/app/**/*.log</c>.
    /// A relative pattern is resolved against the application's content root.
    /// </summary>
    public required string Pattern { get; init; }

    /// <summary>How the files are written. <see cref="LogFileFormat.Auto"/> (the default) detects it from each file.</summary>
    public LogFileFormat Format { get; set; } = LogFileFormat.Auto;

    /// <summary>Output template of text files, in Serilog syntax; see <see cref="UseTemplate"/>.</summary>
    public string? Template { get; set; }

    /// <summary>
    /// Reads the files as text written with this Serilog-style output template, e.g.
    /// <c>{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}</c>.
    /// Paste the template the application logs with.
    /// </summary>
    public FileSourceOptions UseTemplate(string template)
    {
        Template = template;
        Format = LogFileFormat.Template;
        return this;
    }

    /// <summary>
    /// Names the matched files, puts them in groups or leaves some out; called once for each file found, with its full
    /// path. Returning <c>null</c> leaves the file out. By default a file is named by its path relative to the content
    /// root, or, outside it, relative to the directory the pattern starts in, which is then its group.
    /// </summary>
    /// <example>
    /// <code>
    /// f.Describe = path => Path.GetFileName(path).StartsWith("debug") ? null : new FileDescription(Path.GetFileName(path)) { Group = "api" };
    /// </code>
    /// </example>
    public Func<string, FileDescription?>? Describe { get; set; }

    /// <summary>
    /// Lists the files that <see cref="Describe"/> gives the same name and group as one source, with their entries
    /// merged by time: the rotated files of one log (<c>app.log</c>, <c>app.log.1</c>, …) or the logs of the replicas
    /// of a service. The source keeps its id as files come and go, also while it has a single file. Merged sources
    /// are listed by group and name rather than newest first, so they keep their places.
    /// </summary>
    /// <example>
    /// <code>
    /// f.Merge = true;
    /// f.Describe = path => new FileDescription(Regex.Replace(Path.GetFileName(path), @"\.\d+$", ""));
    /// </code>
    /// </example>
    public bool Merge { get; set; }

    /// <summary>
    /// The options for the output inside Docker's JSON lines: the template when one is set, otherwise detection.
    /// </summary>
    internal FileSourceOptions InnerOptions() => new()
    {
        Pattern = Pattern,
        Template = Template,
        Format = Template is null ? LogFileFormat.Auto : LogFileFormat.Template,
    };

    /// <summary>Reads the files in the given format instead of detecting it.</summary>
    public FileSourceOptions UseFormat(LogFileFormat format)
    {
        Format = format;
        return this;
    }
}

/// <summary>How a log file is listed in the UI.</summary>
/// <param name="Name">Name of the source.</param>
public sealed record FileDescription(string Name)
{
    /// <summary>Heading the file is listed under, e.g. a service; <c>null</c> lists it without one.</summary>
    public string? Group { get; init; }
}

/// <summary>Formats of log files MonitorWay can read.</summary>
public enum LogFileFormat
{
    /// <summary>Detected from the first lines of each file: CLEF, JSON lines, ASP.NET Core console output, a known text layout, or plain text.</summary>
    Auto,

    /// <summary>Compact Log Event Format (Serilog <c>CompactJsonFormatter</c>), one JSON object per line.</summary>
    Clef,

    /// <summary>JSON object per line with conventional field names (Serilog <c>JsonFormatter</c>, NLog <c>JsonLayout</c>, …).</summary>
    Json,

    /// <summary>Text written with <see cref="FileSourceOptions.Template"/>.</summary>
    Template,

    /// <summary>Any text; levels and times are guessed.</summary>
    Text,

    /// <summary>Output of the ASP.NET Core console logger (<c>info: Category[0]</c> with the message on the next line), e.g. from <c>docker logs</c>.</summary>
    Console,

    /// <summary>
    /// Logs of a container written by Docker's <c>json-file</c> driver (<c>/var/lib/docker/containers/*/*-json.log</c>).
    /// The application's own format inside is detected, or read with <see cref="FileSourceOptions.Template"/> when set.
    /// </summary>
    Docker,
}
