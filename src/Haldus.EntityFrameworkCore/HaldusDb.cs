using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace Haldus;

/// <summary>Üretilen model ve uç kodunun, veritabanına özgü ayrıntılar için kullandığı yardımcılar.</summary>
public static class HaldusDb
{
    /// <summary>PostgreSQL'in "benzersizlik kısıtı çiğnendi" hata kodu.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Kaydetme, adı verilen benzersiz indeks yüzünden mi başarısız oldu. Aynı anda yazan iki istekten ikincisi
    /// ön kontrolü geçse de buraya düşer; başka bir kısıtın hatası ise bu değildir ve yutulmamalıdır.
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException exception, string indexName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrEmpty(indexName);

        return exception.InnerException is DbException { SqlState: UniqueViolation } failure
            && (Equals(failure.Data["ConstraintName"], indexName) || failure.Message.Contains(indexName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Metadata çantasını jsonb kolonunda saklar. Çanta metne çevrilerek yazılır; böylece sağlayıcının dinamik JSON
    /// desteğinin açılması gerekmez. Karşılaştırıcı içeriğe bakar ki yerinde değiştirilen çanta da kaydedilsin.
    /// </summary>
    public static PropertyBuilder<Dictionary<string, string>> ConfigureMetadata(PropertyBuilder<Dictionary<string, string>> property)
    {
        ArgumentNullException.ThrowIfNull(property);

        return property
            .HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, (JsonSerializerOptions?)null) ?? new Dictionary<string, string>(),
                new ValueComparer<Dictionary<string, string>>(
                    (left, right) => SameEntries(left, right),
                    value => value.Count,
                    value => new Dictionary<string, string>(value, StringComparer.Ordinal)))
            .HasColumnType("jsonb");
    }

    /// <summary>
    /// Çevirileri (dil → alan → metin) jsonb kolonunda saklar; metadata çantasıyla aynı yoldan, metne çevirerek.
    /// </summary>
    public static PropertyBuilder<Dictionary<string, Dictionary<string, string>>> ConfigureTranslations(
        PropertyBuilder<Dictionary<string, Dictionary<string, string>>> property)
    {
        ArgumentNullException.ThrowIfNull(property);

        return property
            .HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json, (JsonSerializerOptions?)null)
                    ?? new Dictionary<string, Dictionary<string, string>>(),
                new ValueComparer<Dictionary<string, Dictionary<string, string>>>(
                    (left, right) => SameTranslations(left, right),
                    value => value.Count,
                    value => HaldusTranslations.Copy(value)))
            .HasColumnType("jsonb");
    }

    /// <summary>
    /// Önekli anahtarı, değeri verilmeden eklenen kayıtlar için de üretir. Üretilen uçlar anahtarı kendileri atar;
    /// bu, kaydı uygulamanın kendi kodunun eklediği durumlar (ör. salt-okunur bir kaynak) içindir.
    /// </summary>
    public static PropertyBuilder<string> ConfigurePrefixedKey(PropertyBuilder<string> property, string prefix)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentException.ThrowIfNullOrEmpty(prefix);

        return property
            .HasSentinel(string.Empty)
            .ValueGeneratedOnAdd()
            .HasValueGenerator((_, _) => new PrefixedKeyGenerator(prefix));
    }

    /// <summary>
    /// ULID anahtarı: uçlar anahtarı kendileri atar; uygulamanın kendi eklediği, anahtarı boş kayıtlar da aynı biçimi alır.
    /// </summary>
    public static PropertyBuilder<string> ConfigureUlidKey(PropertyBuilder<string> property)
    {
        ArgumentNullException.ThrowIfNull(property);

        return property
            .HasMaxLength(26)
            .HasSentinel(string.Empty)
            .ValueGeneratedOnAdd()
            .HasValueGenerator((_, _) => new UlidKeyGenerator());
    }

    private sealed class UlidKeyGenerator : ValueGenerator<string>
    {
        public override bool GeneratesTemporaryValues => false;

        public override string Next(EntityEntry entry) => HaldusKeys.NewUlid();
    }

    private sealed class PrefixedKeyGenerator(string prefix) : ValueGenerator<string>
    {
        public override bool GeneratesTemporaryValues => false;

        public override string Next(EntityEntry entry) => HaldusKeys.NewPrefixed(prefix);
    }

    private static bool SameTranslations(
        Dictionary<string, Dictionary<string, string>>? left,
        Dictionary<string, Dictionary<string, string>>? right) =>
        ReferenceEquals(left, right)
        || (left is not null
            && right is not null
            && left.Count == right.Count
            && left.All(entry => right.TryGetValue(entry.Key, out var texts) && SameEntries(entry.Value, texts)));

    private static bool SameEntries(Dictionary<string, string>? left, Dictionary<string, string>? right) =>
        ReferenceEquals(left, right)
        || (left is not null
            && right is not null
            && left.Count == right.Count
            && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value));
}
