using BrokerForge.Contracts;
using BrokerForge.Descriptors.Mapping;
using BrokerForge.Descriptors.Parsing;
using static BrokerForge.Descriptors.Tests.Fixtures;
using static BrokerForge.Descriptors.Tests.ResponseMapperTests;

namespace BrokerForge.Descriptors.Tests;

public class ErrorMapperTests
{
    [Fact]
    public void Success_DoesNotThrow()
    {
        ErrorMapper.ThrowIfError(Orders("getOrder"), Result(200, "{}"), "c");
        ErrorMapper.ThrowIfError(Orders("deleteOrder"), Result(204, ""), "c");
    }

    [Fact]
    public void Error_Mapped404_NotFound()
    {
        var ex = Assert.Throws<MappedSourceException>(() => ErrorMapper.ThrowIfError(Orders("getOrder"), Result(404, """{"error":{"detail":"Order x not found"}}"""), "corr-1"));
        Assert.Equal(SourceErrorKinds.NotFound, ex.Kind);
        Assert.Equal(404, ex.Status);
        Assert.Equal("corr-1", ex.CorrelationId);
    }

    [Fact]
    public void Error_Mapped422_ValidationMessageFromPathTruncated500()
    {
        var detail = new string('x', 900);
        var ex = Assert.Throws<MappedSourceException>(() => ErrorMapper.ThrowIfError(Orders("createOrder"), Result(422, "{\"error\":{\"detail\":\"customer.ref is required " + detail + "\"}}"), "corr-1"));
        Assert.Equal(SourceErrorKinds.Validation, ex.Kind);
        Assert.Contains("customer.ref is required", ex.Message);
        Assert.DoesNotContain("\"error\"", ex.Message);
        Assert.True(ex.Message.Length <= 500 + 200, $"message length {ex.Message.Length}");
        Assert.DoesNotContain(new string('x', 501), ex.Message);
    }

    [Fact]
    public void Error_Unmapped500_SourceExceptionWithStatusAndCorrelationId()
    {
        var ex = Assert.Throws<SourceException>(() => ErrorMapper.ThrowIfError(Orders("listOrders"), Result(500, """{"error":{"detail":"internal failure"}}"""), "corr-1"));
        Assert.IsNotType<MappedSourceException>(ex);
        Assert.Equal(500, ex.Status);
        Assert.Equal("corr-1", ex.CorrelationId);
        Assert.Contains("500", ex.Message);
        Assert.Contains("internal failure", ex.Message);
    }

    [Fact]
    public void Error_BodyExcerptRedactsSecretsAndTruncates()
    {
        var body = "key=bf-test-dummy-secret " + new string('y', 2000);
        var ex = Assert.Throws<SourceException>(() => ErrorMapper.ThrowIfError(Orders("listOrders"), Result(502, body), "c", new[] { "bf-test-dummy-secret" }));
        Assert.DoesNotContain("bf-test-dummy-secret", ex.Message);
        Assert.DoesNotContain(new string('y', 501), ex.Message);
    }

    [Fact]
    public void Error_FailMappingIsSourceException()
    {
        var yaml = Mutate(OrdersYaml("getOrder"), "  404: NotFound", "  404: Fail");
        var ex = Assert.Throws<SourceException>(() => ErrorMapper.ThrowIfError(DescriptorParser.Parse(yaml, "g"), Result(404, ""), "c"));
        Assert.IsNotType<MappedSourceException>(ex);
    }

    [Fact]
    public void Error_SuccessBodyCheckFails_SourceException()
    {
        var yaml = Mutate(OrdersYaml("getOrder"), "  default: Fail", "  default: Fail\n  successBodyCheck: { path: $.ok, equals: \"true\" }");
        var d = DescriptorParser.Parse(yaml, "g");
        ErrorMapper.ThrowIfError(d, Result(200, """{"ok":true}"""), "c");
        var ex = Assert.Throws<SourceException>(() => ErrorMapper.ThrowIfError(d, Result(200, """{"ok":false,"message":"soft failure"}"""), "c"));
        Assert.Equal(200, ex.Status);
        Assert.Contains("$.ok", ex.Message);
        Assert.Throws<SourceException>(() => ErrorMapper.ThrowIfError(d, Result(200, """{"other":1}"""), "c"));
    }
}
