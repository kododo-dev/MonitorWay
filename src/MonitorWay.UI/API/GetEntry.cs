using System.ComponentModel;
using Kododo.MonitorWay.Sources;
using Kododo.MonitorWay.UI.DTO;
using Kododo.Reiho.AspNetCore.API;

namespace Kododo.MonitorWay.UI.API;

/// <summary>An entry with all its details, or <c>null</c> when it no longer exists.</summary>
[Description("Reads one log entry in full: message, message template, exception with stack trace, properties, trace and span ids.")]
internal sealed record GetEntry(
    [property: Description("Id of a source from the list of sources; several ids joined with commas read those sources together, ordered by time.")] string SourceId,
    [property: Description("Id of the entry, as read with the entries or the entries around.")] long Id) : IRequest<EntryDetail?>;

internal sealed class GetEntryHandler(LogSources sources) : IRequestHandler<GetEntry, EntryDetail?>
{
    public Task<EntryDetail?> HandleAsync(GetEntry request, CancellationToken cancellationToken)
    {
        var entry = sources.Find(request.SourceId)?.Find(request.Id);
        return Task.FromResult(entry is null ? null : EntryDetail.From(entry));
    }
}
