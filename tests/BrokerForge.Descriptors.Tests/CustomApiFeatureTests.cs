using System.Text.Json.Nodes;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Mapping;
using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Rendering;
using BrokerForge.Descriptors.Validation;
using BrokerForge.Keys;
using static BrokerForge.Descriptors.Tests.Fixtures;

namespace BrokerForge.Descriptors.Tests;

/// <summary>Phase 2 descriptor features (HLD D-21): Custom API binds, params and outputs; the json map type; option set writes.</summary>
public class CustomApiFeatureTests
{
    // Descriptor spec 13, Custom API example.
    internal const string SubmitOrder = """
        operation: submitOrder
        version: 1
        binds: customapi:ext_SubmitOrder
        request:
          method: POST
          path: /v2/orders/{orderId}/submit
          pathParams: { orderId: { param: OrderId } }
          headers:
            X-Reason: { param: Reason, omitIfNull: true }
          body:
            template:
              notify: { param: NotifyCustomer }
        response:
          outputs:
            SubmittedAt: { path: $.submittedAt, type: datetime }
            Status:      $.state.code
        errors:
          404: NotFound
          409: Conflict
          default: Fail
        """;

    private static OperationDescriptor Parse(string yaml) => DescriptorParser.Parse(yaml, "test.yaml");

    private static Cell Cell(string col, string type, string? value) => new() { Col = col, Type = type, Value = value };

    private static BindingContext Ctx(Dictionary<string, Cell>? record = null, Dictionary<string, Cell>? inputs = null) =>
        new(RequestRendererTests.Instance, "corr-1", "env-1", "user-1", false, record, null, null, null, null, inputs);

    private static Task<RenderedRequest> Render(OperationDescriptor d, BindingContext ctx) =>
        RequestRenderer.RenderAsync(d.Request, d, ctx, new RequestRendererTests.FakeSecrets(), CancellationToken.None);

    [Fact]
    public void Parse_CustomApiDescriptor_SpecExample()
    {
        var d = Parse(SubmitOrder);
        Assert.Equal("customapi:ext_SubmitOrder", d.Binds);
        Assert.Equal("ext_SubmitOrder", d.CustomApi);
        Assert.Null(d.Table);
        Assert.Equal(new ParamBinding("OrderId", BindingModifiers.None), d.Request.PathParams["orderId"]);
        Assert.Equal(new[] { "SubmittedAt", "Status" }, d.Response!.Outputs.Keys);
        Assert.Equal(new MapEntry("$.submittedAt", "datetime", false), d.Response.Outputs["SubmittedAt"]);
        Assert.Empty(DescriptorValidator.Validate(d, NoAuthNames));
        Assert.Null(Orders("getOrder").CustomApi);
    }

    [Fact]
    public void Parse_IdInCustomApi_RejectedUntilBoundApis()
    {
        var yaml = Mutate(SubmitOrder, "pathParams: { orderId: { param: OrderId } }", "pathParams: { orderId: { id: true } }");
        var ex = Assert.Throws<DescriptorException>(() => Parse(yaml));
        Assert.Equal("request.pathParams.orderId", ex.YamlPath);
        Assert.Contains("D-19", ex.Message);
    }

    [Theory]
    [InlineData("binds: customapi:SubmitOrder", "binds")]
    [InlineData("binds: customapi:ext_SubmitOrder", "table")]
    public void Parse_CustomApiShape_Checked(string from, string path)
    {
        var yaml = path == "table" ? Mutate(SubmitOrder, from, from + "\ntable: ext_order") : Mutate(SubmitOrder, "binds: customapi:ext_SubmitOrder", from);
        var ex = Assert.Throws<DescriptorException>(() => Parse(yaml));
        Assert.Equal(path, ex.YamlPath);
    }

    [Fact]
    public void Parse_OutputsTypeOutsideParamTypes_Rejected()
    {
        var ex = Assert.Throws<DescriptorException>(() => Parse(Mutate(SubmitOrder, "type: datetime }", "type: date }")));
        Assert.Equal("response.outputs.SubmittedAt.type", ex.YamlPath);
    }

    [Fact]
    public void Validate_ParamOutsideCustomApi_Rule2()
    {
        var yaml = Mutate(OrdersYaml("getOrder"), "pathParams: { orderId: { id: true } }", "pathParams: { orderId: { param: OrderId } }");
        Assert.Contains(DescriptorValidator.Validate(Parse(yaml), NoAuthNames), e => e.Rule == 2 && e.YamlPath == "request.pathParams.orderId");
    }

    [Fact]
    public void Validate_CustomApiWithRowsOrMap_Rule6()
    {
        var yaml = Mutate(SubmitOrder, "response:\n  outputs:", "response:\n  rows: $\n  outputs:");
        Assert.Contains(DescriptorValidator.Validate(Parse(yaml), NoAuthNames), e => e.Rule == 6 && e.YamlPath == "response.rows");
    }

    [Fact]
    public void Validate_OutputsOnCrud_Rule6()
    {
        var yaml = OrdersYaml("getOrder").Replace("  unmapped: [ext_lines_json]\n", "").Replace("    ext_lines_json:  { path: $.lines, type: json }\n", "    ext_lines_json:  { path: $.lines, type: json }\n  outputs: { X: $.x }\n");
        Assert.Contains("outputs: { X: $.x }", yaml);
        Assert.Contains(DescriptorValidator.Validate(Parse(yaml), NoAuthNames), e => e.Rule == 6 && e.YamlPath == "response.outputs");
    }

    [Fact]
    public async Task Render_ParamBindings_PathHeaderBody()
    {
        var d = Parse(SubmitOrder);
        var r = await Render(d, Ctx(inputs: new()
        {
            ["OrderId"] = Cell("OrderId", CellTypes.String, "ord/1"),
            ["Reason"] = Cell("Reason", CellTypes.String, "late"),
            ["NotifyCustomer"] = Cell("NotifyCustomer", CellTypes.Bool, "true"),
        }));
        Assert.Equal("/v2/orders/ord%2F1/submit", r.PathAndQuery);
        Assert.Equal("late", r.Headers.Single(h => h.Key == "X-Reason").Value);
        Assert.Equal("""{"notify":true}""", JsonNode.Parse(r.Body!)!.ToJsonString());

        var noReason = await Render(d, Ctx(inputs: new() { ["OrderId"] = Cell("OrderId", CellTypes.String, "1"), ["NotifyCustomer"] = Cell("NotifyCustomer", CellTypes.Bool, "false") }));
        Assert.DoesNotContain(noReason.Headers, h => h.Key == "X-Reason");
    }

    [Fact]
    public async Task Render_RequiredParamMissing_ThrowsValidationNamingIt()
    {
        var d = Parse(Mutate(SubmitOrder, "notify: { param: NotifyCustomer }", "notify: { param: NotifyCustomer, required: true }"));
        var ex = await Assert.ThrowsAsync<MappedSourceException>(() => Render(d, Ctx(inputs: new() { ["OrderId"] = Cell("OrderId", CellTypes.String, "1") })));
        Assert.Equal(SourceErrorKinds.Validation, ex.Kind);
        Assert.Contains("NotifyCustomer", ex.Message);
    }

    [Fact]
    public void Outputs_MapsTypedCells()
    {
        var cells = ResponseMapper.MapOutputs(Parse(SubmitOrder), ResponseMapperTests.Result(200, """{"submittedAt":"2026-01-01T01:00:00+01:00","state":{"code":"submitted"}}"""), "corr-1");
        Assert.Equal(2, cells.Count);
        Assert.Equal(("SubmittedAt", "datetime", "2026-01-01T00:00:00.0000000Z"), (cells[0].Col, cells[0].Type, cells[0].Value));
        Assert.Equal(("Status", "string", "submitted"), (cells[1].Col, cells[1].Type, cells[1].Value));
    }

    [Fact]
    public void Outputs_MissingPath_ThrowsMappingNamingOutput()
    {
        var ex = Assert.Throws<MappingException>(() =>
            ResponseMapper.MapOutputs(Parse(SubmitOrder), ResponseMapperTests.Result(200, """{"submittedAt":"2026-01-01T00:00:00Z"}"""), "corr-1"));
        Assert.Contains("output Status", ex.Message);
        Assert.Contains("$.state.code", ex.Message);
        Assert.Equal("corr-1", ex.CorrelationId);
    }

    [Fact]
    public void Outputs_OptionalMissing_Null()
    {
        var d = Parse(Mutate(SubmitOrder, "    Status:      $.state.code", "    Status:      { path: $.state.code, optional: true }"));
        var cells = ResponseMapper.MapOutputs(d, ResponseMapperTests.Result(200, """{"submittedAt":"2026-01-01T00:00:00Z"}"""), "corr-1");
        Assert.Null(cells.Single(c => c.Col == "Status").Value);
    }

    [Fact]
    public void Map_JsonType_SerialisesSubtree()
    {
        var row = Assert.Single(ResponseMapper.MapRows(Orders("getOrder"),
            ResponseMapperTests.Result(200, """{"id":"ord_1","reference":"R","state":{"code":"open"},"customer":{"ref":"C"},"placedAt":"2026-01-01T00:00:00Z","lines":[{"sku":"A","qty":2}],"totals":{"gross":1}}"""),
            KeyKind.String, "corr-1"));
        var lines = row.Cells.Single(c => c.Col == "ext_lines_json");
        Assert.Equal((CellTypes.String, """[{"sku":"A","qty":2}]"""), (lines.Type, lines.Value));
    }

    [Fact]
    public async Task Render_ColOptionSet_ReverseMapsToSourceValue()
    {
        var r = await Render(Orders("updateOrder"), new BindingContext(RequestRendererTests.Instance, "corr-1", "env-1", "user-1", false,
            new Dictionary<string, Cell> { ["ext_status"] = Cell("ext_status", CellTypes.OptionSet, "100000001") }, new HashSet<string> { "ext_status" }, ExternalKey.FromString("ord_1"), null, null));
        Assert.Equal("""{"state":{"code":"shipped"}}""", JsonNode.Parse(r.Body!)!.ToJsonString());
    }

    [Fact]
    public async Task Render_ColOptionSet_UnknownValue_ThrowsValidation()
    {
        var ex = await Assert.ThrowsAsync<MappedSourceException>(() => Render(Orders("createOrder"),
            Ctx(record: new() { ["ext_customerref"] = Cell("ext_customerref", CellTypes.String, "C"), ["ext_status"] = Cell("ext_status", CellTypes.OptionSet, "100000009") })));
        Assert.Equal(SourceErrorKinds.Validation, ex.Kind);
        Assert.Contains("ext_status", ex.Message);
        Assert.Contains("100000009", ex.Message);
    }

    [Fact]
    public async Task Render_OptionSetCellWithoutMap_StillRefused()
    {
        var d = Parse("operation: op\nversion: 1\nbinds: Create\ntable: ext_order\nrequest:\n  method: POST\n  path: /v2/o\n  body:\n    template:\n      state: { col: ext_status }\nresponse:\n  key: $.id\n");
        var ex = await Assert.ThrowsAsync<UnsupportedQueryException>(() => Render(d, Ctx(record: new() { ["ext_status"] = Cell("ext_status", CellTypes.OptionSet, "100000000") })));
        Assert.Contains("optionset", ex.Message);
    }

    [Fact]
    public void Validate_ColOptionSetMapUndeclared_Rule6()
    {
        var yaml = Mutate(OrdersYaml("createOrder"), "optionsets:\n  orderStatus:", "optionsets:\n  otherStatus:");
        Assert.Contains(DescriptorValidator.Validate(Parse(yaml), NoAuthNames), e => e.Rule == 6 && e.YamlPath == "request.body.template.state.code");
    }
}
