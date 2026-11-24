using System.Text.Json.Serialization;

namespace Haldus;

/// <summary>Bir kaydın hangi aralıkla yinelendiği.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RecurrenceFrequency>))]
public enum RecurrenceFrequency
{
    /// <summary>Yinelenmez; tek bir kez olur.</summary>
    None,
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

/// <summary>
/// Yinelenen kayıt (<c>.Recurring()</c>): haftalık toplantı, aylık fatura dönemi. Kayıt bir kez saklanır; tek tek
/// oluşları saklanmaz, kuraldan hesaplanır. Tek bir oluş ertelenebilir veya iptal edilebilir; bunlar kuralın
/// istisnalarıdır ve ayrı saklanır.
/// </summary>
public interface IRecurring
{
    /// <summary>İlk oluşun zamanı; sonrakiler buna göre hesaplanır.</summary>
    DateTimeOffset StartsAt { get; set; }

    RecurrenceFrequency RecurrenceFrequency { get; set; }

    /// <summary>Kaç aralıkta bir (iki haftada bir için 2); boşsa 1.</summary>
    int? RecurrenceInterval { get; set; }

    /// <summary>Bu zamandan sonra oluş yoktur; boşsa seri açık uçludur.</summary>
    DateTimeOffset? RecurrenceUntil { get; set; }

    /// <summary>Serinin en çok kaç oluşu olduğu; boşsa sınırsızdır.</summary>
    int? RecurrenceCount { get; set; }
}

/// <summary>Serinin tek bir oluşu.</summary>
/// <param name="OriginalStart">Kuralın verdiği zaman; oluşun adresteki kimliğidir.</param>
/// <param name="Start">Oluşun gerçek zamanı; ertelendiyse <paramref name="OriginalStart"/> değerinden farklıdır.</param>
/// <param name="Cancelled">Bu oluş iptal edildi mi.</param>
public sealed record Occurrence(DateTimeOffset OriginalStart, DateTimeOffset Start, bool Cancelled);

/// <summary>Tek bir oluşun kuraldan sapması (<c>PUT /{id}/occurrences/{start}</c>).</summary>
public sealed record OccurrenceChange
{
    /// <summary>Oluşun yeni zamanı; boşsa zamanı değişmez.</summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>Oluş iptal mi edildi.</summary>
    public bool Cancelled { get; init; }
}
