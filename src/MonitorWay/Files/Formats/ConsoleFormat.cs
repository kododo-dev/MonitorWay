using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kododo.MonitorWay.Core.Model;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Files.Formats;

/// <summary>
/// Output of the ASP.NET Core console logger with the default (simple) formatter, as found in redirected console
/// output and <c>docker logs</c>:
/// <code>
/// info: Microsoft.Hosting.Lifetime[14]
///       Now listening on: http://[::]:8080
/// </code>
/// The first line holds the level, category and event id, optionally after a timestamp (<c>TimestampFormat</c>);
/// the message and the exception follow on indented lines.
/// </summary>
internal sealed class ConsoleFormat : ILogFormat
{
    // Per instance: a Regex caches one matcher, so each indexing thread gets its own copy (ForThread).
    private readonly Regex _start = new(
        @"^(?:(?<timestamp>\S.*?)\s+)?(?<level>trce|dbug|info|warn|fail|crit): (?<category>[^\s\[]+)\[(?<event>-?\d+)\]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static ConsoleFormat Instance { get; } = new();

    public string Name => "console";

    public bool IsEntryStart(ReadOnlySpan<byte> line)
    {
        if (line.Length < 8 || line[0] is (byte)' ' or (byte)'\t')
            return false;
        Span<char> chars = line.Length <= 1024 ? stackalloc char[line.Length] : new char[line.Length];
        return _start.IsMatch(chars[..Encoding.UTF8.GetChars(line, chars)]);
    }

    public void ReadHeader(ReadOnlySpan<byte> line, out LogLevel level, out DateTimeOffset? timestamp)
    {
        var match = _start.Match(Encoding.UTF8.GetString(line));
        level = match.Success ? LevelNames.Parse(match.Groups["level"].ValueSpan) : LogLevel.None;
        timestamp = match.Success && match.Groups["timestamp"].Success ? ParseTimestamp(match.Groups["timestamp"].Value) : null;
    }

    public ILogFormat ForThread() => new ConsoleFormat();

    public string? ReadCategory(ReadOnlySpan<byte> line, CategoryNames names)
    {
        var match = _start.Match(Encoding.UTF8.GetString(line));
        return match.Success ? names.Get(match.Groups["category"].ValueSpan) : null;
    }

    public LogEntry Parse(ReadOnlySpan<byte> entry, long id)
    {
        var first = Encoding.UTF8.GetString(EntryText.FirstLine(entry));
        var match = _start.Match(first);
        var lines = Encoding.UTF8.GetString(EntryText.Continuation(entry)).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        if (!match.Success)
            return new LogEntry(id, null, LogLevel.None, null, string.Join('\n', lines.Prepend(first)), null, null, [], null, null);

        // The message is indented by six spaces; an exception keeps its own indentation after them.
        var indent = lines.Where(l => l.Length > 0).Select(l => l.Length - l.TrimStart(' ').Length).DefaultIfEmpty(0).Min();
        var body = lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()).ToArray();
        var exceptionAt = Array.FindIndex(body, l => ExceptionText.Start.IsMatch(l));
        var message = string.Join('\n', exceptionAt < 0 ? body : body[..exceptionAt]).TrimEnd();
        var exception = exceptionAt < 0 ? null : string.Join('\n', body[exceptionAt..]).TrimEnd();

        return new LogEntry(
            id,
            match.Groups["timestamp"].Success ? ParseTimestamp(match.Groups["timestamp"].Value) : null,
            LevelNames.Parse(match.Groups["level"].ValueSpan),
            match.Groups["category"].Value,
            message,
            null,
            exception,
            [new("EventId", match.Groups["event"].Value)],
            null,
            null);
    }

    private static DateTimeOffset? ParseTimestamp(string text) =>
        DateTimeOffset.TryParse(text.Trim('[', ']', ' ').Replace(',', '.'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value)
            ? value
            : null;
}
