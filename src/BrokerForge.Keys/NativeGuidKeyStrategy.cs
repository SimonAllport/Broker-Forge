namespace BrokerForge.Keys;

/// <summary>The source key is already a GUID and is used as is.</summary>
public sealed class NativeGuidKeyStrategy : IKeyStrategy
{
    public KeyKind Kind => KeyKind.Guid;

    public Guid Encode(ExternalKey key)
    {
        if (key.Kind != KeyKind.Guid)
            throw new ArgumentException($"Native GUID strategy cannot encode a {key.Kind} key.", nameof(key));
        return key.AsGuid();
    }

    public ExternalKey Decode(Guid id) =>
        id == Guid.Empty ? throw new InvalidKeyException("The empty GUID is not a record key.") : ExternalKey.FromGuid(id);
}
