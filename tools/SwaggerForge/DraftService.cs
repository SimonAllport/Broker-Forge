using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Microsoft.OpenApi.Services;

namespace BrokerForge.SwaggerForge;

/// <summary>
/// Turns a pasted OpenAPI definition and the admin's answers into a review (spec 6, steps 1 and 2). Never throws for
/// what an admin can paste: a definition that cannot be read is a problem. External references are never loaded.
/// </summary>
public static class DraftService
{
    /// <summary>
    /// The longest definition read, in characters. The design holds the definition plus its descriptors, and the
    /// carrier's upload cap is 5 MB by default.
    /// </summary>
    public const int MaxDefinitionLength = 2_000_000;

    public static DraftReview Review(string? definition, DraftOptions options, IReadOnlyDictionary<string, string>? answers)
    {
        definition ??= "";
        answers ??= new Dictionary<string, string>();
        if (definition.Length > MaxDefinitionLength)
            return Failed($"The definition is {definition.Length} characters; the most BrokerForge reads is {MaxDefinitionLength}.", answers);
        if (string.IsNullOrWhiteSpace(definition))
            return Failed("The definition is empty.", answers);

        OpenApiDocument document;
        OpenApiDiagnostic diagnostic;
        try
        {
            var settings = new OpenApiReaderSettings { LoadExternalRefs = false, ReferenceResolution = ReferenceResolutionSetting.ResolveLocalReferences };
            document = new OpenApiStringReader(settings).Read(definition, out diagnostic);
        }
        catch (Exception ex)
        {
            return Failed($"The definition cannot be read as OpenAPI: {Short(ex.Message)}", answers);
        }
        if (diagnostic.Errors.Count > 0)
        {
            var first = diagnostic.Errors[0];
            var more = diagnostic.Errors.Count > 1 ? $" (and {diagnostic.Errors.Count - 1} more)" : "";
            return Failed($"The definition cannot be read as OpenAPI: {first.Pointer}: {Short(first.Message)}{more}", answers);
        }
        if (document.Paths == null || document.Paths.Count == 0)
            return Failed("The definition has no paths, so it is not an OpenAPI definition BrokerForge can use.", answers);

        var external = new ExternalReferences();
        new OpenApiWalker(external).Walk(document);
        if (external.Found.Count > 0)
            return Failed(string.Join(" ", external.Found.Select(r => $"External reference {r} is not read; paste one self-contained definition.")), answers);

        return Drafter.Draft(document, options, new Answers(answers));
    }

    private static DraftReview Failed(string problem, IReadOnlyDictionary<string, string> answers) =>
        new(null, Array.Empty<EndpointReview>(), new[] { problem }, answers.Keys.Order(StringComparer.Ordinal).ToList(), null, Array.Empty<string>());

    private static string Short(string message) => message.Length <= 200 ? message : message[..200] + "…";

    private sealed class ExternalReferences : OpenApiVisitorBase
    {
        public SortedSet<string> Found { get; } = new(StringComparer.Ordinal);

        public override void Visit(IOpenApiReferenceable referenceable)
        {
            if (referenceable.Reference is { IsExternal: true } r)
                Found.Add(r.ExternalResource + (r.Id is { Length: > 0 } id ? "#" + id : ""));
        }
    }
}
