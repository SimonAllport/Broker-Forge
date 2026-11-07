using BrokerForge.Abstractions;
using BrokerForge.Contracts;

namespace BrokerForge.Host.Routing;

/// <summary>
/// Applies the async context rule (HLD 9.3, D-29). This is the only place a call can run as anything other
/// than the instance's own mode: an async call to an instance whose rule is ServiceIdentity runs as its
/// explicit AsyncAuth. A synchronous call, or a TriggeringUser instance, always uses the instance's mode, so a
/// missing per-user credential is never answered with the service identity.
/// </summary>
public static class AuthSelector
{
    public static ServiceInstance Select(ServiceInstance instance, CallerContext caller) =>
        caller.Async && instance.Auth is { AsyncContext: AsyncContext.ServiceIdentity, AsyncAuth: { } asyncAuth }
            ? instance with { Auth = asyncAuth }
            : instance;
}
