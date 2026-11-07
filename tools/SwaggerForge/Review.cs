namespace BrokerForge.SwaggerForge;

public enum QuestionKind { Choice, Number, Text }

public sealed record Choice(string Value, string Label);

/// <summary>
/// One gap in the definition, in plain words. <see cref="Answer"/> is the answer in use (null when there is none or it
/// was refused); <see cref="Error"/> says why a given answer was refused. <see cref="Proposed"/> is what an optional
/// question uses when it has no answer (a drafted name, for example).
/// </summary>
public sealed record Question(string Key, string Text, QuestionKind Kind, IReadOnlyList<Choice> Choices, bool Required, string? Proposed, string? Answer, string? Error)
{
    /// <summary>True while the question blocks its endpoint: a refused answer, or a required question with none.</summary>
    public bool Open => Error != null || (Required && Answer == null);

    /// <summary>The value in use: the answer, else the proposal.</summary>
    public string? Value => Answer ?? Proposed;
}

public enum EndpointStatus { Ready, NeedsAnswers, NotUsed, Unsupported, Invalid }

/// <summary>
/// One operation in the definition. <see cref="Becomes"/>: List, Open, New, Save, Delete or "Action: name".
/// <see cref="Reason"/> is why an Unsupported endpoint was not drafted; <see cref="Problems"/> the validator errors of an Invalid one.
/// </summary>
public sealed record EndpointReview(string Operation, string Method, string Path, string Becomes, EndpointStatus Status, string? Reason,
    IReadOnlyList<Question> Questions, IReadOnlyList<string> Problems);

public sealed record ColumnProposal(string SourcePath, string LogicalName, string DisplayName, string Type);

/// <summary>
/// The proposed table. <see cref="Collection"/> is null until the collection question is answered. <see cref="KeyKind"/> is
/// the key's type as a key kind (Guid for a uuid string, Int64 for an int64 integer, Int32 for another integer, String
/// otherwise), null while the key is unknown.
/// </summary>
public sealed record TableProposal(string? Collection, string LogicalName, string DisplayName, string CollectionName, string? KeyPath, string? PrimaryName,
    IReadOnlyList<ColumnProposal> Columns, IReadOnlyList<Question> Questions, string? KeyKind = null);

/// <summary>
/// What the admin reviews (spec 5, screen 2). <see cref="Result"/> is set only when nothing is open: no problems, at least
/// one endpoint in use, and every endpoint in use Ready. <see cref="UnusedAnswers"/> are answer keys no question asked.
/// <see cref="Notes"/> say what was not drafted and why, whether or not the review is ready. <see cref="Table"/> is null
/// when the definition has no collection, and when only Custom APIs are in use.
/// </summary>
public sealed record DraftReview(TableProposal? Table, IReadOnlyList<EndpointReview> Endpoints, IReadOnlyList<string> Problems,
    IReadOnlyList<string> UnusedAnswers, DraftResult? Result, IReadOnlyList<string> Notes)
{
    public bool Ready => Result != null;
}
