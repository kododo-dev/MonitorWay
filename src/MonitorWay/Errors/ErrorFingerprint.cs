using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kododo.MonitorWay.Errors;

/// <summary>
/// What makes two exceptions the same error: the type and the top frames of the application's own code. The message
/// is left out, because it carries ids and paths that differ every time.
/// </summary>
/// <remarks>
/// The frames come from the exception as text (<see cref="Exception.ToString"/>), which is what both a log entry and
/// an <c>exception</c> span event carry, so the two reports of one exception get the same fingerprint. Of an exception
/// with inner exceptions, the outer one's frames count. The operation is used only for an exception without frames:
/// a log entry and a span often see different operations for the same exception.
/// </remarks>
internal static partial class ErrorFingerprint
{
    private const int MaxFrames = 5;

    // Frames of these namespaces say where the framework was, not where the application failed.
    private static readonly string[] FrameworkNamespaces =
        ["System.", "Microsoft.", "OpenTelemetry.", "Kododo.Reiho.", "Kododo.Way.Shell.", "Npgsql.", "Polly.", "Grpc.",
         "Kododo.MonitorWay.Core.", "Kododo.MonitorWay.Memory.", "Kododo.MonitorWay.Telemetry.", "Kododo.MonitorWay.UI."];

    public static (string Fingerprint, IReadOnlyList<string> Frames) Compute(string type, string? text, string? operation) =>
        Compute(type, text, operation, out _);

    /// <param name="fullType">
    /// The type with its namespace. Some senders give the name alone (the OTLP log exporter of .NET:
    /// <c>IOException</c>), others the full name (<c>System.IO.IOException</c>); the first line of the text tells it.
    /// </param>
    public static (string Fingerprint, IReadOnlyList<string> Frames) Compute(string type, string? text, string? operation, out string fullType)
    {
        type = fullType = FullType(type, text);
        var all = Frames(text);
        var application = all.Where(f => !Array.Exists(FrameworkNamespaces, ns => f.StartsWith(ns, StringComparison.Ordinal))).ToList();
        var frames = (application.Count > 0 ? application : all).Take(MaxFrames).ToArray();

        var key = new StringBuilder(type);
        foreach (var frame in frames)
            key.Append('\n').Append(frame);
        if (frames.Length == 0 && operation is not null)
            key.Append("\n@").Append(operation);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString()));
        return (Convert.ToHexString(hash, 0, 8).ToLowerInvariant(), frames);
    }

    /// <summary>The full name of the type from the first line of the text (<c>System.IO.IOException: message</c>), when it ends with it.</summary>
    private static string FullType(string type, string? text)
    {
        if (string.IsNullOrEmpty(text) || type.Contains('.'))
            return type;
        var end = text.IndexOfAny([':', '\r', '\n']);
        var first = (end < 0 ? text : text[..end]).Trim();
        return first.EndsWith("." + type, StringComparison.Ordinal) && !first.Contains(' ') ? first : type;
    }

    /// <summary>The methods of the outer exception's frames, top first, without arguments, files and compiler names.</summary>
    internal static List<string> Frames(string? text)
    {
        var frames = new List<string>();
        if (string.IsNullOrEmpty(text))
            return frames;

        // The frames of the inner exceptions come first, each closed by this line.
        var lines = text.Split('\n');
        var start = Array.FindLastIndex(lines, l => l.TrimStart().StartsWith("--- End of inner exception", StringComparison.Ordinal)) + 1;
        for (var i = start; i < lines.Length; i++)
        {
            // "   at Shop.Orders.Place(Int32 id) in C:\src\Orders.cs:line 42", with "at" in the runtime's language.
            var match = FrameLine().Match(lines[i]);
            if (match.Success)
                frames.Add(Normalize(match.Groups["method"].Value));
        }
        return frames;
    }

    private static string Normalize(string method)
    {
        method = AsyncStateMachine().Replace(method, ".$1");
        method = Lambda().Replace(method, "$1.lambda");
        method = DisplayClass().Replace(method, "");
        return method.Replace("..", ".", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^\s+\S+\s+(?<method>[^\s(]+)\(")]
    private static partial Regex FrameLine();

    // Shop.Orders.<PlaceAsync>d__5.MoveNext -> Shop.Orders.PlaceAsync
    [GeneratedRegex(@"\.<([^>]+)>d__\d+\.MoveNext$")]
    private static partial Regex AsyncStateMachine();

    // <Main>b__0_1 -> Main.lambda
    [GeneratedRegex(@"<([^>]+)>b__[\d_]+")]
    private static partial Regex Lambda();

    [GeneratedRegex(@"<>c(__DisplayClass[\d_]+)?")]
    private static partial Regex DisplayClass();
}
