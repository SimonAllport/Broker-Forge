using System.Net.Http.Headers;
using System.Text;
using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;
using BrokerForge.Contracts;

namespace BrokerForge.Auth.PerUser;

/// <summary>
/// SSO (HLD 9.1, D-28): the username and secret the caller entered on the connect page, sent as HTTP Basic or
/// as an API key header (Auth:SsoKind). No credential is a ConnectionRequiredException. Credentials are read
/// under <see cref="CredentialPartition"/>, so a design instance in one environment never sees another's.
/// </summary>
public sealed class SsoAuthProvider(IUserCredentialStore store, Func<ServiceInstance, string> connectUrl, HttpClient http) : IAuthProvider
{
    public AuthMode Mode => AuthMode.Sso;

    public async Task<IAuthenticatedChannel> AuthenticateAsync(ServiceInstance instance, CallerContext caller, string correlationId, CancellationToken ct)
    {
        var auth = instance.Auth;
        if (auth.Mode != AuthMode.Sso)
            throw new ConfigurationException($"Service instance {instance.Name} uses {auth.Mode}, not Sso.");
        var user = ConnectionRequired.User(instance, caller, correlationId, connectUrl);
        UserCredential? stored;
        try
        {
            stored = await store.GetAsync(CredentialPartition.For(instance), user, ct);
        }
        catch (CredentialUnreadableException)
        {
            throw ConnectionRequired.Because(instance, "the stored credential could not be read", correlationId, connectUrl);
        }
        var credential = stored switch
        {
            null => throw ConnectionRequired.Because(instance, "there is no credential for this user", correlationId, connectUrl),
            { Status: not CredentialStatus.Connected } c => throw ConnectionRequired.Because(instance, $"the stored credential is {c.Status.ToString().ToLowerInvariant()}", correlationId, connectUrl),
            { Kind: not CredentialKind.Sso } c => throw ConnectionRequired.Because(instance, $"the stored credential was connected for another mode ({c.Kind})", correlationId, connectUrl),
            { Secret: null } => throw ConnectionRequired.Because(instance, "the stored credential has no secret", correlationId, connectUrl),
            var c => c,
        };
        var secret = credential.Secret!;
        if (secret.Contains('\r') || secret.Contains('\n') || (credential.Username?.IndexOfAny(new[] { '\r', '\n', ':' }) ?? -1) >= 0)
            throw ConnectionRequired.Because(instance, "the stored credential contains a character it cannot be sent with", correlationId, connectUrl);

        if (auth.SsoKind == "apiKeyHeader")
            return new HttpChannel(http, instance, request => request.Headers.TryAddWithoutValidation(auth.HeaderName!, secret), secret);
        if (credential.Username == null)
            throw ConnectionRequired.Because(instance, "the stored credential has no username", correlationId, connectUrl);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.Username + ":" + secret));
        var basic = new AuthenticationHeaderValue("Basic", encoded);
        return new HttpChannel(http, instance, request => request.Headers.Authorization = basic, secret, encoded);
    }
}
