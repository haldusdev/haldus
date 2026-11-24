using System.ComponentModel.DataAnnotations;

namespace Haldus;

/// <summary>Kayıt, uygulamanın anlamını bilmediği serbest bir anahtar-değer çantası taşır.</summary>
public interface IHasMetadata
{
    Dictionary<string, string> Metadata { get; set; }
}

/// <summary>Metadata çantasının sınırları ve istekten kayda yazılma kuralları.</summary>
public static class HaldusMetadata
{
    public const int MaxEntries = 50;
    public const int MaxKeyLength = 40;
    public const int MaxValueLength = 500;

    /// <summary>İstekte gelen çantanın kopyası; gönderilmediyse boş bir çanta.</summary>
    public static Dictionary<string, string> Create(IReadOnlyDictionary<string, string>? requested) =>
        requested is null ? new(StringComparer.Ordinal) : new(requested, StringComparer.Ordinal);

    /// <summary>
    /// Kısmi güncelleme: gönderilen anahtarlar eklenir veya değişir, değeri <c>null</c> gönderilen silinir,
    /// gönderilmeyenler kalır. Çantanın kendisi <c>null</c> gönderildiyse tamamı boşaltılır.
    /// </summary>
    public static Dictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string?>? changes)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (changes is null)
        {
            return new(StringComparer.Ordinal);
        }

        var merged = new Dictionary<string, string>(current, StringComparer.Ordinal);

        foreach (var (key, value) in changes)
        {
            if (value is null)
            {
                merged.Remove(key);
            }
            else
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    internal static bool IsValidKey(string key) => key.Length is > 0 and <= MaxKeyLength;
}

/// <summary>
/// İstekteki metadata çantasını sınırlar: en çok <see cref="HaldusMetadata.MaxEntries"/> anahtar, anahtar en çok
/// <see cref="HaldusMetadata.MaxKeyLength"/>, değer en çok <see cref="HaldusMetadata.MaxValueLength"/> karakter.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MetadataAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value switch
    {
        null => true,
        // Değerin null olabilmesi yalnızca bir açıklamadır; çalışma zamanında iki çanta da aynı tiptir.
        IReadOnlyDictionary<string, string?> entries => entries.Count <= HaldusMetadata.MaxEntries && entries.All(IsValid),
        _ => false,
    };

    private static bool IsValid(KeyValuePair<string, string?> entry) =>
        HaldusMetadata.IsValidKey(entry.Key) && (entry.Value?.Length ?? 0) <= HaldusMetadata.MaxValueLength;
}
