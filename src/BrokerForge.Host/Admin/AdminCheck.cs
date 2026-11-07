using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Generator;
using BrokerForge.Host.Ingress;

namespace BrokerForge.Host.Admin;

/// <summary>
/// Who may set up connections (spec 6): a user holding the <c>BrokerForge administrator</c> role, directly or through a
/// team. Each business unit has its own copy of a role, and every copy points at the same root role; the root must be the
/// role of that name in solution <c>BrokerForgeCore</c>, so a hand-made role with the name does not count (as D-36 rules
/// for <c>&lt;Instance&gt; user</c>). Read with the host's own application user: the plug-in's word is not enough.
/// </summary>
public static class AdminCheck
{
    public const string RoleName = "BrokerForge administrator";
    public const string CoreSolution = "BrokerForgeCore";
    public const string Refusal = "Only a BrokerForge administrator can set up connections.";

    /// <exception cref="CallerRejectedException">No object ID, no such user, or the user does not hold the role.</exception>
    /// <exception cref="ConfigurationException">The core solution or its administrator role is not installed.</exception>
    public static async Task RequireAsync(DataverseClient host, string? userObjectId, string correlationId, CancellationToken ct)
    {
        if (!await IsAdministratorAsync(host, userObjectId, ct))
            throw new CallerRejectedException(Refusal);
    }

    public static async Task<bool> IsAdministratorAsync(DataverseClient host, string? userObjectId, CancellationToken ct)
    {
        // A call with no object ID is an async step or one made on behalf of someone else: never anyone's admin rights.
        if (!Guid.TryParse(userObjectId, out var oid) || oid == Guid.Empty)
            return false;
        var root = await RootRoleAsync(host, ct);
        // A disabled user (removed from the environment) keeps their role assignments, so only an enabled one counts.
        var user = (await host.ListAsync($"systemusers?$select=systemuserid&$filter=azureactivedirectoryobjectid eq {oid:D} and isdisabled eq false", ct)).FirstOrDefault();
        if (user?["systemuserid"] is not { } userId)
            return false;
        if ((await host.ListAsync($"systemusers({userId})/systemuserroles_association?$select=roleid,_parentrootroleid_value", ct)).Any(r => IsCopyOf(r, root)))
            return true;
        foreach (var team in await host.ListAsync($"systemusers({userId})/teammembership_association?$select=teamid", ct))
            if ((await host.ListAsync($"teams({team["teamid"]})/teamroles_association?$select=roleid,_parentrootroleid_value", ct)).Any(r => IsCopyOf(r, root)))
                return true;
        return false;
    }

    private static bool IsCopyOf(System.Text.Json.Nodes.JsonObject role, string root) =>
        string.Equals((string?)role["_parentrootroleid_value"] ?? (string?)role["roleid"], root, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> RootRoleAsync(DataverseClient host, CancellationToken ct)
    {
        var solution = (await host.ListAsync($"solutions?$select=solutionid&$filter=uniquename eq {DataverseClient.Literal(CoreSolution)}", ct)).FirstOrDefault()
            ?? throw new ConfigurationException($"Solution {CoreSolution} is not installed in this environment; install the BrokerForge core first.");
        var inCore = (await host.ListAsync($"solutioncomponents?$select=objectid&$filter=_solutionid_value eq {solution["solutionid"]} and componenttype eq 20", ct))
            .Select(c => (string?)c["objectid"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var named = await host.ListAsync($"roles?$select=roleid&$filter=name eq {DataverseClient.Literal(RoleName)}", ct);
        return named.Select(r => (string?)r["roleid"]).FirstOrDefault(inCore.Contains)
            ?? throw new ConfigurationException($"Role {RoleName} is not part of solution {CoreSolution}; install the BrokerForge core first.");
    }
}
