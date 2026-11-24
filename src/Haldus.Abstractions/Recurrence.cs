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

    /// <summary>
    /// Oluşların haftanın hangi günlerine düştüğü, iCalendar'ın <c>BYDAY</c> yazımıyla: <c>MO,WE,FR</c>. Aylık ve
    /// yıllık seride gün bir sıra alabilir: <c>2TU</c> ayın ikinci salısı, <c>-1FR</c> ayın son cumasıdır. Boşsa
    /// oluşlar ilk oluşun gününe düşer.
    /// </summary>
    string? RecurrenceByDay { get; set; }

    /// <summary>
    /// Serinin saat dilimi (IANA adı, <c>Europe/Istanbul</c>). Verilirse oluşlar o dilimin duvar saatini korur: yaz
    /// saatine geçilince toplantı yine 09.00'dadır. Boşsa oluşlar ilk oluşun UTC farkıyla hesaplanır.
    /// </summary>
    string? RecurrenceTimeZone { get; set; }
}

/// <summary>Bir <c>BYDAY</c> girdisi: haftanın günü ve, varsa, ay içindeki sırası (<c>2TU</c>, <c>-1FR</c>).</summary>
/// <param name="Ordinal">Ay içindeki sıra; 0 ise sıra verilmemiştir (o günlerin hepsi).</param>
public readonly record struct RecurrenceDay(int Ordinal, DayOfWeek Day);

/// <summary><c>BYDAY</c> yazımının okunması.</summary>
public static class RecurrenceDays
{
    private static readonly Dictionary<string, DayOfWeek> Codes = new(StringComparer.Ordinal)
    {
        ["MO"] = DayOfWeek.Monday,
        ["TU"] = DayOfWeek.Tuesday,
        ["WE"] = DayOfWeek.Wednesday,
        ["TH"] = DayOfWeek.Thursday,
        ["FR"] = DayOfWeek.Friday,
        ["SA"] = DayOfWeek.Saturday,
        ["SU"] = DayOfWeek.Sunday,
    };

    /// <summary>Metni girdilere çevirir. Boş metin "gün verilmemiş" demektir ve geçerlidir.</summary>
    public static bool TryParse(string? text, out IReadOnlyList<RecurrenceDay> days)
    {
        var parsed = new List<RecurrenceDay>();
        days = parsed;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (var part in text.Split(','))
        {
            var entry = part.Trim();

            if (entry.Length < 2 || !Codes.TryGetValue(entry.Substring(entry.Length - 2), out var day))
            {
                return false;
            }

            var ordinal = 0;
            var prefix = entry.Substring(0, entry.Length - 2);

            // Sıra en çok beşinci olabilir (bir ayda bir gün en çok beş kez geçer); sıfır bir sıra değildir.
            if (prefix.Length > 0
                && (!int.TryParse(prefix, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out ordinal)
                    || ordinal is 0 or < -5 or > 5))
            {
                return false;
            }

            var candidate = new RecurrenceDay(ordinal, day);

            if (!parsed.Contains(candidate))
            {
                parsed.Add(candidate);
            }
        }

        return true;
    }
}

/// <summary>
/// <c>RecurrenceByDay</c> alanını doğrular: yazımı geçerli olmalı ve sıralı günler (<c>2TU</c>) yalnızca aylık veya
/// yıllık seride kullanılmalıdır.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RecurrenceDaysAttribute : System.ComponentModel.DataAnnotations.ValidationAttribute
{
    public override bool RequiresValidationContext => true;

    protected override System.ComponentModel.DataAnnotations.ValidationResult? IsValid(
        object? value,
        System.ComponentModel.DataAnnotations.ValidationContext validationContext)
    {
        if (value is null)
        {
            return System.ComponentModel.DataAnnotations.ValidationResult.Success;
        }

        if (value is not string text || !RecurrenceDays.TryParse(text, out var days))
        {
            return new System.ComponentModel.DataAnnotations.ValidationResult(ErrorMessage);
        }

        if (days.All(day => day.Ordinal == 0))
        {
            return System.ComponentModel.DataAnnotations.ValidationResult.Success;
        }

        // Sıklık aynı istekte geliyorsa onunla birlikte doğrulanır. Kısmi güncellemede gönderilmediyse kayıttaki
        // değer burada bilinmez; o durumda sıra, aylık ve yıllık olmayan seride yok sayılır.
        var instance = validationContext?.ObjectInstance;
        var frequency = instance?.GetType().GetProperty(nameof(IRecurring.RecurrenceFrequency));

        if (instance is null || frequency is null || (instance is IHaldusPatch patch && !patch.IsSet(frequency.Name)))
        {
            return System.ComponentModel.DataAnnotations.ValidationResult.Success;
        }

        return frequency.GetValue(instance) is RecurrenceFrequency.Monthly or RecurrenceFrequency.Yearly
            ? System.ComponentModel.DataAnnotations.ValidationResult.Success
            : new System.ComponentModel.DataAnnotations.ValidationResult(ErrorMessage);
    }
}

/// <summary>Alanın, sistemin tanıdığı bir saat dilimi adı (IANA) olduğunu doğrular.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TimeZoneIdAttribute : System.ComponentModel.DataAnnotations.ValidationAttribute
{
    public override bool IsValid(object? value) => value switch
    {
        null => true,
        string id => id.Length > 0 && TimeZoneInfo.TryFindSystemTimeZoneById(id, out _),
        _ => false,
    };
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
