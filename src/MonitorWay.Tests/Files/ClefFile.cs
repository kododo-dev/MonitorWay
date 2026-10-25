using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Tests.Files;

/// <summary>A temporary directory with CLEF files written the way Serilog's compact formatter writes them.</summary>
internal sealed class ClefFile : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    public ClefFile(string name = "app.clef")
    {
        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "monitorway-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Path = System.IO.Path.Combine(Directory, name);
        File.WriteAllBytes(Path, []);
    }

    public string Directory { get; }
    public string Path { get; }

    public static string Line(int index, LogLevel level = LogLevel.Information, string? message = null, string? exception = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["@t"] = Start.AddSeconds(index).ToString("O"),
            ["@mt"] = "Order {OrderId} handled",
        };
        if (message is not null)
            fields["@m"] = message;
        if (level != LogLevel.Information)
            fields["@l"] = level switch { LogLevel.Trace => "Verbose", LogLevel.Critical => "Fatal", _ => level.ToString() };
        if (exception is not null)
            fields["@x"] = exception;
        fields["OrderId"] = index;
        fields["SourceContext"] = "Shop.Orders";
        return JsonSerializer.Serialize(fields);
    }

    public static DateTimeOffset TimeOf(int index) => Start.AddSeconds(index);

    public ClefFile Append(params string[] lines) => AppendRaw(string.Concat(lines.Select(l => l + "\n")));

    public ClefFile AppendRaw(string text)
    {
        using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(Encoding.UTF8.GetBytes(text));
        return this;
    }

    /// <summary>Lines 0..count-1; every <paramref name="errorEvery"/>-th one is an error.</summary>
    public ClefFile AppendMany(int count, int errorEvery = 0, int from = 0)
    {
        var text = new StringBuilder();
        for (var i = from; i < from + count; i++)
            text.Append(Line(i, errorEvery > 0 && i % errorEvery == 0 ? LogLevel.Error : LogLevel.Information)).Append('\n');
        return AppendRaw(text.ToString());
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
