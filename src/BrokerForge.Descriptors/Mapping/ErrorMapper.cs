using System.Text;
using System.Text.RegularExpressions;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;

namespace BrokerForge.Descriptors.Mapping;

/// <summary>
/// Applies the descriptor's errors block (descriptor spec 8). Mapped statuses become
/// <see cref="MappedSourceException"/>; every other non-2xx, and a failed successBodyCheck, is a
/// <see cref="SourceException"/> with the status. Quoted source text is redacted and at most 500 characters.
/// </summary>
public static partial class ErrorMapper
{
    public const int MaxExcerpt = 500;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static void ThrowIfError(OperationDescriptor d, BrokerResult r, string correlationId, IEnumerable<string>? secretValues = null)
    {
        var secrets = secretValues?.Where(s => !string.IsNullOrEmpty(s)).ToList() ?? new List<string>();
        if (r.Status is >= 200 and < 300)
        {
            if (d.Errors?.SuccessBodyCheck is { } check)
                CheckSuccessBody(d, r, check, correlationId, secrets);
            return;
        }

        var mapping = d.Errors?.Statuses.GetValueOrDefault(r.Status);
        if (mapping == null || mapping.Exception == "Fail")
            throw new SourceException(
                $"Source returned HTTP {r.Status} for operation {d.Operation}{ExcerptSuffix(r.Body, secrets)}",
                correlationId,
                r.Status);

        var detail = mapping.MessagePath != null ? MessageFromPath(r.Body, mapping.MessagePath, secrets) : null;
        throw new MappedSourceException(
            mapping.Exception,
            detail != null ? $"{detail} (operation {d.Operation}, HTTP {r.Status})" : $"Source returned HTTP {r.Status} ({mapping.Exception}) for operation {d.Operation}",
            correlationId,
            r.Status);
    }

    private static void CheckSuccessBody(OperationDescriptor d, BrokerResult r, SuccessBodyCheck check, string correlationId, List<string> secrets)
    {
        SourceException Fail(string message) => new($"{message} (operation {d.Operation}){ExcerptSuffix(r.Body, secrets)}", correlationId, r.Status);
        var root = Json.Parse(r.Body, Fail);
        var matches = Json.Select(root, check.Path);
        if (matches.Count != 1)
            throw Fail($"Source reported success but {check.Path} is missing from the body");
        var actual = matches[0] == null ? "null" : Json.ScalarText(matches[0]) ?? matches[0]!.ToJsonString();
        if (actual != check.EqualsValue)
            throw Fail($"Source reported success but {check.Path} is {Truncate(Redact(actual, secrets))}, expected {check.EqualsValue}");
    }

    private static string? MessageFromPath(byte[] body, string path, List<string> secrets)
    {
        try
        {
            var matches = Json.Select(Json.Parse(body, m => new FormatException(m)), path);
            var text = matches.Count == 1 ? Json.ScalarText(matches[0]) : null;
            return text == null ? null : Truncate(Redact(text, secrets));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string ExcerptSuffix(byte[] body, List<string> secrets)
    {
        if (body.Length == 0)
            return ".";
        var text = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 8192));
        return ": " + Truncate(Redact(text, secrets));
    }

    /// <summary>Replaces secret values and collapses whitespace so an excerpt is one line.</summary>
    public static string Redact(string text, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets)
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        return Whitespace().Replace(text, " ").Trim();
    }

    private static string Truncate(string text) => text.Length <= MaxExcerpt ? text : text[..MaxExcerpt] + "…";
}
