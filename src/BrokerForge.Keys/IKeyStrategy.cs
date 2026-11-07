namespace BrokerForge.Keys;

/// <summary>Maps source keys to Dataverse row GUIDs and back (HLD 8). Strategies are stateless.</summary>
public interface IKeyStrategy
{
    KeyKind Kind { get; }

    /// <exception cref="KeyTooLongException">The key does not fit this strategy.</exception>
    Guid Encode(ExternalKey key);

    /// <exception cref="InvalidKeyException">The GUID was not produced by this strategy.</exception>
    ExternalKey Decode(Guid id);
}
