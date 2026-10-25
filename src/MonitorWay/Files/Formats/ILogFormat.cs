using Kododo.MonitorWay.Core.Model;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Files.Formats;

/// <summary>
/// How entries are laid out in a log file. An entry starts on a line the format recognizes and continues
/// over the following lines that do not start an entry (stack traces, multi-line messages).
/// </summary>
internal interface ILogFormat
{
    /// <summary>Short name shown in the UI, e.g. <c>clef</c>.</summary>
    string Name { get; }

    /// <summary>Whether <paramref name="line"/> (without its line break) starts a new entry.</summary>
    bool IsEntryStart(ReadOnlySpan<byte> line);

    /// <summary>Level and time of the entry starting on <paramref name="line"/>; enough for indexing.</summary>
    void ReadHeader(ReadOnlySpan<byte> line, out LogLevel level, out DateTimeOffset? timestamp);

    /// <summary>
    /// <see cref="IsEntryStart"/> and <see cref="ReadHeader"/> in one call, for indexing, where both are needed for every
    /// line. A format that matches lines with a regular expression overrides it to match only once.
    /// </summary>
    bool TryReadEntryStart(ReadOnlySpan<byte> line, out LogLevel level, out DateTimeOffset? timestamp)
    {
        if (!IsEntryStart(line))
        {
            level = LogLevel.None;
            timestamp = null;
            return false;
        }
        ReadHeader(line, out level, out timestamp);
        return true;
    }

    /// <summary>Parses a whole entry: its first line and continuation lines, line breaks included.</summary>
    LogEntry Parse(ReadOnlySpan<byte> entry, long id);

    /// <summary>
    /// An instance for one indexing thread. A <see cref="System.Text.RegularExpressions.Regex"/> keeps a single
    /// cached matcher, so threads sharing one allocate a new matcher on almost every call; a format built on regular
    /// expressions returns its own copy.
    /// </summary>
    ILogFormat ForThread() => this;

    /// <summary>The category of the entry starting on <paramref name="line"/>, for the category list; <c>null</c> when the format has none.</summary>
    string? ReadCategory(ReadOnlySpan<byte> line, CategoryNames names) => null;
}

/// <summary>Level names used by the common .NET logging libraries, in full and abbreviated forms.</summary>
internal static class LevelNames
{
    /// <remarks>Called for every entry while indexing, so it works on a stack copy instead of allocating strings.</remarks>
    public static LogLevel Parse(ReadOnlySpan<char> name)
    {
        name = name.Trim();
        if (name.IsEmpty || name.Length > 16)
            return LogLevel.None;
        Span<char> upper = stackalloc char[name.Length];
        name.ToUpperInvariant(upper);
        return upper switch
        {
            "VERBOSE" or "VRB" or "TRACE" or "TRC" or "TRCE" or "V" or "T" => LogLevel.Trace,
            "DEBUG" or "DBG" or "DBUG" or "D" => LogLevel.Debug,
            "INFORMATION" or "INFO" or "INF" or "I" => LogLevel.Information,
            "WARNING" or "WARN" or "WRN" or "W" => LogLevel.Warning,
            "ERROR" or "ERR" or "FAIL" or "E" => LogLevel.Error,
            "FATAL" or "FTL" or "CRITICAL" or "CRIT" or "CRT" or "F" or "C" => LogLevel.Critical,
            _ => LogLevel.None,
        };
    }
}

/// <summary>Helpers shared by the formats.</summary>
internal static class EntryText
{
    /// <summary>The first line of an entry, without its line break.</summary>
    public static ReadOnlySpan<byte> FirstLine(ReadOnlySpan<byte> entry)
    {
        var end = entry.IndexOf((byte)'\n');
        return (end < 0 ? entry : entry[..end]).TrimEnd("\r"u8);
    }

    /// <summary>The lines after the first one, or empty.</summary>
    public static ReadOnlySpan<byte> Continuation(ReadOnlySpan<byte> entry)
    {
        var end = entry.IndexOf((byte)'\n');
        return end < 0 ? default : entry[(end + 1)..].TrimEnd("\r\n"u8);
    }
}
