namespace Haldus;

/// <summary>Sunucunun ürettiği anahtarın biçimi.</summary>
public enum KeyStrategy
{
    /// <summary>Zamana göre sıralı GUID (v7). Varsayılandır; indeksler sırayla dolar.</summary>
    Sequential,

    /// <summary>Rastgele GUID (v4). Kaydın oluşturulma zamanını ve sırasını sızdırmaz.</summary>
    Random,

    /// <summary>Önekli metin kimlik (<c>ord_01j9…</c>): kaydın türü kimliğinden anlaşılır, gövdesi zamana göre sıralıdır.</summary>
    Prefixed,
}

/// <summary>Üretilen kodun ve uygulamanın anahtar üretmek için kullandığı yardımcılar.</summary>
public static class HaldusKeys
{
    // Crockford base32: karakterler ASCII sırasındadır, böylece metin sırası değerin sırasıyla aynıdır.
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>Gövdenin uzunluğu: 128 bit, karakter başına 5 bit.</summary>
    public const int BodyLength = 26;

    /// <summary>
    /// <c>{önek}_{gövde}</c> biçiminde bir kimlik üretir. Gövde, zamana göre sıralı bir GUID'in (v7) 26
    /// karakterlik yazımıdır; aynı önekli kimlikler metin olarak sıralandığında oluşturulma sırasına dizilir.
    /// </summary>
    public static string NewPrefixed(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);

        Span<byte> bytes = stackalloc byte[16];
        Guid.CreateVersion7().TryWriteBytes(bytes, bigEndian: true, out _);
        var value = new UInt128(
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));

        return string.Create(prefix.Length + 1 + BodyLength, (prefix, value), static (span, state) =>
        {
            state.prefix.CopyTo(span);
            span[state.prefix.Length] = '_';

            var body = span[(state.prefix.Length + 1)..];
            var remaining = state.value;

            for (var index = body.Length - 1; index >= 0; index--)
            {
                body[index] = Alphabet[(int)(remaining & 31)];
                remaining >>= 5;
            }
        });
    }
}
