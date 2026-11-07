namespace BrokerForge.Connections;

/// <summary>A connection design that cannot be read. The message names the source and, for content errors, the JSON path.</summary>
public sealed class DesignFormatException(string message) : Exception(message);

/// <summary>One connection design (docs/connection-design-format.md). FormatVersion is always 1. Holds no secret values and no per-environment values.</summary>
public sealed record ConnectionDesign(
    int FormatVersion,
    string Instance,
    int Version,
    string BrokerType,
    string OpenApi,
    IReadOnlyDictionary<string, string> Answers,
    DesignAuth Auth,
    DesignObjectMap? ObjectMap,
    string Schema,
    IReadOnlyDictionary<string, string> Descriptors);

/// <summary>Authentication shape only. SecretNames lists the instance secrets the design needs, never their values or references.</summary>
public sealed record DesignAuth(
    string Mode,
    string? StaticKind,
    string? HeaderName,
    string? QueryParam,
    string? Username,
    string? Scope,
    string? SsoKind,
    string? AsyncContext,
    [property: System.Text.Json.Serialization.JsonPropertyName("oauth")] DesignOAuth? OAuth,
    IReadOnlyList<string> SecretNames);

/// <summary>
/// AuthorizeUrl and TokenUrl are the source's own (https), and must be absent when UsesConnectApp is true: the host
/// then uses its tenant's Entra endpoints, so the connect app's credential never goes to a URL the design chose.
/// </summary>
public sealed record DesignOAuth(
    string? AuthorizeUrl,
    string? TokenUrl,
    IReadOnlyList<string> Scopes,
    string? ClientId,
    bool UsesConnectApp);

public sealed record DesignObjectMap(
    string Table,
    string KeyStrategy,
    string KeyKind,
    IReadOnlyDictionary<string, string> Operations);

/// <summary>The per-environment values; they are not part of the design.</summary>
public sealed record EnvironmentSettings(
    string? BaseUrl,
    IReadOnlyDictionary<string, string> AuthOverrides);
