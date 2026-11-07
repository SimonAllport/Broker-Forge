using BrokerForge.Abstractions;
using BrokerForge.Contracts;

namespace BrokerForge.Auth.PerUser;

/// <summary>The one way a per-user provider fails for want of a credential (HLD 7.3, D-03): never a fallback.</summary>
internal static class ConnectionRequired
{
    public static ConnectionRequiredException Because(ServiceInstance instance, string reason, string correlationId, Func<ServiceInstance, string> connectUrl) =>
        new($"Service instance {instance.Name} uses {instance.Auth.Mode} and {reason}; connect your account and try again.", correlationId, connectUrl(instance));

    /// <summary>The caller's object ID in D-form, or a ConnectionRequiredException when there is none (D-27).</summary>
    public static string User(ServiceInstance instance, CallerContext caller, string correlationId, Func<ServiceInstance, string> connectUrl) =>
        Guid.TryParse(caller.UserObjectId, out var oid)
            ? oid.ToString("D")
            : throw Because(instance, "the caller has no Entra object ID (an impersonated call sends none), so no per-user credential can be used", correlationId, connectUrl);
}
