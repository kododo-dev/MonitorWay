using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kododo.MonitorWay.Core.Model;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Files.Formats;

/// <summary>
/// Any text file, used when no other format fits. A line starts an entry unless it is indented or looks like
/// part of a stack trace; the level is guessed from a level word near the start of the line and the time from
/// a leading ISO-like timestamp.
/// </summary>
internal sealed class PlainTextFormat : ILogFormat
{
    private static readonly Regex Timestamp = new(
        @"^\[?(?<t>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:\s?(?:Z|[+-]\d{2}:?\d{2}))?)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // Upper-case level words, the ASP.NET Core console prefixes (`fail:`) and capitalized names in brackets
    // or between pipes. Plain "error" inside a sentence is not a level.
    private static readonly Regex Level = new(
        @"\b(?<l>TRACE|TRCE|VERBOSE|VRB|TRC|DEBUG|DBUG|DBG|INFO|INFORMATION|INF|WARN|WARNING|WRN|ERROR|ERR|FAIL|FATAL|FTL|CRITICAL|CRIT|CRT)\b" +
        @"|^(?<l>trce|dbug|info|warn|fail|crit):" +
        @"|[\[|]\s*(?<l>Trace|Verbose|Debug|Information|Info|Warning|Warn|Error|Fatal|Critical)\s*[\]|]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private const int LevelSearchLength = 120;

    public static PlainTextFormat Instance { get; } = new();

    public string Name => "text";

    public bool IsEntryStart(ReadOnlySpan<byte> line)
    {
        if (line.IsEmpty || line[0] is (byte)' ' or (byte)'\t')
            return false;
        return !line.StartsWith("at "u8) && !line.StartsWith("---"u8) && !line.StartsWith("Caused by"u8);
    }

    public void ReadHeader(ReadOnlySpan<byte> line, out LogLevel level, out DateTimeOffset? timestamp)
    {
        var text = Encoding.UTF8.GetString(line[..Math.Min(line.Length, LevelSearchLength * 2)]);
        level = GuessLevel(text);
        timestamp = GuessTimestamp(text);
    }

    public LogEntry Parse(ReadOnlySpan<byte> entry, long id)
    {
        var first = Encoding.UTF8.GetString(EntryText.FirstLine(entry));
        var rest = Encoding.UTF8.GetString(EntryText.Continuation(entry));
        return new LogEntry(id, GuessTimestamp(first), GuessLevel(first), null, first, null, rest.Length > 0 ? rest : null, [], null, null);
    }

    private static LogLevel GuessLevel(string line)
    {
        var match = Level.Match(line, 0, Math.Min(line.Length, LevelSearchLength));
        // An unrecognized line is still worth showing; Information keeps it out of the error views.
        return match.Success ? LevelNames.Parse(match.Groups["l"].ValueSpan) : LogLevel.Information;
    }

    private static DateTimeOffset? GuessTimestamp(string line)
    {
        var match = Timestamp.Match(line);
        return match.Success &&
               DateTimeOffset.TryParse(match.Groups["t"].Value.Replace(',', '.'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value)
            ? value
            : null;
    }
}
