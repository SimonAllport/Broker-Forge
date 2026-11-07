using BrokerForge.Connections;

namespace BrokerForge.Host.Config;

/// <summary>
/// The configuration for each Dataverse environment the host serves: the file configuration plus that environment's
/// connection designs. <see cref="Files"/> is the file configuration alone, for what is not per environment.
/// </summary>
public interface IHostConfigProvider
{
    Task<HostConfigStore> ForEnvironmentAsync(string environmentId, CancellationToken ct);
    HostConfigStore Files { get; }
    void Invalidate(string environmentId);
}

/// <summary>Reads an environment's connection designs and their per-environment settings.</summary>
public interface IDesignSource
{
    /// <summary>
    /// The designs that could be read, and one failure text per design (or unrelated resource) that could not. Throws
    /// only when the environment cannot be read at all: <see cref="DesignSourceUnavailableException"/> when that may
    /// pass by itself, <see cref="BrokerForge.Abstractions.ConfigurationException"/> otherwise.
    /// </summary>
    Task<DesignLoad> LoadAsync(string environmentId, CancellationToken ct);
}

/// <summary>
/// What the host allows one environment's designs: the scopes they may use in ServiceAccount or Impersonate mode
/// (<c>Trust:AllowedCallers[i]:DesignScopes</c>) and the tenant whose Entra endpoints connect-app designs use.
/// </summary>
/// <param name="AllowedPrivateHosts">Config:Designs:AllowedPrivateHosts: exact host names a design may reach although they are
/// private (and over http, for a base URL).</param>
/// <param name="AllowLoopbackForTests">Config:Designs:AllowLoopbackForTests: OAuth design URLs may use http to loopback (tests only).</param>
public sealed record DesignTrust(IReadOnlyCollection<string> Scopes, string TenantId, IReadOnlyCollection<string>? AllowedPrivateHosts = null,
    bool AllowLoopbackForTests = false)
{
    public static readonly DesignTrust None = new(Array.Empty<string>(), "");
}

/// <summary>What <see cref="IDesignSource.LoadAsync"/> read: the usable designs, and why each other one was left out.</summary>
public sealed record DesignLoad(IReadOnlyList<(ConnectionDesign Design, EnvironmentSettings Settings)> Designs, IReadOnlyList<string> Failures)
{
    public DesignLoad(IEnumerable<(ConnectionDesign Design, EnvironmentSettings Settings)> designs) : this(designs.ToList(), Array.Empty<string>()) { }
}

/// <summary>
/// An <see cref="IDesignSource"/> could not read the environment's designs at all (as opposed to reading a design that
/// is wrong). The message is the full text a request gets; the provider may serve the last good store meanwhile.
/// </summary>
public sealed class DesignSourceUnavailableException(string message) : Exception(message);
