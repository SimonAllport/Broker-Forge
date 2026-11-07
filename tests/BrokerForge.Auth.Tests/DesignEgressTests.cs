using System.Net;

namespace BrokerForge.Auth.Tests;

public class DesignEgressTests
{
    [Theory]
    [InlineData("127.0.0.1")] [InlineData("127.255.0.9")] [InlineData("0.0.0.0")] [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")] [InlineData("100.64.0.1")] [InlineData("100.127.255.255")] [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")] [InlineData("172.31.255.255")] [InlineData("192.168.0.1")]
    [InlineData("::1")] [InlineData("::")] [InlineData("fe80::1")] [InlineData("febf::1")] [InlineData("fc00::1")] [InlineData("fd12:3456::1")]
    [InlineData("::ffff:127.0.0.1")] [InlineData("::ffff:169.254.169.254")] [InlineData("::ffff:10.1.2.3")]
    public void PrivateAddresses(string address) => Assert.True(DesignEgress.IsPrivate(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")] [InlineData("100.63.255.255")] [InlineData("100.128.0.0")] [InlineData("172.15.255.255")] [InlineData("172.32.0.0")]
    [InlineData("192.169.0.1")] [InlineData("169.253.0.1")] [InlineData("20.50.2.1")] [InlineData("2001:4860::8888")] [InlineData("fec0::1")] [InlineData("::ffff:8.8.8.8")]
    public void PublicAddresses(string address) => Assert.False(DesignEgress.IsPrivate(IPAddress.Parse(address)));
}
