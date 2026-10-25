namespace Kododo.MonitorWay.UI;

/// <summary>
/// Options of the MonitorWay web UI. The title, the user and anonymous access are the shell's:
/// <c>services.AddWayShell(o => ...)</c>.
/// </summary>
public sealed class MonitorWayUiOptions
{
    /// <summary>
    /// Keeps the telemetry of requests to the MonitorWay UI itself. Off by default: the UI polls the server every few
    /// seconds, and its own requests would crowd the data it shows.
    /// </summary>
    public bool CaptureOwnRequests { get; set; }
}
