namespace BrokerForge.Abstractions;

/// <summary>
/// The host's configuration or a descriptor cannot serve this request: a missing config value or secret
/// name, a mode mismatch, an unregistered adapter. Reported as ConfigurationError with its message, so
/// messages name the setting, never a secret value. Other unexpected exceptions report no message.
/// </summary>
public sealed class ConfigurationException(string message) : Exception(message);
