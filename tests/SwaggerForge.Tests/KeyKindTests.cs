using BrokerForge.SwaggerForge;

namespace SwaggerForge.Tests;

/// <summary>The review names the key's kind, so a design can pick its key strategy without a guess (plan 4 task 1).</summary>
public class KeyKindTests
{
    private static string Definition(string idSchema) => $$"""
        openapi: 3.0.3
        info: { title: Keys, version: "1" }
        paths:
          /widgets/{id}:
            get:
              operationId: getWidget
              parameters: [{ name: id, in: path, required: true, schema: {{idSchema}} }]
              responses:
                "200":
                  description: ok
                  content:
                    application/json:
                      schema:
                        type: object
                        required: [id, name]
                        properties:
                          id: {{idSchema}}
                          name: { type: string, maxLength: 50 }
        """;

    private static string? KindOf(string idSchema) =>
        DraftService.Review(Definition(idSchema), DraftServiceTests.Options, new Dictionary<string, string>()).Table!.KeyKind;

    [Fact]
    public void Review_UuidKey_KeyKindGuid() => Assert.Equal("Guid", KindOf("{ type: string, format: uuid }"));

    [Fact]
    public void Review_IntegerKey_KeyKindInt32() => Assert.Equal("Int32", KindOf("{ type: integer }"));

    [Fact]
    public void Review_Int64Key_KeyKindInt64() => Assert.Equal("Int64", KindOf("{ type: integer, format: int64 }"));

    [Fact]
    public void Review_StringKey_KeyKindString() => Assert.Equal("String", KindOf("{ type: string }"));
}
