using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace Kododo.MonitorWay.Demo.Web;

/// <summary>
/// Writes logs to a daily CLEF file, the format Serilog's <c>CompactJsonFormatter</c> produces, so the
/// demo has a growing log file to show next to the in-memory buffer. A real application would use Serilog.
/// </summary>
[ProviderAlias("ClefFile")]
internal sealed class ClefFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly string _directory;
    private readonly Channel<byte[]> _lines = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private IExternalScopeProvider? _scopes;

    public ClefFileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteAsync);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
        _lines.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private async Task WriteAsync()
    {
        FileStream? file = null;
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
                    file = new FileStream(Path.Combine(_directory, $"shop-{day:yyyyMMdd}.clef"), FileMode.Append,
                        FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
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

    internal static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "Verbose",
        LogLevel.Critical => "Fatal",
        _ => level.ToString(),
    };

    private sealed class Logger(ClefFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("@t", DateTimeOffset.Now);
                if (state is IEnumerable<KeyValuePair<string, object?>> values &&
                    values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value is string template)
                    json.WriteString("@mt", template);
                json.WriteString("@m", formatter(state, exception));
                if (logLevel != LogLevel.Information)
                    json.WriteString("@l", LevelName(logLevel));
                if (exception is not null)
                    json.WriteString("@x", exception.ToString());
                if (Activity.Current is { } activity)
                {
                    json.WriteString("@tr", activity.TraceId.ToHexString());
                    json.WriteString("@sp", activity.SpanId.ToHexString());
                }
                if (eventId.Id != 0)
                    json.WriteNumber("EventId", eventId.Id);

                if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                {
                    foreach (var (key, value) in properties)
                    {
                        if (key != "{OriginalFormat}")
                            WriteValue(json, key, value);
                    }
                }
                provider._scopes?.ForEachScope(static (scope, writer) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> scopeValues)
                    {
                        foreach (var (key, value) in scopeValues)
                            WriteValue(writer, key, value);
                    }
                }, json);
                json.WriteString("SourceContext", category);
                json.WriteEndObject();
            }
            buffer.WriteByte((byte)'\n');
            provider._lines.Writer.TryWrite(buffer.ToArray());
        }

        private static void WriteValue(Utf8JsonWriter json, string key, object? value)
        {
            var name = key.StartsWith('@') ? "@" + key : key;
            switch (value)
            {
                case null: json.WriteNull(name); break;
                case bool b: json.WriteBoolean(name, b); break;
                case int or long or decimal or double or float:
                    json.WritePropertyName(name);
                    json.WriteRawValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
                    break;
                default: json.WriteString(name, value.ToString()); break;
            }
        }
    }
}
