namespace BrokerForge.Host.Config;

/// <summary>
/// What <see cref="HostConfigStore"/> validates and builds from. The file input reads host settings and the
/// descriptor and schema files; a later input reads connection designs from Dataverse. Descriptor and schema
/// are carried as text, and the store labels them as it always has (<c>Orders/listOrders.yaml</c>,
/// <c>schemas/Orders.yaml</c>) in its problem messages.
/// </summary>
public sealed class ConfigInput
{
    public Dictionary<string, InstanceOptions> Instances { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, OAuthResourceOptions> OAuthResources { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ObjectMapOptions> ObjectMaps { get; } = new(StringComparer.Ordinal);
    /// <summary>Descriptor YAML by instance and file name (for example <c>listOrders.yaml</c>).</summary>
    public Dictionary<(string Instance, string File), string> DescriptorTexts { get; } = new();
    /// <summary>Table schema YAML by instance name.</summary>
    public Dictionary<string, string> SchemaTexts { get; } = new(StringComparer.Ordinal);
    public string? ConnectBaseUrl { get; set; }
    public string? ConnectClientId { get; set; }
    /// <summary>The Dataverse environment the input belongs to; null for file input.</summary>
    public string? EnvironmentId { get; set; }
    /// <summary>The instances that come from a connection design, by name. The others come from host settings.</summary>
    public Dictionary<string, DesignOrigin> Designs { get; } = new(StringComparer.Ordinal);
    /// <summary>Problems found while reading, reported after the instance problems and before the schema ones.</summary>
    public List<string> Problems { get; } = new();
    /// <summary>Problems found reading descriptors, reported after the schema problems and before the object map ones.</summary>
    public List<string> DescriptorProblems { get; } = new();
    /// <summary>
    /// Why connection designs of this environment were left out, one text each (or why none could be read). Not a
    /// problem of the store: it serves what did load, and a request it cannot resolve gets these texts.
    /// </summary>
    public List<string> DesignFailures { get; } = new();
}

/// <summary>
/// Where a design instance came from: its environment and design version, which label its descriptors and schema in
/// problem messages, and the per-environment values it still lacks (<see cref="BrokerForge.Abstractions.ServiceInstance.Incomplete"/>).
/// </summary>
public sealed record DesignOrigin(string EnvironmentId, int Version, IReadOnlyList<string> Incomplete);

public sealed class InstanceOptions
{
    public string BrokerType { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public AuthOptions Auth { get; set; } = new();
    public Dictionary<string, string> Config { get; set; } = new();
    public Dictionary<string, string> Secrets { get; set; } = new();
}

public sealed class AuthOptions
{
    public string Mode { get; set; } = "";
    public string? StaticKind { get; set; }
    public string? HeaderName { get; set; }
    public string? QueryParam { get; set; }
    public string? Username { get; set; }
    public string? SecretRef { get; set; }
    public string? Scope { get; set; }
    public string? OAuthResource { get; set; }
    public string? SsoKind { get; set; }
    public string? AsyncContext { get; set; }
    public AuthOptions? AsyncAuth { get; set; }
}

public sealed class OAuthResourceOptions
{
    public string AuthorizeUrl { get; set; } = "";
    public string TokenUrl { get; set; } = "";
    public List<string> Scopes { get; set; } = new();
    public string? ClientId { get; set; }
    public string? ClientSecretRef { get; set; }
    public bool UsesConnectApp { get; set; }
}

public sealed class ObjectMapOptions
{
    public string ServiceInstance { get; set; } = "";
    public string KeyStrategy { get; set; } = "reversible";
    public string KeyKind { get; set; } = "";
    public Dictionary<string, string> Operations { get; set; } = new();
}
