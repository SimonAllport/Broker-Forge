using BrokerForge.Abstractions;
using BrokerForge.Host.Errors;

namespace BrokerForge.Contract.Tests;

public class ErrorTranslatorTests
{
    [Fact]
    public void FrameworkInvalidOperation_InternalErrorWithoutItsMessage()
    {
        var (status, payload) = ErrorTranslator.Translate(new InvalidOperationException("Collection was modified; enumeration operation may not execute."), "corr-1");
        Assert.Equal(500, status);
        Assert.Equal("InternalError", payload.Error);
        Assert.DoesNotContain("Collection", payload.Message);
        Assert.Equal("corr-1", payload.CorrelationId);
    }

    [Fact]
    public void ConfigurationException_ConfigurationErrorWithItsMessage()
    {
        var (status, payload) = ErrorTranslator.Translate(new ConfigurationException("Service instance OrdersAPI has no config value `tenant`."), "corr-1");
        Assert.Equal(500, status);
        Assert.Equal("ConfigurationError", payload.Error);
        Assert.Contains("tenant", payload.Message);
    }
}
