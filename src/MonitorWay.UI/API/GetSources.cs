using System.ComponentModel;
using Kododo.MonitorWay.Sources;
using Kododo.MonitorWay.UI.DTO;
using Kododo.Reiho.AspNetCore.API;

namespace Kododo.MonitorWay.UI.API;

[Description("Lists the log sources: the in-memory buffer of the application and the log files (or containers), with their groups. Start here to get a source id.")]
internal sealed record GetSources : IRequest<Source[]>;

internal sealed class GetSourcesHandler(LogSources sources) : IRequestHandler<GetSources, Source[]>
{
    public Task<Source[]> HandleAsync(GetSources request, CancellationToken cancellationToken) =>
        Task.FromResult(sources.All.Select(s => new Source(s.Id, s.Name, s.Kind, s.Group)).ToArray());
}
