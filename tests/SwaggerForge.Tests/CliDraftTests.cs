using BrokerForge.Cli;

namespace SwaggerForge.Tests;

public class CliDraftTests
{
    private static string WidgetAnswers(DirectoryInfo root)
    {
        var file = Path.Combine(root.FullName, "widgets-answers.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(DrafterTests.AnswersFor("unsupported.yaml")));
        return file;
    }

    [Fact]
    public void Cli_Draft_WithAnswers_WritesAndValidates()
    {
        var root = Directory.CreateTempSubdirectory("bf-draft-");
        try
        {
            var output = new StringWriter();
            var code = DraftCommand.Run(new[] { DrafterTests.Fixture("unsupported.yaml"), "--instance", "WidgetsAPI", "--prefix", "ext", "--out", "out", "--root", root.FullName, "--answers", WidgetAnswers(root) }, output);
            Assert.True(code == 0, output.ToString());
            Assert.True(File.Exists(Path.Combine(root.FullName, "schemas", "WidgetsAPI.yaml")));
            Assert.True(File.Exists(Path.Combine(root.FullName, "descriptors", "WidgetsAPI", "listWidgets.yaml")));
            var report = File.ReadAllText(Path.Combine(root.FullName, "out", "WidgetsAPI-draft-report.md"));
            Assert.Contains("/widgets/{widgetId}/variant", report);
            Assert.Contains("oneOf", report);

            var validate = new StringWriter();
            Assert.Equal(0, ValidateCommand.Run(new[] { Path.Combine(root.FullName, "descriptors"), "--schemas", Path.Combine(root.FullName, "schemas") }, validate));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void Cli_Draft_RefusesToOverwrite()
    {
        var root = Directory.CreateTempSubdirectory("bf-draft-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "schemas"));
            File.WriteAllText(Path.Combine(root.FullName, "schemas", "WidgetsAPI.yaml"), "keep me");
            var output = new StringWriter();
            var code = DraftCommand.Run(new[] { DrafterTests.Fixture("unsupported.yaml"), "--instance", "WidgetsAPI", "--prefix", "ext", "--out", "out", "--root", root.FullName, "--answers", WidgetAnswers(root) }, output);
            Assert.Equal(1, code);
            Assert.Contains("WidgetsAPI.yaml", output.ToString());
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(root.FullName, "schemas", "WidgetsAPI.yaml")));
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "descriptors")));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void Cli_Draft_RefusedAnswer_KeptInQuestionsFile()
    {
        var root = Directory.CreateTempSubdirectory("bf-draft-");
        try
        {
            var answers = Path.Combine(root.FullName, "answers.json");
            File.WriteAllText(answers, """{ "op.listOrders.paging": "sideways" }""");
            var output = new StringWriter();
            var code = DraftCommand.Run(new[] { DrafterTests.Fixture("orders-openapi.yaml"), "--instance", "OrdersAPI", "--prefix", "ext", "--out", "out", "--root", root.FullName, "--answers", answers }, output);
            Assert.True(code == 3, output.ToString());
            var questions = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "out", "OrdersAPI-questions.json"))).RootElement;
            Assert.Equal("sideways", questions.GetProperty("op.listOrders.paging").GetString());
            Assert.Contains(questions.GetProperty("_about").EnumerateArray(), l => l.GetString()!.StartsWith("op.listOrders.paging:") && l.GetString()!.Contains("Refused"));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void Cli_Draft_WithoutAnswers_WritesQuestionsAndExit3()
    {
        var root = Directory.CreateTempSubdirectory("bf-draft-");
        try
        {
            var output = new StringWriter();
            var code = DraftCommand.Run(new[] { DrafterTests.Fixture("orders-openapi.yaml"), "--instance", "OrdersAPI", "--prefix", "ext", "--out", "out", "--root", root.FullName }, output);
            Assert.True(code == 3, output.ToString());
            var questions = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "out", "OrdersAPI-questions.json"))).RootElement;
            Assert.Equal("", questions.GetProperty("op.listOrders.paging").GetString());
            Assert.Contains(questions.GetProperty("_about").EnumerateArray(), l => l.GetString()!.StartsWith("op.listOrders.paging:") && l.GetString()!.Contains("cursor"));
            Assert.Contains("NeedsAnswers", File.ReadAllText(Path.Combine(root.FullName, "out", "OrdersAPI-draft-report.md")));
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "descriptors")));
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "schemas")));

            // The questions file, filled in, is a valid answers file.
            var filled = new Dictionary<string, object>();
            foreach (var p in questions.EnumerateObject())
                filled[p.Name] = p.Name == "_about" ? p.Value.Clone() : (object)p.Value.GetString()!;
            foreach (var (k, v) in DrafterTests.AnswersFor("orders-openapi.yaml"))
                filled[k] = v;
            var answers = Path.Combine(root.FullName, "answers.json");
            File.WriteAllText(answers, System.Text.Json.JsonSerializer.Serialize(filled));
            var again = new StringWriter();
            Assert.True(DraftCommand.Run(new[] { DrafterTests.Fixture("orders-openapi.yaml"), "--instance", "OrdersAPI", "--prefix", "ext", "--out", "out", "--root", root.FullName, "--answers", answers }, again) == 0, again.ToString());
            Assert.True(File.Exists(Path.Combine(root.FullName, "descriptors", "OrdersAPI", "listOrders.yaml")));
        }
        finally
        {
            root.Delete(true);
        }
    }
}
