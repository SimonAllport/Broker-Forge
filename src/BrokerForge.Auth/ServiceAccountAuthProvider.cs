using System.Net.Http.Headers;
using Azure.Core;
using Azure.Identity;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;

namespace BrokerForge.Auth;

/// <summary>Service Account mode (HLD 9.1): a bearer token for the host's own identity, for the instance's Auth.Scope.</summary>
public sealed class ServiceAccountAuthProvider(TokenCredential credential, HttpClient http) : IAuthProvider
{
    public AuthMode Mode => AuthMode.ServiceAccount;

    public async Task<IAuthenticatedChannel> AuthenticateAsync(ServiceInstance instance, CallerContext caller, string correlationId, CancellationToken ct)
    {
        var auth = instance.Auth;
        if (auth.Mode != AuthMode.ServiceAccount)
            throw new ConfigurationException($"Service instance {instance.Name} uses {auth.Mode}, not ServiceAccount.");
        if (string.IsNullOrEmpty(auth.Scope))
            throw new CredentialUnavailableException($"Service instance {instance.Name} has no Auth.Scope for ServiceAccount mode.");

        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(new TokenRequestContext(new[] { auth.Scope }), ct);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new CredentialUnavailableException($"The host identity could not get a token for scope {auth.Scope}.", ex);
        }
        var header = new AuthenticationHeaderValue("Bearer", token.Token);
        return new HttpChannel(http, instance, request => request.Headers.Authorization = header, token.Token);
    }
}
