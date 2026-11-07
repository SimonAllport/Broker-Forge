using System.Buffers.Binary;
using System.Text;

namespace BrokerForge.Keys;

/// <summary>
/// Packs a short key into the GUID's 16 bytes (HLD 8). The layout is fixed, because rows linked
/// from native tables store these GUIDs:
///   byte 0      type: 0x01 Int32, 0x02 Int64, 0x03 String
///   Int32/Int64 bytes 1-8: the value as little-endian int64
///   String      byte 1: UTF-8 length (0-14), bytes 2..: the UTF-8 bytes
///   all remaining bytes are zero, and decode checks that they are.
/// </summary>
public sealed class ReversibleKeyStrategy : IKeyStrategy
{
    public const int MaxStringBytes = 14;
    private const byte Int32Type = 0x01, Int64Type = 0x02, StringType = 0x03;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ReversibleKeyStrategy(KeyKind kind)
    {
        if (kind == KeyKind.Guid)
            throw new ArgumentException("Use NativeGuidKeyStrategy for GUID keys.", nameof(kind));
        Kind = kind;
    }

    public KeyKind Kind { get; }

    private byte TypeByte => Kind switch { KeyKind.Int32 => Int32Type, KeyKind.Int64 => Int64Type, _ => StringType };

    public Guid Encode(ExternalKey key)
    {
        if (key.Kind != Kind)
            throw new ArgumentException($"A {Kind} strategy cannot encode a {key.Kind} key.", nameof(key));

        var bytes = new byte[16];
        bytes[0] = TypeByte;
        if (Kind == KeyKind.String)
        {
            var utf8 = StrictUtf8.GetBytes(key.Value);
            if (utf8.Length > MaxStringBytes)
                throw new KeyTooLongException($"Key is {utf8.Length} UTF-8 bytes; reversible encoding holds at most {MaxStringBytes}.");
            bytes[1] = (byte)utf8.Length;
            utf8.CopyTo(bytes, 2);
        }
        else
        {
            var value = Kind == KeyKind.Int32 ? key.AsInt32() : key.AsInt64();
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(1, 8), value);
        }
        return new Guid(bytes);
    }

    public ExternalKey Decode(Guid id)
    {
        var bytes = id.ToByteArray();
        if (bytes[0] != TypeByte)
            throw new InvalidKeyException($"GUID {id} is not a reversible {Kind} key.");

        int used;
        ExternalKey key;
        if (Kind == KeyKind.String)
        {
            int length = bytes[1];
            if (length > MaxStringBytes)
                throw new InvalidKeyException($"GUID {id} declares a key length over {MaxStringBytes}.");
            string text;
            try { text = StrictUtf8.GetString(bytes, 2, length); }
            catch (DecoderFallbackException) { throw new InvalidKeyException($"GUID {id} does not hold valid UTF-8."); }
            key = ExternalKey.FromString(text);
            used = 2 + length;
        }
        else
        {
            var value = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(1, 8));
            if (Kind == KeyKind.Int32 && (value < int.MinValue || value > int.MaxValue))
                throw new InvalidKeyException($"GUID {id} holds a value outside the Int32 range.");
            key = Kind == KeyKind.Int32 ? ExternalKey.FromInt32((int)value) : ExternalKey.FromInt64(value);
            used = 9;
        }

        for (var i = used; i < 16; i++)
            if (bytes[i] != 0)
                throw new InvalidKeyException($"GUID {id} has non-zero padding.");
        return key;
    }
}
