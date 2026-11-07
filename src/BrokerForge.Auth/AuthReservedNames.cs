using BrokerForge.Abstractions;
using BrokerForge.Descriptors.Validation;

namespace BrokerForge.Auth;

/// <summary>
/// The headers and query parameters an instance's auth mode sets, which descriptors may not declare (descriptor
/// spec 4). An async identity (D-29) sends from the same descriptors, so its names are reserved too.
/// Authorization is always reserved.
/// </summary>
public static class AuthReservedNames
{
    public static ReservedNames For(ServiceInstance instance)
    {
        // Headers that unlock a host's managed identity (Azure IMDS and the App Service / Container Apps identity
        // endpoint): no descriptor may send them, whatever the instance.
        var headers = new List<string> { "Metadata", "X-IDENTITY-HEADER" };
        var query = new List<string>();
        foreach (var auth in new[] { instance.Auth, instance.Auth.AsyncAuth })
        {
            if (auth is { Mode: AuthMode.Static, StaticKind: "apiKeyHeader", HeaderName: { } staticHeader })
                headers.Add(staticHeader);
            else if (auth is { Mode: AuthMode.Sso, SsoKind: "apiKeyHeader", HeaderName: { } ssoHeader })
                headers.Add(ssoHeader);
            else if (auth is { Mode: AuthMode.Static, StaticKind: "apiKeyQuery", QueryParam: { } param })
                query.Add(param);
        }
        return ReservedNames.For(headers, query);
    }
}
