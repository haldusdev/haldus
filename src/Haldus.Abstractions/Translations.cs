namespace Haldus;

/// <summary>
/// Kaydın bazı metin alanları başka dillere çevrilebilir. Alanın kendisi kaynak dildeki metni taşır; çeviriler
/// kayıtla birlikte, dil → alan → metin biçiminde saklanır.
/// </summary>
public interface ITranslatable
{
    Dictionary<string, Dictionary<string, string>> Translations { get; set; }
}

/// <summary>Çevirilerin yazılma ve okunma kuralları.</summary>
public static class HaldusTranslations
{
    /// <summary>Bir kaydın çevrilebileceği en çok dil sayısı.</summary>
    public const int MaxLocales = 50;

    private const int MaxLocaleLength = 35;

    /// <summary>
    /// Dil etiketini (<c>tr</c>, <c>pt-BR</c>, <c>zh-Hant-TW</c>) küçük harfe çevirir; etiket biçimine uymuyorsa
    /// <c>null</c> döner. Etiketin gerçekten var olan bir dile ait olup olmadığına bakılmaz.
    /// </summary>
    public static string? Normalize(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale) || locale.Length > MaxLocaleLength)
        {
            return null;
        }

        var parts = locale.Trim().Split('-');

        if (parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsAsciiLetter))
        {
            return null;
        }

        for (var index = 1; index < parts.Length; index++)
        {
            if (parts[index].Length is < 1 or > 8 || !parts[index].All(char.IsAsciiLetterOrDigit))
            {
                return null;
            }
        }

        return string.Join('-', parts).ToLowerInvariant();
    }

    /// <summary>
    /// Alanın istenen dildeki metni. Tam eşleşme yoksa daha genel dile düşülür (<c>pt-br</c> → <c>pt</c>); o da
    /// yoksa <c>null</c> döner ve kaynak dildeki metin kullanılır.
    /// </summary>
    /// <param name="locale"><see cref="Normalize"/> ile düzeltilmiş dil etiketi.</param>
    public static string? Pick(IReadOnlyDictionary<string, Dictionary<string, string>>? translations, string? locale, string field)
    {
        if (translations is null || translations.Count == 0)
        {
            return null;
        }

        for (var candidate = locale; !string.IsNullOrEmpty(candidate);)
        {
            if (translations.TryGetValue(candidate, out var texts) && texts.TryGetValue(field, out var text))
            {
                return text;
            }

            var cut = candidate.LastIndexOf('-');
            candidate = cut < 0 ? null : candidate.Substring(0, cut);
        }

        return null;
    }

    /// <summary>
    /// Bir dilin çevirilerini verilenlerle değiştirir ve yeni çantayı döndürür. Boş bırakılan alanın çevirisi
    /// kaldırılır; hiçbir alanı kalmayan dil çantadan çıkar.
    /// </summary>
    /// <param name="locale"><see cref="Normalize"/> ile düzeltilmiş dil etiketi.</param>
    public static Dictionary<string, Dictionary<string, string>> Set(
        IReadOnlyDictionary<string, Dictionary<string, string>> current,
        string locale,
        IEnumerable<KeyValuePair<string, string?>> texts)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrEmpty(locale);
        ArgumentNullException.ThrowIfNull(texts);

        var translations = Copy(current);
        var written = texts
            .Where(text => !string.IsNullOrEmpty(text.Value))
            .ToDictionary(text => text.Key, text => text.Value!, StringComparer.Ordinal);

        if (written.Count == 0)
        {
            translations.Remove(locale);
        }
        else
        {
            translations[locale] = written;
        }

        return translations;
    }

    /// <summary>Bir dilin çevirilerini kaldırır ve yeni çantayı döndürür.</summary>
    public static Dictionary<string, Dictionary<string, string>> Remove(
        IReadOnlyDictionary<string, Dictionary<string, string>> current,
        string locale)
    {
        ArgumentNullException.ThrowIfNull(current);

        var translations = Copy(current);
        translations.Remove(locale);
        return translations;
    }

    /// <summary>Çantanın, kaynağıyla hiçbir nesneyi paylaşmayan kopyası.</summary>
    public static Dictionary<string, Dictionary<string, string>> Copy(IReadOnlyDictionary<string, Dictionary<string, string>>? source)
    {
        var copy = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        foreach (var (locale, texts) in source ?? new Dictionary<string, Dictionary<string, string>>())
        {
            copy[locale] = new Dictionary<string, string>(texts, StringComparer.Ordinal);
        }

        return copy;
    }
}
