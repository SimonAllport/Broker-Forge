using BrokerForge.Abstractions;
using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Rendering;
using BrokerForge.Keys;

namespace BrokerForge.Host.Execution;

/// <summary>Everything resolved for one canonical request. The router builds it once per request.</summary>
public sealed record OperationContext(
    ServiceInstance Instance,
    ObjectMap Map,
    OperationDescriptor Descriptor,
    IKeyStrategy Keys,
    IAuthenticatedChannel Channel,
    BindingContext Binding);

/// <summary>Everything resolved for one unbound Custom API call (HLD D-18). There is no object map or key strategy.</summary>
public sealed record CustomApiContext(
    ServiceInstance Instance,
    OperationDescriptor Descriptor,
    IAuthenticatedChannel Channel,
    BindingContext Binding);
