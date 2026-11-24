using System.Text.Json.Serialization;

namespace Haldus;

// Arama gövdesindeki alan koşulları. Bir koşulda birden çok operatör verilirse hepsi VE ile birleşir.
// Bilinmeyen bir operatör adı sessizce yok sayılmaz, istek reddedilir: aksi halde yanlış yazılmış bir
// koşul süzülmemiş sonuç döndürürdü.

/// <summary>Metin alanı koşulu.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StringCondition
{
    public string? Eq { get; init; }

    public string? Ne { get; init; }

    public string? Contains { get; init; }

    public string? StartsWith { get; init; }

    public string? EndsWith { get; init; }

    public IReadOnlyList<string>? In { get; init; }

    public bool? IsNull { get; init; }

    /// <summary>Karşılaştırmalar büyük/küçük harf ayrımı yapmadan yapılsın mı.</summary>
    public bool IgnoreCase { get; init; }
}

/// <summary>Yalnızca eşitlikle karşılaştırılabilen alanların (Guid, bool, enum) koşulu.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EqualityCondition<T>
    where T : struct
{
    public T? Eq { get; init; }

    public T? Ne { get; init; }

    public IReadOnlyList<T>? In { get; init; }

    public bool? IsNull { get; init; }
}

/// <summary>Sıralanabilir alanların (sayı, tarih) koşulu.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RangeCondition<T>
    where T : struct
{
    public T? Eq { get; init; }

    public T? Ne { get; init; }

    public T? Gt { get; init; }

    public T? Gte { get; init; }

    public T? Lt { get; init; }

    public T? Lte { get; init; }

    public IReadOnlyList<T>? In { get; init; }

    public bool? IsNull { get; init; }
}

/// <summary>
/// Bir arama isteğinin ne kadar büyüyebileceğini sınırlar. Arama ucu istemciye geniş bir sorgu yüzeyi
/// açar; sınırsız iç içe koşul veya çok uzun listeler pahalı sorgulara dönüşebilir.
/// </summary>
public sealed class SearchBudget
{
    public const int MaxDepth = 5;
    public const int MaxConditions = 50;
    public const int MaxListValues = 100;
    public const int MaxSortFields = 8;

    private int _depth;
    private int _conditions;

    public bool Exceeded { get; private set; }

    /// <summary>Bir iç içe düzeye girer; derinlik sınırı aşıldıysa <c>false</c> döner.</summary>
    public bool TryEnter()
    {
        if (!Exceeded && ++_depth > MaxDepth)
        {
            Exceeded = true;
        }

        return !Exceeded;
    }

    public void Exit() => _depth--;

    public void CountConditions(int count)
    {
        _conditions += count;
        Exceeded |= _conditions > MaxConditions;
    }

    public void CountListValues(int count) => Exceeded |= count > MaxListValues;

    public void CountSortFields(int count) => Exceeded |= count > MaxSortFields;
}
