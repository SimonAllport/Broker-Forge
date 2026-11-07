using BrokerForge.Abstractions;

namespace BrokerForge.Host.Config;

/// <summary>
/// Which Entra scopes a connection design may ask for in ServiceAccount or Impersonate mode (HLD D-34). Both modes
/// send a token minted through the host (its managed identity, or a user's token through its connect app) to the
/// design's base URL, which the design's author chooses. So a design may use only a scope the host lists for its
/// environment in <c>Trust:AllowedCallers[i]:DesignScopes</c>, and never one for Azure management, Key Vault, Storage,
/// Dataverse or Microsoft Graph, whatever the list says.
/// </summary>
public static class DesignScopeRules
{
    /// <summary>OpenID Connect scopes: they name no resource of their own.</summary>
    private static readonly HashSet<string> SignIn = new(StringComparer.OrdinalIgnoreCase) { "openid", "profile", "email", "offline_access" };

    private static readonly (string Domain, string Service)[] ReservedHosts =
    {
        ("management.azure.com", "Azure management"),
        ("management.core.windows.net", "Azure management"),
        ("vault.azure.net", "Azure Key Vault"),
        ("core.windows.net", "Azure Storage"),
        ("storage.azure.com", "Azure Storage"),
        ("dynamics.com", "Dataverse"),
        ("graph.microsoft.com", "Microsoft Graph"),
        ("graph.windows.net", "Microsoft Graph"),
    };

    /// <summary>The same services named by their well-known Entra application IDs (<c>&lt;appId&gt;/.default</c>).</summary>
    private static readonly Dictionary<Guid, string> ReservedApps = new()
    {
        [Guid.Parse("797f4846-ba00-4fd7-ba43-dac1f8f63013")] = "Azure management",
        [Guid.Parse("cfa8b339-82a2-471a-a3c9-0fc0be7a4093")] = "Azure Key Vault",
        [Guid.Parse("e406a681-f3d4-42a8-90b6-c2b029497af1")] = "Azure Storage",
        [Guid.Parse("00000007-0000-0000-c000-000000000000")] = "Dataverse",
        [Guid.Parse("00000003-0000-0000-c000-000000000000")] = "Microsoft Graph",
        [Guid.Parse("00000002-0000-0000-c000-000000000000")] = "Microsoft Graph",
    };

    public static bool IsSignIn(string scope) => SignIn.Contains(scope.Trim());

    /// <summary>The reserved service a scope is for, or null. A scope with no resource (such as <c>User.Read</c>) is Microsoft Graph's.</summary>
    public static string? Reserved(string scope)
    {
        var s = scope.Trim();
        if (s.Length == 0 || IsSignIn(s))
            return null;
        string resource;
        if (s.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri))
                return null;
            resource = uri.Host;
        }
        else
        {
            var slash = s.IndexOf('/');
            if (slash < 0)
                return "Microsoft Graph";
            resource = s[..slash];
        }
        if (Guid.TryParse(resource, out var app))
            return ReservedApps.GetValueOrDefault(app);
        var host = resource.ToLowerInvariant().TrimEnd('.');
        foreach (var (domain, service) in ReservedHosts)
            if (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal))
                return service;
        return null;
    }

    /// <summary>
    /// A scope a design or the DesignScopes list may name: an absolute <c>https://</c> or <c>api://</c> URI with a host,
    /// no user info, query, fragment or surrounding space. Anything else (a bare host, a bare app ID, another scheme) is
    /// refused rather than guessed at, so the reserved-audience check cannot be sidestepped by an odd spelling.
    /// </summary>
    public static bool WellFormed(string scope) =>
        scope.Length > 0 && !scope.Any(char.IsWhiteSpace)
        && Uri.TryCreate(scope, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "api"
        && uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    /// <summary>
    /// The problems with a design instance's auth, on its parsed mode: for ServiceAccount its scope, for Impersonate the
    /// scopes of its OAuth resource. Other modes use no token minted through the host and have none.
    /// </summary>
    public static IEnumerable<string> Problems(string instance, string environmentId, AuthMode mode, string? scope, IReadOnlyList<string>? oauthScopes,
        IReadOnlyCollection<string> allowed)
    {
        IEnumerable<string> scopes;
        if (mode == AuthMode.ServiceAccount)
            scopes = string.IsNullOrWhiteSpace(scope) ? Array.Empty<string>() : new[] { scope };
        else if (mode == AuthMode.Impersonate && oauthScopes != null)
        {
            if (oauthScopes.All(s => string.IsNullOrWhiteSpace(s) || IsSignIn(s)))
            {
                yield return $"Instance {instance} in environment {environmentId} uses Impersonate with only sign-in scopes, which give a Microsoft Graph token; a connection design may never use Microsoft Graph";
                yield break;
            }
            scopes = oauthScopes.Where(s => !string.IsNullOrWhiteSpace(s) && !IsSignIn(s));
        }
        else
            yield break;
        foreach (var s in scopes)
        {
            if (Reserved(s) is { } service)
                yield return $"Instance {instance} in environment {environmentId} uses {mode} with scope `{s}`, which is for {service}: a connection design may never use it, because the token would go to the design's base URL";
            else if (!WellFormed(s))
                yield return $"Instance {instance} in environment {environmentId} uses {mode} with scope `{s}`, which must be an absolute https:// or api:// URI such as api://contoso-inventory/.default";
            else if (!allowed.Contains(s, StringComparer.OrdinalIgnoreCase))
                yield return $"Instance {instance} uses {mode} with scope `{s}`, which Trust:AllowedCallers does not list in DesignScopes for environment {environmentId}: a connection design may use a token minted through the host only for a scope listed there";
        }
    }
}
