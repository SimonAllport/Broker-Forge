using System.Text;
using System.Text.RegularExpressions;
using BrokerForge.Connections;
using BrokerForge.Host.Config;
using BrokerForge.Host.Generation;

namespace BrokerForge.Host.Admin;

/// <summary>
/// One-off admin command (until plan 4's bf_Generate): runs one generation job as the host's own identity, inside the
/// host container (managed identity) or locally (DefaultAzureCredential falls back to the az CLI user).
/// <code>dotnet BrokerForge.Host.dll generate --environment &lt;id&gt; --instance &lt;Instance&gt; --solution &lt;S&gt; --publisher &lt;P&gt;
///   (--design-file &lt;path&gt; | --from-environment) [--base-url &lt;url&gt;] [--auth-file &lt;path&gt;] [--preview] [--confirm &lt;fingerprint&gt;] [--with-role]</code>
/// <c>--from-environment</c> regenerates from the design already published there. It prints the change list, the
/// parts and the result; never the design text. The host's identity can't grant a generated table's privileges, so the
/// role is left to an administrator and printed as pending; <c>--with-role</c> applies it too, for an administrator running
/// the command locally as themselves. Exit codes: 0 done (or previewed), 1 failed, 2 usage, 3 done except what an
/// administrator still has to do (printed), 4 the changes need confirming with <c>--confirm</c> and the printed fingerprint.
/// </summary>
public static class GenerateCommand
{
    public const string Name = "generate";

    private const string Usage = "usage: generate --environment <id> --instance <Instance> --solution <unique> --publisher <unique> "
        + "(--design-file <path> | --from-environment) [--base-url <url>] [--auth-file <path>] [--preview] [--confirm <fingerprint>] [--with-role]";

    private static readonly string[] Flags = ["from-environment", "preview", "with-role"];
    private static readonly string[] Values = ["environment", "instance", "solution", "publisher", "design-file", "base-url", "auth-file", "confirm"];

    /// <summary>Splits the admin command off the host's arguments; <paramref name="command"/> is null for any other start.</summary>
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

    public static async Task<int> RunAsync(string[] args, GenerationJobs jobs, TextWriter output, CancellationToken ct)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i].StartsWith("--", StringComparison.Ordinal) ? args[i][2..] : null;
            if (key != null && Flags.Contains(key) && !options.ContainsKey(key))
                options[key] = "";
            else if (key != null && Values.Contains(key) && !options.ContainsKey(key) && i + 1 < args.Length)
                options[key] = args[++i];
            else
                return await Fail(output, Usage, 2);
        }
        if (new[] { "environment", "instance", "solution", "publisher" }.Any(k => !options.ContainsKey(k))
            || options.ContainsKey("design-file") == options.ContainsKey("from-environment"))
            return await Fail(output, Usage, 2);

        var environment = options["environment"];
        var instance = options["instance"];
        ConnectionDesign design;
        string? auth;
        try
        {
            design = options.TryGetValue("design-file", out var file)
                ? DesignSerializer.Parse(await File.ReadAllTextAsync(file, ct), Path.GetFileName(file))
                : await jobs.ReadPublishedAsync(environment, instance, ct);
            auth = options.TryGetValue("auth-file", out var authFile) ? await File.ReadAllTextAsync(authFile, ct) : null;
        }
        catch (Exception ex) when (ex is DesignFormatException or IOException or BrokerForge.Abstractions.ConfigurationException or BrokerForge.Generator.DataverseException)
        {
            return await Fail(output, ex.Message, 1);
        }
        if (design.Instance != instance)
            return await Fail(output, $"The design is for {design.Instance}, not {instance}.", 1);

        var request = new GenerationRequest(environment, design, options["solution"], options["publisher"], options.GetValueOrDefault("base-url"), auth,
            options.GetValueOrDefault("confirm"), $"generate-{Guid.NewGuid():N}", ApplyRole: options.ContainsKey("with-role"));
        try
        {
            var preview = await jobs.PreviewAsync(request, ct);
            await output.WriteLineAsync(preview.Changes.Changes.Count == 0
                ? $"{instance}: no changes to the table or actions"
                : $"{instance}: changes (fingerprint {preview.Changes.Fingerprint}):");
            foreach (var c in preview.Changes.Changes)
                await output.WriteLineAsync(" - " + c.Text);
            foreach (var r in preview.Changes.Refusals)
                await output.WriteLineAsync(" ! " + r);
            foreach (var b in preview.Blocked)
                await output.WriteLineAsync($" ! {b.Change.Text} is blocked: {b.Dependent} uses it");
            foreach (var p in preview.Parts)
                await output.WriteLineAsync(" + " + p);
            foreach (var p in preview.Pending)
                await output.WriteLineAsync(" ? needs an administrator: " + p);
            if (options.ContainsKey("preview"))
                return 0;
            if (preview.Changes.Refusals.Count > 0 || preview.Blocked.Count > 0)
                return await Fail(output, "Nothing was generated.", 1);
            if (preview.NeedsConfirmation && request.ConfirmedFingerprint == null)
                return await Fail(output, $"To apply exactly these changes, run again with --confirm {preview.Changes.Fingerprint}.", 4);

            var job = await jobs.WaitAsync((await jobs.StartAsync(request, ct)).Id);
            await output.WriteLineAsync($"{instance}: {job.State}, {job.Writes.Count} write(s){(job.DesignVersion is { } v ? $", design v{v}" : "")}");
            foreach (var p in job.Pending)
                await output.WriteLineAsync($"{instance}: needs an administrator: {p} (run this command as an administrator with --with-role)");
            if (job.State == JobState.Succeeded)
                return job.Pending.Count > 0 ? 3 : 0;
            return await Fail(output, job.Failure ?? "failed", job.NeedsConfirmation ? 4 : 1);
        }
        catch (Exception ex) when (ex is BrokerForge.Abstractions.ConfigurationException or GenerationBusyException or BrokerForge.Generator.GenerationRefusedException
                                       or BrokerForge.Generator.DataverseException or BrokerForge.Schema.SchemaException or BrokerForge.Descriptors.Parsing.DescriptorException)
        {
            return await Fail(output, ex.Message, 1);
        }
    }

    private static async Task<int> Fail(TextWriter output, string message, int code)
    {
        await output.WriteLineAsync(message);
        return code;
    }
}
