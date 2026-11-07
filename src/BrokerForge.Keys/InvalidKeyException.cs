namespace BrokerForge.Keys;

/// <summary>A GUID this strategy did not produce. The host reports it as not found.</summary>
public sealed class InvalidKeyException(string message) : Exception(message);

/// <summary>A source key too long for the strategy, raised on encode. The host reports it as a mapping failure.</summary>
public sealed class KeyTooLongException(string message) : Exception(message);
