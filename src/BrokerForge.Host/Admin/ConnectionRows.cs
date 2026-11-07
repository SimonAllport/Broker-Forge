using BrokerForge.Contracts;
using BrokerForge.Generator;

namespace BrokerForge.Host.Admin;

/// <summary>Reads <c>bf_connection</c> rows as the host's application user.</summary>
public static class ConnectionRows
{
    public const string EntitySet = "bf_connections";

    /// <exception cref="MappedSourceException">NotFound when there is no such row.</exception>
    public static async Task<ConnectionRow> ReadAsync(DataverseClient host, Guid id, string correlationId, CancellationToken ct)
    {
        var row = await host.GetAsync($"{EntitySet}({id:D})?$select={ConnectionRow.Select}", ct)
            ?? throw new MappedSourceException(SourceErrorKinds.NotFound, $"There is no connection {id:D}.", correlationId);
        return ConnectionRow.FromJson(row);
    }

    public static async Task<IReadOnlyList<ConnectionRow>> ListAsync(DataverseClient host, CancellationToken ct) =>
        (await host.ListAsync($"{EntitySet}?$select={ConnectionRow.Select}", ct)).Select(ConnectionRow.FromJson).ToList();
}
