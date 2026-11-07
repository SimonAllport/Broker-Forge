using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using BrokerForge.Contracts;
using BrokerForge.Plugin.Transport;
using Microsoft.Xrm.Sdk;

namespace BrokerForge.Plugin.Tests;

/// <summary>The plug-in entry point and host client, run against SDK fakes and a stub host on localhost.</summary>
public class ProviderPluginTests
{
    private static readonly Guid Correlation = Guid.Parse("c0ffee00-0000-0000-0000-000000000001");

    /// <summary>An interface fake that answers getters from a dictionary and everything else with a default.</summary>
    public class Fake<T> : DispatchProxy where T : class
    {
        private Func<MethodInfo, object[], object> _handler = null!;

        public static T With(Func<MethodInfo, object[], object> handler)
        {
            var proxy = Create<T, Fake<T>>();
            ((Fake<T>)(object)proxy)._handler = handler;
            return proxy;
        }

        public static T WithProperties(IDictionary<string, object> values) => With((m, _) =>
            values.TryGetValue(m.Name.StartsWith("get_") ? m.Name[4..] : m.Name, out var v) ? v : Default(m.ReturnType));

        private static object Default(Type t) => t.IsValueType && t != typeof(void) ? Activator.CreateInstance(t)! : null!;

        protected override object Invoke(MethodInfo? method, object?[]? args) => _handler(method!, args!);
    }

    /// <summary>Answers every POST with one status and body, counts the calls and keeps the last request body.</summary>
    internal sealed class StubHost : IDisposable
    {
        private readonly HttpListener _listener = new();
        public int Calls;
        public string? LastBody;
        public string Url { get; }

        public StubHost(int status, string body)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch (Exception) { return; }
                    using (var reader = new StreamReader(ctx.Request.InputStream))
                        LastBody = await reader.ReadToEndAsync();
                    Interlocked.Increment(ref Calls);
                    ctx.Response.StatusCode = status;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose() => _listener.Close();
    }

    internal static IManagedIdentityService Identity(Exception? fails = null) =>
        Fake<IManagedIdentityService>.With((_, _) => fails != null ? throw fails : "test-token");

    private static IServiceProvider Services(string hostUrl, string message, ParameterCollection inputs)
    {
        var context = Fake<IPluginExecutionContext6>.WithProperties(new Dictionary<string, object>
        {
            ["CorrelationId"] = Correlation,
            ["MessageName"] = message,
            ["PrimaryEntityName"] = "ext_order",
            ["InputParameters"] = inputs,
            ["OutputParameters"] = new ParameterCollection(),
            ["EnvironmentId"] = "a4c0d8aa-0000-0000-0000-000000000001",
        });
        var dataSource = new Entity("bf_brokerdatasource", Guid.NewGuid())
        {
            ["bf_hosturl"] = hostUrl,
            ["bf_scope"] = "api://brokerforge-test/.default",
            ["bf_serviceinstance"] = "OrdersAPI",
        };
        var retriever = Fake<IEntityDataSourceRetrieverService>.With((_, _) => dataSource);
        var tracing = Fake<ITracingService>.With((_, _) => null!);
        var services = new Dictionary<Type, object>
        {
            [typeof(IPluginExecutionContext)] = context,
            [typeof(ITracingService)] = tracing,
            [typeof(IManagedIdentityService)] = Identity(),
            [typeof(IEntityDataSourceRetrieverService)] = retriever,
        };
        return Fake<IServiceProvider>.With((_, args) => services.GetValueOrDefault((Type)args[0])!);
    }

    private static CanonicalRequest Request() => new() { CorrelationId = Correlation.ToString(), Message = Messages.Delete, Id = Guid.NewGuid().ToString() };

    private static void AssertCarriesCorrelation(Exception ex) =>
        Assert.EndsWith($"(correlationId {Correlation})", ex.Message);

    [Fact]
    public void HostClient_HostUnreachable_BrokerUnavailableWithCorrelationId()
    {
        string url;
        using (var stopped = new StubHost(200, "{}"))
            url = stopped.Url;
        var ex = Assert.Throws<BrokerUnavailableException>(() => new HostClient(url, "s", Identity()).Execute(Request()));
        Assert.Equal(Correlation.ToString(), ex.CorrelationId);
        Assert.Contains("did not answer", ex.Message);
    }

    [Fact]
    public void HostClient_ErrorWithoutBody_BrokerUnavailable()
    {
        using var host = new StubHost(502, "");
        var ex = Assert.Throws<BrokerUnavailableException>(() => new HostClient(host.Url, "s", Identity()).Execute(Request()));
        Assert.Contains("HTTP 502", ex.Message);
    }

    [Fact]
    public void HostClient_NoManagedIdentityToken_BrokerUnavailable()
    {
        using var host = new StubHost(200, "{}");
        var ex = Assert.Throws<BrokerUnavailableException>(() => new HostClient(host.Url, "s", Identity(new InvalidOperationException("no identity"))).Execute(Request()));
        Assert.Contains("managed identity", ex.Message);
        Assert.Equal(0, host.Calls);
    }

    [Fact]
    public void Execute_HostRepliesWithInvalidJson_FailsWithCorrelationId()
    {
        using var host = new StubHost(200, "<html>proxy error</html>");
        var inputs = new ParameterCollection { ["Target"] = new EntityReference("ext_order", Guid.NewGuid()) };
        var ex = Assert.Throws<InvalidPluginExecutionException>(() => new ProviderPlugin().Execute(Services(host.Url, Messages.Delete, inputs)));
        AssertCarriesCorrelation(ex);
    }

    [Fact]
    public void Execute_HostError_PassesThroughOnce()
    {
        using var host = new StubHost(404, $$"""{"error":"NotFound","message":"No order ord_9.","correlationId":"{{Correlation}}"}""");
        var inputs = new ParameterCollection { ["Target"] = new EntityReference("ext_order", Guid.NewGuid()) };
        var ex = Assert.Throws<InvalidPluginExecutionException>(() => new ProviderPlugin().Execute(Services(host.Url, Messages.Delete, inputs)));
        Assert.Equal($"NotFound: No order ord_9. (correlationId {Correlation})", ex.Message);
        Assert.Equal(ErrorRaiser.ObjectDoesNotExist, ex.ErrorCode);
    }

    [Fact]
    public void Execute_CreateWithPlatformAssignedId_CallsHost()
    {
        // Live: Dataverse sets Target.Id on every virtual table Create, even when the caller sent no id.
        var newId = Guid.NewGuid();
        using var host = new StubHost(200, $$"""{"correlationId":"{{Correlation}}","id":"{{newId}}"}""");
        var services = Services(host.Url, Messages.Create, new ParameterCollection { ["Target"] = new Entity("ext_order", Guid.NewGuid()) { ["ext_customerref"] = "CUST-01" } });
        new ProviderPlugin().Execute(services);
        Assert.Equal(1, host.Calls);
        var context = (IPluginExecutionContext)services.GetService(typeof(IPluginExecutionContext))!;
        Assert.Equal(newId, context.OutputParameters["id"]);
    }

    private static readonly Guid Owner = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid TestMe = Guid.Parse("22222222-0000-0000-0000-000000000002");
    private static readonly Guid TestMeOid = Guid.Parse("b0b00000-0000-0000-0000-000000000002");

    private static IPluginExecutionContext Caller(Guid authenticated, Guid initiating) =>
        Fake<IPluginExecutionContext6>.WithProperties(new Dictionary<string, object>
        {
            ["AuthenticatedUserId"] = authenticated,
            ["InitiatingUserId"] = initiating,
            ["UserId"] = initiating,
            ["InitiatingUserAzureActiveDirectoryObjectId"] = TestMeOid,
        });

    [Fact]
    public void UserObjectId_NotImpersonated_SendsInitiatingUsersObjectId() =>
        Assert.Equal(TestMeOid.ToString(), ProviderPlugin.UserObjectId(Caller(authenticated: TestMe, initiating: TestMe)));

    [Fact]
    public void UserObjectId_Impersonated_SendsNone()
    {
        // Live (phase 3 findings 10): under CallerObjectId the platform reports the impersonated user as the
        // initiating user; only AuthenticatedUserId names who actually called.
        Assert.Null(ProviderPlugin.UserObjectId(Caller(authenticated: Owner, initiating: TestMe)));
    }

    [Fact]
    public void UserObjectId_AuthenticatedUserUnknown_SendsNone() =>
        Assert.Null(ProviderPlugin.UserObjectId(Caller(authenticated: Guid.Empty, initiating: TestMe)));

    [Fact]
    public void Execute_CreateWithClientSuppliedId_RefusedWithoutCallingHost()
    {
        using var host = new StubHost(200, "{}");
        var id = Guid.NewGuid();
        var target = new Entity("ext_order", id) { ["ext_orderid"] = id, ["ext_customerref"] = "CUST-01" };
        var ex = Assert.Throws<InvalidPluginExecutionException>(() => new ProviderPlugin().Execute(Services(host.Url, Messages.Create, new ParameterCollection { ["Target"] = target })));
        Assert.StartsWith(nameof(UnsupportedQueryException), ex.Message);
        Assert.Contains("id", ex.Message);
        AssertCarriesCorrelation(ex);
        Assert.Equal(0, host.Calls);
    }
}
