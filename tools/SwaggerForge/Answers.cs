using System.Globalization;

namespace BrokerForge.SwaggerForge;

/// <summary>
/// The admin's answers, looked up by question key. Every lookup records the key as asked, so answers no question asked
/// can be listed (<see cref="Unused"/>) and are never applied. An answer that is not valid for its question is refused
/// with an error and counts as no answer. An empty answer counts as no answer.
/// </summary>
internal sealed class Answers(IReadOnlyDictionary<string, string> given)
{
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);

    /// <summary>A fixed-choice question; <paramref name="check"/> can refuse a listed choice that doesn't fit the rest of the answers.</summary>
    public Question Choice(string key, string text, IReadOnlyList<Choice> choices, bool required = true, string? proposed = null, Func<string, string?>? check = null) =>
        Ask(key, text, QuestionKind.Choice, choices, required, proposed,
            a => choices.Any(c => c.Value == a) ? check?.Invoke(a) : $"`{a}` is not one of the choices");

    public Question Number(string key, string text, int min, int max) =>
        Ask(key, text, QuestionKind.Number, Array.Empty<Choice>(), true, null,
            a => int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
                ? null : $"must be a whole number from {min} to {max}");

    public Question Text(string key, string text, bool required, string? proposed, Func<string, string?> check) =>
        Ask(key, text, QuestionKind.Text, Array.Empty<Choice>(), required, proposed, check);

    public IReadOnlyList<string> Unused() => given.Keys.Where(k => !_asked.Contains(k)).Order(StringComparer.Ordinal).ToList();

    private Question Ask(string key, string text, QuestionKind kind, IReadOnlyList<Choice> choices, bool required, string? proposed, Func<string, string?> check)
    {
        _asked.Add(key);
        var question = new Question(key, text, kind, choices, required, proposed, null, null);
        if (!given.TryGetValue(key, out var answer) || string.IsNullOrEmpty(answer))
            return question;
        return check(answer) is { } error ? question with { Error = error } : question with { Answer = answer };
    }
}
