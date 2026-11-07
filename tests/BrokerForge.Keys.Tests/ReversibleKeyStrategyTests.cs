using System.Text;
using BrokerForge.Keys;
using FsCheck;
using FsCheck.Xunit;

namespace BrokerForge.Keys.Tests;

public class ReversibleKeyStrategyTests
{
    [Property]
    public bool Int32_RoundTrips(int value)
    {
        var strategy = new ReversibleKeyStrategy(KeyKind.Int32);
        return strategy.Decode(strategy.Encode(ExternalKey.FromInt32(value))) == ExternalKey.FromInt32(value);
    }

    [Property]
    public bool Int64_RoundTrips(long value)
    {
        var strategy = new ReversibleKeyStrategy(KeyKind.Int64);
        return strategy.Decode(strategy.Encode(ExternalKey.FromInt64(value))) == ExternalKey.FromInt64(value);
    }

    [Property]
    public bool String_UpTo14Utf8Bytes_RoundTrips(NonNull<string> input)
    {
        // Take the longest valid prefix that fits; FsCheck strings can hold lone surrogates.
        var value = new string(input.Get.Where(c => !char.IsSurrogate(c)).ToArray());
        while (Encoding.UTF8.GetByteCount(value) > 14) value = value[..^1];
        var strategy = new ReversibleKeyStrategy(KeyKind.String);
        return strategy.Decode(strategy.Encode(ExternalKey.FromString(value))) == ExternalKey.FromString(value);
    }

    [Fact]
    public void String_Empty_RoundTrips()
    {
        var strategy = new ReversibleKeyStrategy(KeyKind.String);
        Assert.Equal(ExternalKey.FromString(""), strategy.Decode(strategy.Encode(ExternalKey.FromString(""))));
    }

    [Property]
    public Property String_Over14Bytes_ThrowsKeyTooLong(NonNull<string> input)
    {
        var value = new string(input.Get.Where(c => !char.IsSurrogate(c)).ToArray()) + "ord_0000000000001";
        var strategy = new ReversibleKeyStrategy(KeyKind.String);
        return Prop.Throws<KeyTooLongException, Guid>(new Lazy<Guid>(() => strategy.Encode(ExternalKey.FromString(value))));
    }

    [Property]
    public Property Decode_RandomGuid_ThrowsInvalidKey(byte[] raw, PositiveInt pick)
    {
        var kind = (KeyKind)(pick.Get % 3); // Int32, Int64 or String
        var bytes = new byte[16];
        Array.Copy(raw, bytes, Math.Min(raw.Length, 16));
        var strategy = new ReversibleKeyStrategy(kind);
        var expectedType = kind switch { KeyKind.Int32 => 1, KeyKind.Int64 => 2, _ => 3 };
        // Either the type byte is wrong, or it is right and the padding is forced non-zero.
        if (bytes[0] == expectedType)
        {
            if (kind == KeyKind.String && bytes[1] == 14) bytes[1] = 13; // a 14-byte string has no padding byte
            bytes[15] = (byte)(bytes[15] | 0x80);
        }
        return Prop.Throws<InvalidKeyException, ExternalKey>(new Lazy<ExternalKey>(() => strategy.Decode(new Guid(bytes))));
    }

    [Fact]
    public void Decode_Int32OutOfRange_ThrowsInvalidKey()
    {
        var int64 = new ReversibleKeyStrategy(KeyKind.Int64).Encode(ExternalKey.FromInt64(long.MaxValue));
        var bytes = int64.ToByteArray();
        bytes[0] = 0x01;
        Assert.Throws<InvalidKeyException>(() => new ReversibleKeyStrategy(KeyKind.Int32).Decode(new Guid(bytes)));
    }

    [Fact]
    public void Decode_InvalidUtf8_ThrowsInvalidKey()
    {
        var bytes = new byte[16];
        bytes[0] = 0x03; bytes[1] = 2; bytes[2] = 0xC3; bytes[3] = 0x28;
        Assert.Throws<InvalidKeyException>(() => new ReversibleKeyStrategy(KeyKind.String).Decode(new Guid(bytes)));
    }

    [Fact]
    public void Decode_WrongKind_ThrowsInvalidKey()
    {
        var id = new ReversibleKeyStrategy(KeyKind.String).Encode(ExternalKey.FromString("ord_0001"));
        Assert.Throws<InvalidKeyException>(() => new ReversibleKeyStrategy(KeyKind.Int32).Decode(id));
    }

    [Property]
    public bool Encode_NeverProducesGuidEmpty(int a, long b)
    {
        return new ReversibleKeyStrategy(KeyKind.Int32).Encode(ExternalKey.FromInt32(a)) != Guid.Empty
            && new ReversibleKeyStrategy(KeyKind.Int64).Encode(ExternalKey.FromInt64(b)) != Guid.Empty
            && new ReversibleKeyStrategy(KeyKind.String).Encode(ExternalKey.FromString("")) != Guid.Empty;
    }

    [Fact]
    public void Layout_IsFixed()
    {
        var id = new ReversibleKeyStrategy(KeyKind.String).Encode(ExternalKey.FromString("ab"));
        Assert.Equal(new byte[] { 0x03, 2, (byte)'a', (byte)'b', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, id.ToByteArray());
        var n = new ReversibleKeyStrategy(KeyKind.Int32).Encode(ExternalKey.FromInt32(-1));
        Assert.Equal(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0 }, n.ToByteArray());
    }

    [Property]
    public bool Native_RoundTrips(Guid value)
    {
        var strategy = new NativeGuidKeyStrategy();
        return value == Guid.Empty || strategy.Decode(strategy.Encode(ExternalKey.FromGuid(value))) == ExternalKey.FromGuid(value);
    }

    [Fact]
    public void Native_EmptyGuid_ThrowsInvalidKey()
    {
        Assert.Throws<InvalidKeyException>(() => new NativeGuidKeyStrategy().Decode(Guid.Empty));
    }

    [Fact]
    public void FromJson_NumberAndString()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"n": 42, "s": "ord_0001"}""");
        Assert.Equal(ExternalKey.FromInt32(42), ExternalKey.FromJson(doc.RootElement.GetProperty("n"), KeyKind.Int32));
        Assert.Equal(ExternalKey.FromString("ord_0001"), ExternalKey.FromJson(doc.RootElement.GetProperty("s"), KeyKind.String));
    }
}
