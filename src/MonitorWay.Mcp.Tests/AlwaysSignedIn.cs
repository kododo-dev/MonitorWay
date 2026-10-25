using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kododo.MonitorWay.Mcp.Tests;

/// <summary>Signs every request in, so a test can require authorization without a real sign-in.</summary>
internal sealed class AlwaysSignedIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "agent")], Scheme.Name)), Scheme.Name)));
}
