namespace BrokerForge.Abstractions;

public enum AuthMode { ServiceAccount, Static, Impersonate, Sso, OAuth }

/// <summary>Who an async call runs as (HLD 9.3, D-29): the user who triggered it, or the instance's explicit AsyncAuth.</summary>
public enum AsyncContext { TriggeringUser, ServiceIdentity }

/// <summary>
/// One configured connection to a source (HLD 6, bf_serviceinstance), from host configuration (HLD D-10) or a
/// connection design. It holds secret references, never secret values. <see cref="EnvironmentId"/> is set only
/// for a design instance: its name is unique within that environment, not across the host.
/// </summary>
public sealed record ServiceInstance(
    string Name,
    string BrokerType,
    string BaseUrl,
    AuthConfig Auth,
    IReadOnlyDictionary<string, string> Config,
    IReadOnlyDictionary<string, string> Secrets,
    string? EnvironmentId = null)
{
    /// <summary>
    /// Per-environment values a design instance still lacks, each as "what (how to set it)". A call to such an
    /// instance fails naming them; it is not a startup problem, because a solution import sets them later.
    /// </summary>
    public IReadOnlyList<string> Incomplete { get; init; } = Array.Empty<string>();

    /// <summary>The connection design version a design instance was built from (spec 6, Approval); null for a file instance.</summary>
    public int? DesignVersion { get; init; }
}

/// <summary>
/// OAuth and Impersonate: <see cref="OAuthResource"/> names an entry in OAuthResources. SSO:
/// <see cref="SsoKind"/> is basic or apiKeyHeader (with <see cref="HeaderName"/>). <see cref="AsyncAuth"/>
/// is the ServiceAccount or Static identity async calls use when <see cref="AsyncContext"/> is ServiceIdentity.
/// Static: <see cref="StaticKind"/> is apiKeyHeader (with <see cref="HeaderName"/>), apiKeyQuery
/// (with <see cref="QueryParam"/>) or basic (with <see cref="Username"/>); the value comes from
/// <see cref="SecretRef"/>. ServiceAccount: <see cref="Scope"/> is the token scope.
/// </summary>
public sealed record AuthConfig(
    AuthMode Mode,
    string? StaticKind = null,
    string? HeaderName = null,
    string? QueryParam = null,
    string? Username = null,
    string? SecretRef = null,
    string? Scope = null,
    string? OAuthResource = null,
    string? SsoKind = null,
    AsyncContext AsyncContext = AsyncContext.TriggeringUser,
    AuthConfig? AsyncAuth = null);

/// <summary>
/// An OAuth authorisation server (HLD bf_oauthresource, D-28). With <see cref="UsesConnectApp"/> the client is
/// the BrokerForge Connect app registration (Impersonate, against Entra), so ClientId and ClientSecretRef are
/// absent. <see cref="ClientSecretRef"/> is a secret reference, never the value.
/// </summary>
public sealed record OAuthResource(
    string Name,
    string AuthorizeUrl,
    string TokenUrl,
    IReadOnlyList<string> Scopes,
    string? ClientId,
    string? ClientSecretRef,
    bool UsesConnectApp);

/// <summary>Links a virtual table to its operations (HLD 6, bf_objectmap). Operations is keyed by message name.</summary>
public sealed record ObjectMap(
    string ServiceInstance,
    string Table,
    string KeyStrategy,
    string KeyKind,
    IReadOnlyDictionary<string, string> Operations);
