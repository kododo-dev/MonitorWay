using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Kododo.MonitorWay.Sources;

/// <summary>
/// All log sources registered with MonitorWay: the memory buffer first, when OpenTelemetry exports to it, then the
/// matched files.
/// </summary>
internal sealed class LogSources(IServiceProvider services)
{
    /// <summary>Separates the ids of sources read together, e.g. <c>file-a1,file-b2</c>.</summary>
    public const char Separator = ',';

    private readonly MemoryLogBuffer? _buffer = services.GetService<MemoryLogBuffer>();
    private MemorySource? _source;

    private MemorySource? Memory => _buffer is { IsExportedTo: true } ? _source ??= new MemorySource(_buffer) : null;
    private readonly FileSourceRegistry? _files = services.GetService<FileSourceRegistry>();

    public IReadOnlyList<ILogSource> All
    {
        get
        {
            var sources = new List<ILogSource>();
            if (Memory is { } memory)
                sources.Add(memory);
            if (_files is not null)
                sources.AddRange(_files.Views);
            return sources;
        }
    }

    /// <summary>
    /// The source with the id, or, for ids joined with <see cref="Separator"/>, those sources merged by time. A merged
    /// source among them contributes its members, so they are merged once. Sources that are gone leave a gap.
    /// </summary>
    public ILogSource? Find(string id)
    {
        if (!id.Contains(Separator))
            return FindOne(id);

        var members = new List<ILogSource?>();
        var names = new List<string>();
        foreach (var part in id.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct())
        {
            var source = FindOne(part);
            if (source is MergedSource merged)
                members.AddRange(merged.Members);
            else
                members.Add(source);
            if (source is not null)
                names.Add(source.Name);
        }
        return members.Count is 0 or > MergedSource.MaxMembers
            ? null
            : new MergedSource(id, string.Join(" + ", names), null, members);
    }

    private ILogSource? FindOne(string id) =>
        id == MemorySource.SourceId ? Memory : _files?.FindView(id);
}
