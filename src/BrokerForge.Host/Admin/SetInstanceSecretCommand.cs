using Azure;
using BrokerForge.Abstractions;
using BrokerForge.Auth;
using BrokerForge.Auth.Credentials;
using BrokerForge.Host.Config;

namespace BrokerForge.Host.Admin;

/// <summary>
/// One-off admin command (stopgap until the plan 4 secret page): sets an instance secret (HLD D-34) in the host's own
/// store, run inside the host container so it uses the host identity, table and Data Protection key ring.
/// <code>dotnet BrokerForge.Host.dll set-instance-secret --environment &lt;id&gt; --instance &lt;Instance&gt; --name &lt;name&gt; --from &lt;kv:uri | env:VARIABLE&gt;</code>
/// The value is read from the reference given in <c>--from</c>, so it never appears on a command line, and it is never printed.
/// The instance and name must be an instance secret that a connection design of that environment lists in its
/// <c>secretNames</c>. Anyone who can run this inside the host can copy any Key Vault secret or environment variable
/// the host identity can read into an instance secret: exec rights on the host are equivalent to reading its secrets.
/// </summary>
public static class SetInstanceSecretCommand
{
    public const string Name = "set-instance-secret";

    private const string Usage = "usage: set-instance-secret --environment <id> --instance <Instance> --name <name> --from <kv:<secret uri> | env:<VARIABLE>>";

    /// <summary>Splits the admin command off the host's arguments; <paramref name="command"/> is null for a normal start.</summary>
    public static string[] Split(string[] args, out string[]? command)
    {
        if (args.Length > 0 && args[0] == Name)
        {
            command = args[1..];
            return [];
        }
        command = null;
        return args;
    }

    /// <summary>Runs the command. Returns the process exit code; messages name the instance and secret, never the value.</summary>
    public static async Task<int> RunAsync(string[] args, IEnumerable<string> allowedEnvironments, IHostConfigProvider configs, IInstanceSecretStore store,
        ISecretResolver resolver, TextWriter output, CancellationToken ct)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || options.ContainsKey(args[i][2..]))
            {
                await output.WriteLineAsync(Usage);
                return 2;
            }
            options[args[i][2..]] = args[i + 1];
        }
        string[] required = ["environment", "instance", "name", "from"];
        if (options.Count != required.Length || required.Any(r => !options.ContainsKey(r)))
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }
        var (environment, instance, name) = (options["environment"], options["instance"], options["name"]);
        if (!allowedEnvironments.Any(e => string.Equals(e, environment, StringComparison.OrdinalIgnoreCase)))
        {
            await output.WriteLineAsync($"Environment {environment} is not an allowed caller of this host (Trust:AllowedCallers).");
            return 1;
        }

        // Only a secret some design of that environment asks for: otherwise the store fills with values nothing reads.
        var config = await configs.ForEnvironmentAsync(environment, ct);
        if (config.Instance(instance) is not { EnvironmentId: not null } designed || !designed.Secrets.ContainsKey(name))
        {
            await output.WriteLineAsync($"No connection design in environment {environment} lists the instance secret `{name}` for {instance} in its secretNames; nothing was set."
                + (config.DesignFailures.Count == 0 ? "" : " These designs could not be loaded there:" + string.Concat(config.DesignFailures.Select(f => "\n - " + f))));
            return 1;
        }

        SecretRef from;
        try
        {
            from = SecretRef.Parse(options["from"]);
        }
        catch (FormatException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 2;
        }
        if (from.Scheme == "store")
        {
            await output.WriteLineAsync("--from must be a kv: or env: reference: the value is read from there, never typed.");
            return 2;
        }

        string value;
        try
        {
            value = await resolver.GetAsync(from, ct);
        }
        catch (Exception ex) when (ex is CredentialUnavailableException or ConfigurationException)
        {
            await output.WriteLineAsync($"Could not read {from}: {ex.Message}");
            return 1;
        }
        if (string.IsNullOrEmpty(value))
        {
            await output.WriteLineAsync($"{from} is empty; nothing was set.");
            return 1;
        }
        bool stored;
        try
        {
            await store.SetAsync(environment, instance, name, value, ct);
            stored = await store.ExistsAsync(environment, instance, name, ct);
        }
        catch (ArgumentException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 2;
        }
        catch (RequestFailedException ex)
        {
            // Only the status and error code: the service's message can echo the request.
            await output.WriteLineAsync($"Could not store instance secret `{name}` of {instance} in environment {environment}: the table answered HTTP {ex.Status}"
                + (string.IsNullOrEmpty(ex.ErrorCode) ? "." : $" ({ex.ErrorCode})."));
            return 1;
        }
        await output.WriteLineAsync(stored
            ? $"Set instance secret `{name}` of {instance} in environment {environment} from {from}."
            : $"Instance secret `{name}` of {instance} in environment {environment} was written but cannot be read back.");
        return stored ? 0 : 1;
    }
}
