namespace BrokerForge.Abstractions;

/// <summary>
/// A secret reference: <c>kv:&lt;secret uri&gt;</c>, <c>env:&lt;VARIABLE&gt;</c> or <c>store:&lt;name&gt;</c> (an instance
/// secret in the host's store, keyed by environment and instance). Never the value.
/// </summary>
public sealed record SecretRef(string Scheme, string Location)
{
    public static SecretRef Parse(string reference)
    {
        var colon = reference.IndexOf(':');
        if (colon <= 0 || colon == reference.Length - 1)
            throw new FormatException("A secret reference has the form kv:<secret uri>, env:<VARIABLE> or store:<name>.");
        var scheme = reference[..colon];
        if (scheme is not ("kv" or "env" or "store"))
            throw new FormatException($"Unknown secret reference scheme `{scheme}`; use kv, env or store.");
        var location = reference[(colon + 1)..];
        if (scheme == "store" && !location.All(char.IsAsciiLetterOrDigit))
            throw new FormatException("A store: secret name is letters and digits only.");
        return new SecretRef(scheme, location);
    }

    public override string ToString() => $"{Scheme}:{Location}";
}

/// <summary>Resolves secret references. Only the host calls this; adapters and hooks never do.</summary>
public interface ISecretResolver
{
    Task<string> GetAsync(SecretRef reference, CancellationToken ct);

    /// <summary>A secret of <paramref name="instance"/>: <c>store:</c> references resolve within its environment.</summary>
    Task<string> GetAsync(SecretRef reference, ServiceInstance instance, CancellationToken ct) => GetAsync(reference, ct);
}
