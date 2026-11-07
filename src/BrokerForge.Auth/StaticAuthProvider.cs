using System.Net.Http.Headers;
using System.Text;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;

namespace BrokerForge.Auth;

/// <summary>
/// Static mode (HLD 9.1): one shared credential from the instance's secret reference, sent as an API key
/// header (apiKeyHeader), an API key query parameter (apiKeyQuery) or HTTP Basic (basic).
/// </summary>
public sealed class StaticAuthProvider(ISecretResolver secrets, HttpClient http) : IAuthProvider
{
    public AuthMode Mode => AuthMode.Static;

    public async Task<IAuthenticatedChannel> AuthenticateAsync(ServiceInstance instance, CallerContext caller, string correlationId, CancellationToken ct)
    {
        var auth = instance.Auth;
        if (auth.Mode != AuthMode.Static)
            throw new ConfigurationException($"Service instance {instance.Name} uses {auth.Mode}, not Static.");
        if (string.IsNullOrEmpty(auth.SecretRef))
            throw new CredentialUnavailableException($"Service instance {instance.Name} has no Auth.SecretRef for Static mode.");

        (Action<HttpRequestMessage> Attach, string[] Values) attach = auth.StaticKind switch
        {
            "apiKeyHeader" when string.IsNullOrEmpty(auth.HeaderName) => throw Config(instance, "apiKeyHeader needs Auth.HeaderName"),
            "apiKeyQuery" when string.IsNullOrEmpty(auth.QueryParam) => throw Config(instance, "apiKeyQuery needs Auth.QueryParam"),
            "basic" when string.IsNullOrEmpty(auth.Username) || auth.Username.Contains(':') => throw Config(instance, "basic needs Auth.Username without a colon"),
            "apiKeyHeader" or "apiKeyQuery" or "basic" => await Attach(instance, auth, ct),
            _ => throw Config(instance, $"Auth.StaticKind must be apiKeyHeader, apiKeyQuery or basic; got `{auth.StaticKind}`"),
        };
        return new HttpChannel(http, instance, attach.Attach, attach.Values);
    }

    private async Task<(Action<HttpRequestMessage>, string[])> Attach(ServiceInstance instance, AuthConfig auth, CancellationToken ct)
    {
        var secret = await secrets.GetAsync(SecretRef.Parse(auth.SecretRef!), instance, ct);
        if (secret.Contains('\r') || secret.Contains('\n'))
            throw new CredentialUnavailableException($"The secret named by {auth.SecretRef} contains a line break.");

        switch (auth.StaticKind)
        {
            case "apiKeyHeader":
                return (request => request.Headers.TryAddWithoutValidation(auth.HeaderName!, secret), new[] { secret });
            case "apiKeyQuery":
                var pair = Uri.EscapeDataString(auth.QueryParam!) + "=" + Uri.EscapeDataString(secret);
                return (request =>
                {
                    var uri = request.RequestUri!;
                    var builder = new UriBuilder(uri) { Query = string.IsNullOrEmpty(uri.Query) ? pair : uri.Query.TrimStart('?') + "&" + pair };
                    request.RequestUri = builder.Uri;
                }, new[] { secret, Uri.EscapeDataString(secret) });
            default:
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(auth.Username + ":" + secret));
                var basic = new AuthenticationHeaderValue("Basic", encoded);
                return (request => request.Headers.Authorization = basic, new[] { secret, encoded });
        }
    }

    private static CredentialUnavailableException Config(ServiceInstance instance, string message) =>
        new($"Service instance {instance.Name}: {message}.");
}
