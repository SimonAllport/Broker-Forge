namespace BrokerForge.Auth.PerUser;

/// <summary>
/// The BrokerForge Connect app registration (HLD D-26) as an OAuth client: its ID and the form fields that
/// authenticate it at Entra's token endpoint (a managed identity client assertion, or a client secret).
/// </summary>
public interface IConnectAppClient
{
    string ClientId { get; }
    Task<IReadOnlyList<KeyValuePair<string, string>>> ClientAuthAsync(CancellationToken ct);
}
