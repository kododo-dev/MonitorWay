using System.Text;
using System.Threading.Channels;

namespace Kododo.MonitorWay.Demo.Web;

/// <summary>
/// Writes logs to a daily text file laid out like Serilog's file sink with <see cref="Template"/>, so the demo
/// shows a text log with multi-line exceptions. A real application would use Serilog.
/// </summary>
[ProviderAlias("TextFile")]
internal sealed class TextFileLoggerProvider : ILoggerProvider
{
    public const string Template = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    private readonly string _directory;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;

    public TextFileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteAsync);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
        _lines.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    internal static string Format(DateTimeOffset time, LogLevel level, string category, string message, string? exception)
    {
        var text = new StringBuilder()
            .Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append(" [").Append(Short(level)).Append("] ")
            .Append(category).Append(": ").Append(message).Append('\n');
        if (exception is not null)
            text.Append(exception.ReplaceLineEndings("\n")).Append('\n');
        return text.ToString();
    }

    private static string Short(LogLevel level) => level switch
    {
        LogLevel.Trace => "VRB",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "FTL",
    };

    private async Task WriteAsync()
    {
        StreamWriter? file = null;
        var day = DateOnly.MinValue;
        try
        {
            while (await _lines.Reader.WaitToReadAsync())
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (today != day || file is null)
                {
                    if (file is not null)
                        await file.DisposeAsync();
                    day = today;
                    file = new StreamWriter(new FileStream(Path.Combine(_directory, $"shop-{day:yyyyMMdd}.log"), FileMode.Append,
                        FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
                }
                while (_lines.Reader.TryRead(out var line))
                    await file.WriteAsync(line);
                await file.FlushAsync();
            }
        }
        finally
        {
            if (file is not null)
                await file.DisposeAsync();
        }
    }

    private sealed class Logger(TextFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                provider._lines.Writer.TryWrite(Format(DateTimeOffset.Now, logLevel, category, formatter(state, exception), exception?.ToString()));
        }
    }
}
