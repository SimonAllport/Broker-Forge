using System.Text.Json;
using BrokerForge.SwaggerForge;
using YamlDotNet.RepresentationModel;

namespace SwaggerForge.Tests;

/// <summary>Today's sources, re-drafted from their OpenAPI files plus answers, against the committed descriptors and schemas.</summary>
public class ReproduceTests
{
    private static readonly string Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static Dictionary<string, string> AnswersFile(string name) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(DrafterTests.Fixture(name)))!;

    /// <summary>
    /// The YAML as a tree with sorted keys and scalars as text, so quoting and flow style do not matter. Option set map names
    /// are replaced by their position, because a descriptor's map name is internal to it and changes no behaviour.
    /// </summary>
    internal static string Canonical(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = stream.Documents[0].RootNode;
        var names = root is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode("optionsets"), out var sets) && sets is YamlMappingNode setMap
            ? setMap.Children.Keys.Select((k, i) => (((YamlScalarNode)k).Value!, $"set{i}")).ToDictionary(x => x.Item1, x => x.Item2)
            : new Dictionary<string, string>();
        return JsonSerializer.Serialize(Tree(root, names), new JsonSerializerOptions { WriteIndented = true });
    }

    private static object? Tree(YamlNode node, Dictionary<string, string> sets, string? parentKey = null) => node switch
    {
        YamlMappingNode map => new SortedDictionary<string, object?>(map.Children.ToDictionary(
            c => parentKey == "optionsets" && sets.TryGetValue(((YamlScalarNode)c.Key).Value!, out var n) ? n : ((YamlScalarNode)c.Key).Value!,
            c => Tree(c.Value, sets, ((YamlScalarNode)c.Key).Value)), StringComparer.Ordinal),
        YamlSequenceNode seq => seq.Children.Select(c => Tree(c, sets)).ToList(),
        YamlScalarNode s when s.Value is { } v && v.StartsWith("optionset:", StringComparison.Ordinal) && sets.TryGetValue(v["optionset:".Length..], out var n) => "optionset:" + n,
        YamlScalarNode s when parentKey == "optionset" && s.Value is { } v && sets.TryGetValue(v, out var n) => n,
        YamlScalarNode s => s.Value,
        _ => null,
    };

    private static DraftResult Draft(string openApi, string answers) =>
        DrafterTests.Ready(DraftService.Review(File.ReadAllText(openApi), new DraftOptions("X", "ext"), AnswersFile(answers)));

    [Fact]
    public void Reproduce_Inventory_ExactlyTheCommittedDescriptors()
    {
        var r = Draft(Path.Combine(Repo, "tools", "mock-api", "inventory-openapi.yaml"), "inventory-answers.json");
        var committed = Directory.GetFiles(Path.Combine(Repo, "descriptors", "InventoryAPI"), "*.yaml").ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText);
        Assert.Equal(committed.Keys.Order(), r.Descriptors.Keys.Order());
        foreach (var (name, yaml) in committed)
            Assert.Equal(Canonical(yaml), Canonical(r.Descriptors[name]));
        Assert.Equal(Canonical(File.ReadAllText(Path.Combine(Repo, "schemas", "InventoryAPI.yaml"))), Canonical(r.SchemaYaml));
    }

    [Fact]
    public void Reproduce_Orders_AllFiveMatchTheCommittedDescriptors()
    {
        var r = Draft(DrafterTests.Fixture("orders-openapi.yaml"), "orders-answers.json");
        var committed = Directory.GetFiles(Path.Combine(Repo, "descriptors", "OrdersAPI"), "*.yaml").ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText);
        Assert.Equal(committed.Keys.Order(), r.Descriptors.Keys.Order());
        // Two differences. createOrder's account is a fixed value the admin gives (D-35), not instance config. And its
        // `409: Conflict` isn't drafted: the definition documents no 409 and the mock never answers one, so a 409 would be Fail.
        foreach (var (name, yaml) in committed)
            Assert.Equal(Canonical(yaml.Replace("{ accountId: { instance: accountId } }", "{ accountId: { static: acc-1 } }").Replace("  409: Conflict\n", "")),
                Canonical(r.Descriptors[name]));
        // A memo with no length is created at 100000, the length the committed schema states.
        Assert.Equal(Canonical(File.ReadAllText(Path.Combine(Repo, "schemas", "OrdersAPI.yaml")).Replace("type: memo, maxLength: 100000,", "type: memo,")),
            Canonical(r.SchemaYaml));
    }

    /// <summary>
    /// <see cref="Canonical"/>, with error entries that say Fail dropped: Fail is the default, so `401: Fail` behaves as no entry.
    /// </summary>
    private static string WithoutFailEntries(string yaml)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Canonical(yaml))!;
        if (node["errors"] is System.Text.Json.Nodes.JsonObject errors)
            foreach (var key in errors.Where(e => e.Key != "default" && e.Value?.GetValueKind() == JsonValueKind.String && (string?)e.Value == "Fail").Select(e => e.Key).ToList())
                errors.Remove(key);
        return node.ToJsonString();
    }

    [Fact]
    public void Reproduce_Tickets_MatchesCommittedDescriptors()
    {
        var r = Draft(Path.Combine(Repo, "tools", "mock-api", "tickets-openapi.yaml"), "tickets-answers.json");
        var committed = Directory.GetFiles(Path.Combine(Repo, "descriptors", "TicketsAPI"), "*.yaml").ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText);
        Assert.Equal(committed.Keys.Order(), r.Descriptors.Keys.Order());
        foreach (var (name, yaml) in committed)
            Assert.Equal(WithoutFailEntries(yaml), WithoutFailEntries(r.Descriptors[name]));
        Assert.Equal(Canonical(File.ReadAllText(Path.Combine(Repo, "schemas", "TicketsAPI.yaml"))), Canonical(r.SchemaYaml));
    }

    [Fact]
    public void Reproduce_TicketsEntra_MatchesCommittedDescriptorsApartFromOperationNames()
    {
        var r = Draft(Path.Combine(Repo, "tools", "mock-api", "tickets-openapi.yaml"), "tickets-entra-answers.json");
        // The committed files are named listEntraTickets and so on; the design takes the definition's operationIds.
        var committed = Directory.GetFiles(Path.Combine(Repo, "descriptors", "TicketsEntraAPI"), "*.yaml")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f).Replace("EntraTicket", "Ticket"), f => File.ReadAllText(f).Replace("EntraTicket", "Ticket"));
        Assert.Equal(committed.Keys.Order(), r.Descriptors.Keys.Order());
        foreach (var (name, yaml) in committed)
            Assert.Equal(WithoutFailEntries(yaml), WithoutFailEntries(r.Descriptors[name]));
        Assert.Equal(Canonical(File.ReadAllText(Path.Combine(Repo, "schemas", "TicketsEntraAPI.yaml"))), Canonical(r.SchemaYaml));
    }

    [Fact]
    public void Review_SameInput_SameOutputAndQuestionOrder()
    {
        var text = File.ReadAllText(DrafterTests.Fixture("orders-openapi.yaml"));
        var a = DraftService.Review(text, DraftServiceTests.Options, AnswersFile("orders-answers.json")).Result!;
        var b = DraftService.Review(text, DraftServiceTests.Options, AnswersFile("orders-answers.json")).Result!;
        Assert.Equal(a.SchemaYaml, b.SchemaYaml);
        Assert.Equal(a.Descriptors, b.Descriptors);

        IEnumerable<string> Keys(DraftReview r) => r.Table!.Questions.Concat(r.Endpoints.SelectMany(e => e.Questions)).Select(q => q.Key);
        var empty = new Dictionary<string, string>();
        Assert.Equal(Keys(DraftService.Review(text, DraftServiceTests.Options, empty)), Keys(DraftService.Review(text, DraftServiceTests.Options, empty)));
    }
}
