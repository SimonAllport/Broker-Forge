using BrokerForge.Contracts;

namespace BrokerForge.Abstractions;

/// <summary>An HTTP channel with credentials already attached by an <see cref="IAuthProvider"/>.</summary>
public interface IAuthenticatedChannel
{
    /// <summary>The credential values this channel attaches, for redaction only. Never logged or sent anywhere else.</summary>
    IReadOnlyCollection<string> CredentialValues { get; }

    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);
}

/// <summary>Returns a channel for the instance's mode. A per-user mode without a credential throws ConnectionRequiredException; no provider falls back to another mode.</summary>
public interface IAuthProvider
{
    AuthMode Mode { get; }
    Task<IAuthenticatedChannel> AuthenticateAsync(ServiceInstance instance, CallerContext caller, string correlationId, CancellationToken ct);
}

public sealed record BrokerCapabilities(IReadOnlySet<string> PagingStrategies);

public sealed record DiscoveryResult(string Description);

/// <summary>Executes rendered requests (HLD 7.2). Adapters never read secrets or descriptors.</summary>
public interface IBroker
{
    string BrokerType { get; }
    BrokerCapabilities Capabilities { get; }
    Task<DiscoveryResult> DiscoverAsync(ServiceInstance instance, CancellationToken ct);
    Task<BrokerResult> ExecuteAsync(RenderedRequest request, IAuthenticatedChannel channel, string correlationId, CancellationToken ct);
}
