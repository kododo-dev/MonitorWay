using System.ComponentModel;
using Kododo.MonitorWay.Memory;
using Kododo.Reiho.AspNetCore.API;

namespace Kododo.MonitorWay.UI.API;

internal sealed record ServiceRow(string Name, string[] Signals, DateTimeOffset LastSeen);

[Description("Lists the services telemetry came from (service.name): the application itself and those that send over OTLP, with " +
             "what each sent (logs, traces, metrics). Filter by one with service:<name> in a search, or service in the overview.")]
internal sealed record GetServices : IRequest<ServiceRow[]>;

internal sealed class GetServicesHandler(ServiceDirectory services) : IRequestHandler<GetServices, ServiceRow[]>
{
    public Task<ServiceRow[]> HandleAsync(GetServices request, CancellationToken cancellationToken) =>
        Task.FromResult(services.List().Select(s => new ServiceRow(s.Name, s.Signals.ToArray(), s.LastSeen)).ToArray());
}
