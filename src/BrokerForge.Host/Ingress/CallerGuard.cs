using System.Security.Claims;
using BrokerForge.Contracts;
using Microsoft.Extensions.Options;

namespace BrokerForge.Host.Ingress;

/// <summary>The token's caller or environment is not allowed. Reported as 403.</summary>
public sealed class CallerRejectedException(string message) : Exception(message);

/// <summary>
/// Maps the token's azp (or v1 appid) to its one allowed environment and checks the request names that
/// environment (D-09: the token carries no environment claim).
/// </summary>
public sealed class CallerGuard(IOptions<TrustOptions> trust)
{
    public void Check(ClaimsPrincipal user, CanonicalRequest request)
    {
        var client = user.FindFirstValue("azp") ?? user.FindFirstValue("appid");
        var caller = trust.Value.AllowedCallers.FirstOrDefault(c => string.Equals(c.ClientId, client, StringComparison.OrdinalIgnoreCase))
            ?? throw new CallerRejectedException($"Client {client ?? "(none)"} is not an allowed BrokerForge caller.");
        if (!string.Equals(caller.EnvironmentId, request.EnvironmentId, StringComparison.OrdinalIgnoreCase))
            // The reply goes back to the caller, so it names the environment asked for, not the one configured.
            throw new CallerRejectedException($"Client {client} is not allowed for environment {request.EnvironmentId ?? "(none)"}.");
    }
}
