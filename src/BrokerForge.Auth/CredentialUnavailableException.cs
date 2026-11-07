namespace BrokerForge.Auth;

/// <summary>
/// A service credential could not be obtained: a missing secret reference, an unreadable secret or a
/// failed token request. The message names the reference or scope, never a value. The host reports
/// it as a configuration failure; it is never retried as another mode.
/// </summary>
public sealed class CredentialUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
