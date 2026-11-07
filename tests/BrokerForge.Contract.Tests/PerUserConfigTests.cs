using BrokerForge.Abstractions;
using BrokerForge.Host.Config;
using Microsoft.Extensions.Configuration;

namespace BrokerForge.Contract.Tests;

/// <summary>Host settings for OAuth, Impersonate and SSO and the async context rule (HLD D-28, D-29).</summary>
public class PerUserConfigTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("bf-peruser-");

    public void Dispose() => _root.Delete(true);

    /// <summary>A Tickets instance in the given mode, a Tickets OAuth resource and an Entra one; edits are applied last (null removes).</summary>
    private Dictionary<string, string?> Settings(string mode, params (string Key, string? Value)[] edits)
    {
        Directory.CreateDirectory(Path.Combine(_root.FullName, "descriptors"));
        var s = new Dictionary<string, string?>
        {
            ["Config:DescriptorsPath"] = Path.Combine(_root.FullName, "descriptors"),
            ["Connect:PublicBaseUrl"] = "https://bf-host.example/",
            ["Connect:ClientId"] = "connect-test-client",
            ["Instances:Tickets:BrokerType"] = "rest",
            ["Instances:Tickets:BaseUrl"] = "https://tickets.example",
            ["Instances:Tickets:Auth:Mode"] = mode,
            ["OAuthResources:Tickets:AuthorizeUrl"] = "https://tickets.example/tk/oauth/authorize",
            ["OAuthResources:Tickets:TokenUrl"] = "https://tickets.example/tk/oauth/token",
            ["OAuthResources:Tickets:Scopes:0"] = "tickets.read",
            ["OAuthResources:Tickets:ClientId"] = "bf-tickets",
            ["OAuthResources:Tickets:ClientSecretRef"] = "env:BF_TEST_TK_CLIENT_SECRET",
            ["OAuthResources:Entra:AuthorizeUrl"] = "https://login.microsoftonline.com/t/oauth2/v2.0/authorize",
            ["OAuthResources:Entra:TokenUrl"] = "https://login.microsoftonline.com/t/oauth2/v2.0/token",
            ["OAuthResources:Entra:Scopes:0"] = "api://tickets/user_impersonation",
            ["OAuthResources:Entra:Scopes:1"] = "offline_access",
            ["OAuthResources:Entra:UsesConnectApp"] = "true",
        };
        switch (mode)
        {
            case "OAuth": s["Instances:Tickets:Auth:OAuthResource"] = "Tickets"; break;
            case "Impersonate": s["Instances:Tickets:Auth:OAuthResource"] = "Entra"; break;
            case "Sso": s["Instances:Tickets:Auth:SsoKind"] = "basic"; break;
        }
        foreach (var (k, v) in edits)
            if (v == null) s.Remove(k); else s[k] = v;
        return s;
    }

    private static HostConfigStore Load(Dictionary<string, string?> settings) =>
        HostConfigStore.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

    private static string Problems(Dictionary<string, string?> settings) =>
        Assert.Throws<ConfigurationException>(() => Load(settings)).Message;

    [Fact]
    public void Config_StoreSecretOnFileInstance_Fails() =>
        Assert.Contains("Instances:Tickets: store:apiKey is an instance secret, which only a connection design can use",
            Problems(Settings("Static", ("Instances:Tickets:Auth:StaticKind", "apiKeyHeader"), ("Instances:Tickets:Auth:HeaderName", "X-Key"),
                ("Instances:Tickets:Auth:SecretRef", "store:apiKey"))));

    [Fact]
    public void Config_PerUserModesLoad()
    {
        var oauth = Load(Settings("OAuth"));
        Assert.Equal(AuthMode.OAuth, oauth.Instance("Tickets")!.Auth.Mode);
        Assert.Equal("Tickets", oauth.Instance("Tickets")!.Auth.OAuthResource);
        Assert.Equal(AsyncContext.TriggeringUser, oauth.Instance("Tickets")!.Auth.AsyncContext);
        var tickets = oauth.OAuthResource("Tickets");
        Assert.Equal(new[] { "tickets.read" }, tickets.Scopes);
        Assert.False(tickets.UsesConnectApp);
        Assert.Equal("https://bf-host.example/connect/Tickets", oauth.ConnectUrl(oauth.Instance("Tickets")!));

        var impersonate = Load(Settings("Impersonate"));
        Assert.True(impersonate.OAuthResource("Entra").UsesConnectApp);
        Assert.Null(impersonate.OAuthResource("Entra").ClientId);

        var sso = Load(Settings("Sso"));
        Assert.Equal("basic", sso.Instance("Tickets")!.Auth.SsoKind);
    }

    [Fact]
    public void Config_AsyncServiceIdentity_LoadsAsyncAuth()
    {
        var store = Load(Settings("OAuth",
            ("Instances:Tickets:Auth:AsyncContext", "ServiceIdentity"),
            ("Instances:Tickets:Auth:AsyncAuth:Mode", "Static"),
            ("Instances:Tickets:Auth:AsyncAuth:StaticKind", "apiKeyHeader"),
            ("Instances:Tickets:Auth:AsyncAuth:HeaderName", "X-Api-Key"),
            ("Instances:Tickets:Auth:AsyncAuth:SecretRef", "env:BF_TEST_MOCK_KEY")));
        var auth = store.Instance("Tickets")!.Auth;
        Assert.Equal(AsyncContext.ServiceIdentity, auth.AsyncContext);
        Assert.Equal(AuthMode.Static, auth.AsyncAuth!.Mode);
        Assert.Equal("X-Api-Key", auth.AsyncAuth.HeaderName);
    }

    [Theory]
    [InlineData("OAuth", "Instances:Tickets:Auth:OAuthResource", null, "Instances:Tickets:Auth:OAuthResource")]
    [InlineData("OAuth", "Instances:Tickets:Auth:OAuthResource", "Nope", "Instances:Tickets:Auth:OAuthResource")]
    [InlineData("OAuth", "Instances:Tickets:Auth:OAuthResource", "Entra", "Instances:Tickets:Auth:OAuthResource")]
    [InlineData("Impersonate", "Instances:Tickets:Auth:OAuthResource", "Tickets", "Instances:Tickets:Auth:OAuthResource")]
    [InlineData("Sso", "Instances:Tickets:Auth:SsoKind", "digest", "Instances:Tickets:Auth:SsoKind")]
    [InlineData("Sso", "Instances:Tickets:Auth:SsoKind", "apiKeyHeader", "Instances:Tickets:Auth:HeaderName")]
    [InlineData("OAuth", "Instances:Tickets:Auth:AsyncContext", "ServiceIdentity", "Instances:Tickets:Auth:AsyncAuth")]
    [InlineData("OAuth", "Instances:Tickets:Auth:AsyncContext", "Sometimes", "Instances:Tickets:Auth:AsyncContext")]
    [InlineData("OAuth", "Connect:PublicBaseUrl", null, "Connect:PublicBaseUrl")]
    [InlineData("Sso", "Connect:ClientId", null, "Connect:ClientId")]
    [InlineData("OAuth", "OAuthResources:Tickets:ClientId", null, "OAuthResources:Tickets:ClientId")]
    [InlineData("OAuth", "OAuthResources:Tickets:TokenUrl", "not a url", "OAuthResources:Tickets:TokenUrl")]
    [InlineData("OAuth", "OAuthResources:Tickets:Scopes:0", null, "OAuthResources:Tickets:Scopes")]
    [InlineData("Impersonate", "OAuthResources:Entra:ClientId", "x", "OAuthResources:Entra:ClientId")]
    [InlineData("OAuth", "OAuthResources:Tickets:ClientSecretRef", "plain-text", "OAuthResources:Tickets")]
    public void Config_PerUserProblem_NamesSetting(string mode, string key, string? value, string named)
    {
        var message = Problems(Settings(mode, (key, value)));
        Assert.Contains(named, message);
    }

    [Fact]
    public void Config_AsyncAuthPerUserMode_Refused()
    {
        var message = Problems(Settings("OAuth",
            ("Instances:Tickets:Auth:AsyncContext", "ServiceIdentity"),
            ("Instances:Tickets:Auth:AsyncAuth:Mode", "Sso"),
            ("Instances:Tickets:Auth:AsyncAuth:SsoKind", "basic")));
        Assert.Contains("Instances:Tickets:Auth:AsyncAuth:Mode", message);
        Assert.Contains("ServiceAccount or Static", message);
    }

    [Fact]
    public void Config_TriggeringUserWithAsyncAuth_Refused()
    {
        var message = Problems(Settings("OAuth",
            ("Instances:Tickets:Auth:AsyncAuth:Mode", "ServiceAccount"),
            ("Instances:Tickets:Auth:AsyncAuth:Scope", "api://x/.default")));
        Assert.Contains("Instances:Tickets:Auth:AsyncAuth", message);
        Assert.Contains("TriggeringUser", message);
    }

    [Fact]
    public void Config_ServiceAccountWithAsyncAuth_Refused()
    {
        var settings = Settings("ServiceAccount",
            ("Instances:Tickets:Auth:Scope", "api://x/.default"),
            ("Instances:Tickets:Auth:AsyncContext", "ServiceIdentity"),
            ("Instances:Tickets:Auth:AsyncAuth:Mode", "ServiceAccount"),
            ("Instances:Tickets:Auth:AsyncAuth:Scope", "api://x/.default"));
        Assert.Contains("only for per-user modes", Problems(settings));
    }
}
