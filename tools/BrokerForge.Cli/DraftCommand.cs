using System.Text.Json;
using BrokerForge.SwaggerForge;

namespace BrokerForge.Cli;

/// <summary>
/// <c>bf draft &lt;openapi&gt; --instance &lt;name&gt; --prefix &lt;p&gt; --out &lt;dir&gt; [--answers &lt;json&gt;] [--root &lt;dir&gt;]</c>:
/// drafts through the draft service (HLD D-35). When every endpoint in use is ready, writes <c>schemas/&lt;instance&gt;.yaml</c>
/// and <c>descriptors/&lt;instance&gt;/*.yaml</c> under the root (default: the current directory) and refuses to overwrite
/// an existing one. Otherwise writes <c>&lt;out&gt;/&lt;instance&gt;-questions.json</c>: the answers given plus every open
/// question with an empty answer, and <c>_about</c> lines saying what each asks; fill it in and pass it back as
/// <c>--answers</c>. Both runs rewrite <c>&lt;out&gt;/&lt;instance&gt;-draft-report.md</c>. Exit codes: 0 written, 1 problems,
/// 2 usage, 3 questions open.
/// </summary>
public static class DraftCommand
{
    private const string Usage = "usage: bf draft <openapi> --instance <name> --prefix <p> --out <dir> [--answers <json>] [--root <dir>]";

    public static int Run(string[] args, TextWriter output)
    {
        if (args.Length < 1 || (args.Length - 1) % 2 != 0)
        {
            output.WriteLine(Usage);
            return 2;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
            options[args[i]] = args[i + 1];
        if (!options.TryGetValue("--instance", out var instance) || !options.TryGetValue("--prefix", out var prefix)
            || !options.TryGetValue("--out", out var outDir)
            || options.Keys.Except(new[] { "--instance", "--prefix", "--out", "--root", "--answers" }).Any())
        {
            output.WriteLine(Usage);
            return 2;
        }
        var root = Path.GetFullPath(options.GetValueOrDefault("--root") ?? ".");
        var outPath = Path.IsPathRooted(outDir) ? outDir : Path.Combine(root, outDir);

        Dictionary<string, string> answers;
        try
        {
            answers = options.TryGetValue("--answers", out var answersFile) ? ReadAnswers(File.ReadAllText(answersFile)) : new();
        }
        catch (JsonException ex)
        {
            output.WriteLine($"--answers: not a JSON object of string answers: {ex.Message}");
            return 1;
        }

        var review = DraftService.Review(File.ReadAllText(args[0]), new DraftOptions(instance, prefix), answers);
        foreach (var problem in review.Problems)
            output.WriteLine($"{args[0]}: {problem}");
        if (review.Problems.Count > 0)
            return 1;

        var report = Path.Combine(outPath, instance + "-draft-report.md");
        if (review.Result is not { } draft)
        {
            var questions = Path.Combine(outPath, instance + "-questions.json");
            Directory.CreateDirectory(outPath);
            File.WriteAllText(report, Report(args[0], instance, review));
            File.WriteAllText(questions, QuestionsFile(review, answers));
            output.WriteLine($"{OpenQuestions(review).Count()} questions are open; answer them in {Path.GetRelativePath(root, questions)} and run again with --answers.");
            return 3;
        }

        var files = new Dictionary<string, string> { [Path.Combine(root, "schemas", instance + ".yaml")] = draft.SchemaYaml };
        foreach (var (operation, yaml) in draft.Descriptors)
            files[Path.Combine(root, "descriptors", instance, operation + ".yaml")] = yaml;

        var existing = files.Keys.Where(File.Exists).ToList();
        if (existing.Count > 0)
        {
            foreach (var file in existing)
                output.WriteLine($"{Path.GetRelativePath(root, file)} exists; bf draft does not overwrite files. Move it away or edit it by hand.");
            return 1;
        }
        foreach (var (file, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
            output.WriteLine($"wrote {Path.GetRelativePath(root, file)}");
        }
        Directory.CreateDirectory(outPath);
        File.WriteAllText(report, Report(args[0], instance, review));
        output.WriteLine($"{draft.Descriptors.Count} descriptors drafted, {draft.Skipped.Count} operations skipped.");
        return 0;
    }

    /// <summary>String values only; keys starting with an underscore (such as <c>_about</c>) are comments.</summary>
    private static Dictionary<string, string> ReadAnswers(string json)
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in JsonDocument.Parse(json).RootElement.EnumerateObject())
        {
            if (p.Name.StartsWith('_'))
                continue;
            answers[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : throw new JsonException($"answer {p.Name} is not a string");
        }
        return answers;
    }

    private static IEnumerable<Question> OpenQuestions(DraftReview review) =>
        (review.Table?.Questions ?? Array.Empty<Question>()).Concat(review.Endpoints.SelectMany(e => e.Questions)).Where(q => q.Open);

    private static string QuestionsFile(DraftReview review, IReadOnlyDictionary<string, string> answers)
    {
        var open = OpenQuestions(review).ToList();
        var file = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var (k, v) in answers)
            file[k] = v;
        foreach (var q in open)
            file.TryAdd(q.Key, "");
        file["_about"] = open.Select(q => $"{q.Key}: {q.Text}" + (q.Choices.Count > 0 ? $" Choices: {string.Join(" | ", q.Choices.Select(c => $"{c.Value} ({c.Label})"))}." : "")
            + (q.Error != null ? $" Refused: {q.Error}." : "")).ToList();
        return JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static string Report(string openApi, string instance, DraftReview review)
    {
        var lines = new List<string>
        {
            $"# {instance} draft report",
            "",
            $"Drafted by `bf draft` from `{Path.GetFileName(openApi)}`.",
            "",
            "## Endpoints",
            "",
        };
        lines.AddRange(review.Endpoints.Select(e => $"- `{e.Method} {e.Path}` ({e.Operation}): {e.Becomes}, {e.Status}"
            + (e.Reason != null ? $": {e.Reason}" : "") + (e.Problems.Count > 0 ? $": {string.Join("; ", e.Problems)}" : "")));
        var open = OpenQuestions(review).ToList();
        lines.AddRange(new[] { "", "## Open questions", "" });
        lines.AddRange(open.Count == 0 ? new[] { "None." } : open.Select(q => $"- `{q.Key}`: {q.Text}" + (q.Error != null ? $" (refused: {q.Error})" : "")));
        if (review.UnusedAnswers.Count > 0)
        {
            lines.AddRange(new[] { "", "## Answers no question asked", "" });
            lines.AddRange(review.UnusedAnswers.Select(a => $"- `{a}`"));
        }
        var notes = review.Notes;
        if (notes.Count > 0)
        {
            lines.AddRange(new[] { "", "## Notes", "" });
            lines.AddRange(notes.Select(n => $"- {n}"));
        }
        return string.Join("\n", lines) + "\n";
    }
}
