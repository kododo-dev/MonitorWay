using System.Text.RegularExpressions;

namespace Kododo.MonitorWay.Files.Formats;

/// <summary>Recognizes where an exception starts in the lines that follow a text log message.</summary>
internal static class ExceptionText
{
    /// <summary>
    /// A stack frame (<c>   at …</c>), an inner exception marker (<c> ---&gt; …</c>, <c>--- End of …</c>), or the first line of
    /// <see cref="Exception.ToString"/>: the type name, with the HRESULT .NET adds for some exceptions
    /// (<c>Npgsql.NpgsqlException (0x80004005): …</c>), then a colon or the end of the line. Nested types are
    /// written with <c>+</c> and generic ones with a backtick.
    /// </summary>
    public static readonly Regex Start = new(
        @"^(\s+at\s|[\w.`+]+(Exception|Error)(\s*\(0x[0-9A-Fa-f]{1,8}\))?(\s*:|\s*$)|\s*---\s)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
