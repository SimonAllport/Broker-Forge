using System.Net.Http.Headers;
using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;
using BrokerForge.Contracts;
using Microsoft.Extensions.Logging;

namespace BrokerForge.Auth.PerUser;

/// <summary>
/// OAuth and Impersonate (HLD 9.1, D-28): the caller's stored access token as a bearer, refreshed when it has
/// under a minute left. No credential, an expired or revoked one, or a refused refresh is a
/// ConnectionRequiredException. Nothing here can reach another identity. Credentials are kept under
/// <see cref="CredentialPartition"/>, and <c>resources</c> looks up the instance's OAuth resource in its own environment.
/// </summary>
public sealed class DelegatedTokenAuthProvider(
    AuthMode mode,
    IUserCredentialStore store,
    Func<ServiceInstance, CancellationToken, Task<OAuthResource>> resources,
    TokenEndpointClient tokens,
    Func<ServiceInstance, string> connectUrl,
    HttpClient http,
    ILogger log,
    TimeProvider time,
    TimeSpan? refusalGrace = null) : IAuthProvider
{
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a refused refresh waits for another replica's save before marking the user expired: with
    /// single-use refresh tokens the refusal can answer before the winner's success does.
    /// </summary>
    public static readonly TimeSpan DefaultRefusalGrace = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _refusalGrace = refusalGrace ?? DefaultRefusalGrace;
    private readonly RefreshGate _gate = new();

    public AuthMode Mode => mode is AuthMode.OAuth or AuthMode.Impersonate
        ? mode
        : throw new ArgumentOutOfRangeException(nameof(mode), "DelegatedTokenAuthProvider serves OAuth and Impersonate only.");

    private CredentialKind Kind => mode == AuthMode.OAuth ? CredentialKind.OAuth : CredentialKind.Impersonate;

    public async Task<IAuthenticatedChannel> AuthenticateAsync(ServiceInstance instance, CallerContext caller, string correlationId, CancellationToken ct)
    {
        try
        {
            return await AuthenticateCoreAsync(instance, caller, correlationId, ct);
        }
        catch (CredentialUnreadableException)
        {
            throw ConnectionRequired.Because(instance, "the stored credential could not be read", correlationId, connectUrl);
        }
    }

    private async Task<IAuthenticatedChannel> AuthenticateCoreAsync(ServiceInstance instance, CallerContext caller, string correlationId, CancellationToken ct)
    {
        if (instance.Auth.Mode != Mode)
            throw new ConfigurationException($"Service instance {instance.Name} uses {instance.Auth.Mode}, not {Mode}.");
        var user = ConnectionRequired.User(instance, caller, correlationId, connectUrl);
        var key = CredentialPartition.For(instance);

        var credential = Usable(instance, await store.GetAsync(key, user, ct), correlationId);
        if (!ExpiresSoon(credential))
            return Channel(instance, credential.AccessToken!);

        // The gate stops two calls on this replica refreshing at once. Across replicas, and against the user's
        // own disconnect or reconnect, every write below is conditional on the row version read here.
        using (await _gate.EnterAsync(key, user, ct))
        {
            var row = await store.GetVersionedAsync(key, user, ct);
            credential = Usable(instance, row?.Credential, correlationId);
            if (!ExpiresSoon(credential))
                return Channel(instance, credential.AccessToken!);
            if (credential.RefreshToken == null)
            {
                await store.SetStatusIfUnchangedAsync(key, user, CredentialStatus.Expired, row!.Version, ct);
                throw ConnectionRequired.Because(instance, "the stored token has expired and cannot be refreshed", correlationId, connectUrl);
            }

            TokenSet refreshed;
            try
            {
                refreshed = await tokens.RefreshAsync(await resources(instance, ct), instance, credential.RefreshToken, ct);
            }
            catch (RefreshRejectedException)
            {
                // Refused because another replica used this refresh token first, or the user reconnected meanwhile:
                // the row then holds a newer grant, and that grant is the answer.
                if (await NewerGrant(key, user, credential, ct) is { } newer)
                    return Channel(instance, newer.AccessToken!);
                await Task.Delay(_refusalGrace, ct);
                if (await NewerGrant(key, user, credential, ct) is { } later)
                    return Channel(instance, later.AccessToken!);
                if (!await store.SetStatusIfUnchangedAsync(key, user, CredentialStatus.Expired, row!.Version, ct)
                    && await NewerGrant(key, user, credential, ct) is { } last)
                    return Channel(instance, last.AccessToken!);
                log.LogWarning("The {Mode} refresh for {Instance} in environment {EnvironmentId} user {UserObjectId} was refused; marked expired", Mode, instance.Name, instance.EnvironmentId, user);
                throw ConnectionRequired.Because(instance, "the stored token has expired and the source refused to refresh it", correlationId, connectUrl);
            }

            var updated = credential with
            {
                Status = CredentialStatus.Connected,
                AccessToken = refreshed.AccessToken,
                RefreshToken = refreshed.RefreshToken ?? credential.RefreshToken,
                ExpiresAt = refreshed.ExpiresAt,
            };
            var version = row!.Version;
            // Two attempts: the second covers a racing replica that marked this same grant expired after its
            // refresh of the same token was refused (only the status changed, so the refreshed grant still stands).
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (await store.ReplaceIfUnchangedAsync(key, user, updated, version, ct))
                {
                    log.LogInformation("Refreshed the {Mode} token for {Instance} in environment {EnvironmentId} user {UserObjectId}", Mode, instance.Name, instance.EnvironmentId, user);
                    return Channel(instance, refreshed.AccessToken);
                }
                var now = await store.GetVersionedAsync(key, user, ct);
                if (now == null)
                    throw ConnectionRequired.Because(instance, "the user disconnected while the token was being refreshed", correlationId, connectUrl);
                if (now.Credential.RefreshToken != credential.RefreshToken)
                {
                    // The user reconnected, or another replica refreshed: keep that grant and discard this one.
                    if (Fresh(now.Credential))
                        return Channel(instance, now.Credential.AccessToken!);
                    break;
                }
                version = now.Version;
            }
            throw ConnectionRequired.Because(instance, "the stored credential changed while it was being refreshed", correlationId, connectUrl);
        }
    }

    /// <summary>The stored grant when it is not the one this call used (another replica refreshed, or the user reconnected) and is usable.</summary>
    private async Task<UserCredential?> NewerGrant(string instance, string user, UserCredential used, CancellationToken ct) =>
        await store.GetAsync(instance, user, ct) is { } current && current.RefreshToken != used.RefreshToken && Fresh(current) ? current : null;

    private bool Fresh(UserCredential credential) =>
        credential is { Status: CredentialStatus.Connected, AccessToken: not null } && credential.Kind == Kind && !ExpiresSoon(credential);

    private UserCredential Usable(ServiceInstance instance, UserCredential? credential, string correlationId) => credential switch
    {
        null => throw ConnectionRequired.Because(instance, "there is no credential for this user", correlationId, connectUrl),
        { Status: CredentialStatus.Expired } => throw ConnectionRequired.Because(instance, "the stored credential has expired", correlationId, connectUrl),
        { Status: CredentialStatus.Revoked } => throw ConnectionRequired.Because(instance, "the stored credential was revoked", correlationId, connectUrl),
        _ when credential.Kind != Kind || credential.AccessToken == null =>
            throw ConnectionRequired.Because(instance, $"the stored credential was connected for another mode ({credential.Kind})", correlationId, connectUrl),
        _ => credential,
    };

    private bool ExpiresSoon(UserCredential credential) => credential.ExpiresAt is not { } at || at - time.GetUtcNow() < RefreshWindow;

    private HttpChannel Channel(ServiceInstance instance, string accessToken)
    {
        var header = new AuthenticationHeaderValue("Bearer", accessToken);
        return new HttpChannel(http, instance, request => request.Headers.Authorization = header, accessToken);
    }
}
