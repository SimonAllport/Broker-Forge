using BrokerForge.Host.Config;

namespace BrokerForge.Host.Ingress;

/// <summary>
/// Who may call the host (HLD 9.2, D-09). Tokens must come from <see cref="TenantId"/> for
/// <see cref="Audience"/>; each plug-in identity (azp) is allowed for exactly one environment,
/// whose Dataverse is at <see cref="AllowedCaller.OrgUrl"/>.
/// </summary>
public sealed class TrustOptions
{
    public string TenantId { get; set; } = "";
    public string Audience { get; set; } = "";
    public List<AllowedCaller> AllowedCallers { get; set; } = new();

    /// <param name="designs">Config:Designs:Enabled: every environment then needs its <see cref="AllowedCaller.OrgUrl"/>.</param>
    public IEnumerable<string> Problems(bool designs = false)
    {
        if (string.IsNullOrWhiteSpace(TenantId)) yield return "Trust:TenantId is required";
        if (string.IsNullOrWhiteSpace(Audience)) yield return "Trust:Audience is required";
        if (AllowedCallers.Count == 0) yield return "Trust:AllowedCallers needs at least one { ClientId, EnvironmentId }";
        foreach (var c in AllowedCallers.Where(c => string.IsNullOrWhiteSpace(c.ClientId) || string.IsNullOrWhiteSpace(c.EnvironmentId)))
            yield return "every Trust:AllowedCallers entry needs ClientId and EnvironmentId";
        foreach (var dup in AllowedCallers.GroupBy(c => c.ClientId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"Trust:AllowedCallers lists client {dup.Key} more than once; one plug-in identity serves one environment";
        foreach (var c in AllowedCallers.Where(c => !string.IsNullOrWhiteSpace(c.OrgUrl)
                     && !(Uri.TryCreate(c.OrgUrl.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.PathAndQuery == "/" && u.Fragment == "")))
            yield return $"Trust:AllowedCallers entry for environment {c.EnvironmentId}: OrgUrl must be an absolute https URL with no path, such as https://contoso.crm.dynamics.com";
        foreach (var env in AllowedCallers.Where(c => Guid.TryParse(c.EnvironmentId, out _)).GroupBy(c => Guid.Parse(c.EnvironmentId))
                     .Where(g => g.Select(c => c.OrgUrl?.Trim().TrimEnd('/')).Where(o => !string.IsNullOrEmpty(o)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            yield return $"Trust:AllowedCallers gives environment {env.Key} more than one OrgUrl";
        foreach (var c in AllowedCallers)
            foreach (var scope in c.DesignScopes)
                if (string.IsNullOrWhiteSpace(scope))
                    yield return $"Trust:AllowedCallers entry for environment {c.EnvironmentId}: DesignScopes has an empty entry";
                else if (DesignScopeRules.IsSignIn(scope))
                    yield return $"Trust:AllowedCallers entry for environment {c.EnvironmentId}: DesignScopes lists {scope}, a sign-in scope that names no resource; list the resource's scope";
                else if (DesignScopeRules.Reserved(scope) is { } service)
                    yield return $"Trust:AllowedCallers entry for environment {c.EnvironmentId}: DesignScopes lists {scope}, which is for {service}; a connection design may never use it";
                else if (!DesignScopeRules.WellFormed(scope))
                    yield return $"Trust:AllowedCallers entry for environment {c.EnvironmentId}: DesignScopes lists {scope}, which must be an absolute https:// or api:// URI";
        if (designs)
            foreach (var c in AllowedCallers.Where(c => string.IsNullOrWhiteSpace(c.OrgUrl)))
                yield return $"Trust:AllowedCallers entry for environment {c.EnvironmentId} needs OrgUrl when Config:Designs:Enabled is true";
    }

    /// <summary>The scopes connection designs in an environment may use in ServiceAccount or Impersonate mode (HLD D-34): every entry for it, combined.</summary>
    public IReadOnlyCollection<string> DesignScopesFor(string environmentId) =>
        AllowedCallers.Where(c => Guid.TryParse(c.EnvironmentId, out var id) && Guid.TryParse(environmentId, out var wanted) && id == wanted)
            .SelectMany(c => c.DesignScopes).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed class AllowedCaller
{
    public string ClientId { get; set; } = "";
    public string EnvironmentId { get; set; } = "";

    /// <summary>The environment's Dataverse URL, where the host reads its connection designs. Required when Config:Designs:Enabled is true.</summary>
    public string? OrgUrl { get; set; }

    /// <summary>
    /// The Entra scopes a connection design in this environment may use in ServiceAccount or Impersonate mode
    /// (HLD D-34). Empty by default, which refuses such designs. Azure management, Key Vault, Storage, Dataverse and
    /// Microsoft Graph scopes are refused here at startup.
    /// </summary>
    public List<string> DesignScopes { get; set; } = new();
}
