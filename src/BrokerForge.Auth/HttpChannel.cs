using BrokerForge.Abstractions;

namespace BrokerForge.Auth;

/// <summary>Sends through a shared HttpClient after attaching this channel's credential to each request.</summary>
internal sealed class HttpChannel(HttpClient http, ServiceInstance instance, Action<HttpRequestMessage> attach, params string[] credentialValues) : IAuthenticatedChannel
{
    public IReadOnlyCollection<string> CredentialValues { get; } = credentialValues;

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        attach(request);
        // A design instance's requests go through the design egress rule (DesignEgress).
        DesignEgress.Mark(request, instance);
        return http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }
}
