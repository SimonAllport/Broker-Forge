using System.Net;
using System.Net.Sockets;
using BrokerForge.Abstractions;

namespace BrokerForge.Auth;

/// <summary>
/// The egress rule for connection designs (HLD D-34). A design's author chooses its base URL and OAuth URLs, so a
/// request made for a design instance may not reach loopback, link-local (such as the 169.254.169.254 metadata
/// endpoint), private or carrier-grade NAT addresses, unless its host is listed in
/// <c>Config:Designs:AllowedPrivateHosts</c>. Requests are marked with <see cref="DesignInstance"/>; the
/// <see cref="Handler"/> sends marked requests over their own connection pool, whose connect callback resolves the
/// name itself, checks every address and connects only to an address it checked, so a DNS answer cannot change between
/// the check and the connection. Unmarked requests (file instances) use the ordinary pool, unchanged.
/// </summary>
public static class DesignEgress
{
    /// <summary>The design instance a request is made for. Absent for file instances.</summary>
    public static readonly HttpRequestOptionsKey<string> DesignInstance = new("BrokerForge.DesignInstance");

    /// <summary>Marks a request as made for <paramref name="instance"/> when it comes from a connection design.</summary>
    public static void Mark(HttpRequestMessage request, ServiceInstance instance)
    {
        if (instance.EnvironmentId != null)
            request.Options.Set(DesignInstance, instance.Name);
    }

    private static readonly (IPAddress Network, int Prefix)[] PrivateRanges =
    {
        (IPAddress.Parse("0.0.0.0"), 8),
        (IPAddress.Parse("127.0.0.0"), 8),
        (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("100.64.0.0"), 10),
        (IPAddress.Parse("169.254.0.0"), 16),
        (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("::"), 128),
        (IPAddress.Parse("::1"), 128),
        (IPAddress.Parse("fe80::"), 10),
        (IPAddress.Parse("fc00::"), 7),
    };

    /// <summary>Whether a design may not reach this address. IPv4-mapped IPv6 addresses are judged as their IPv4 address.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        foreach (var (network, prefix) in PrivateRanges)
        {
            if (network.AddressFamily != address.AddressFamily)
                continue;
            var net = network.GetAddressBytes();
            var whole = prefix / 8;
            if (!bytes.AsSpan(0, whole).SequenceEqual(net.AsSpan(0, whole)))
                continue;
            var rest = prefix % 8;
            if (rest == 0 || (bytes[whole] & (0xFF << (8 - rest)) & 0xFF) == net[whole])
                return true;
        }
        return false;
    }

    /// <summary>The message a refused design request gets: it names the instance and host, never a value.</summary>
    public static string Refusal(string instance, string host) =>
        $"Service instance {instance}: host {host} resolves to a private address; list it in Config:Designs:AllowedPrivateHosts to allow.";

    /// <summary>
    /// The primary handler for an HTTP client that may carry design requests: <paramref name="create"/> builds both
    /// pools (the ordinary one and the guarded one), and <paramref name="allowedPrivateHosts"/> exempts exact host names.
    /// </summary>
    public static HttpMessageHandler Handler(Func<SocketsHttpHandler> create, IEnumerable<string> allowedPrivateHosts) =>
        new Router(create(), Guarded(create(), new HashSet<string>(allowedPrivateHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()), StringComparer.OrdinalIgnoreCase)));

    private static SocketsHttpHandler Guarded(SocketsHttpHandler handler, HashSet<string> allowed)
    {
        handler.UseProxy = false;
        handler.ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var instance = context.InitialRequestMessage.Options.TryGetValue(DesignInstance, out var name) ? name : "(design)";
            var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0)
                throw new EgressRefusedException($"Service instance {instance}: host {host} does not resolve.");
            if (!allowed.Contains(host.Trim('[', ']')) && addresses.Any(IsPrivate))
                throw new EgressRefusedException(Refusal(instance, host));
            Exception? last = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    // The checked address, not the name: nothing is resolved again.
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException ex)
                {
                    socket.Dispose();
                    last = ex;
                }
            }
            throw last!;
        };
        return handler;
    }

    /// <summary>Thrown inside the connect callback; <see cref="Router"/> turns it into a <see cref="ConfigurationException"/>.</summary>
    private sealed class EgressRefusedException(string message) : Exception(message);

    /// <summary>Sends design requests over the guarded pool and every other request over the ordinary one.</summary>
    private sealed class Router(HttpMessageHandler open, HttpMessageHandler guarded) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _open = new(open);
        private readonly HttpMessageInvoker _guarded = new(guarded);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.Options.TryGetValue(DesignInstance, out _))
                return await _open.SendAsync(request, ct);
            try
            {
                return await _guarded.SendAsync(request, ct);
            }
            catch (HttpRequestException ex) when (ex.InnerException is EgressRefusedException refused)
            {
                throw new ConfigurationException(refused.Message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _open.Dispose();
                _guarded.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
