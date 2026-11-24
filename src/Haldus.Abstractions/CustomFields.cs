using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haldus;

/// <summary>
/// Kayıt, alanlarını uygulamanın değil kiracının tanımladığı bir değer çantası taşır (<c>.CustomFields()</c>).
/// Değerler tanımlara göre doğrulanır; tanımı olmayan anahtar yazılamaz.
/// </summary>
public interface IHasCustomFields
{
    Dictionary<string, JsonElement> CustomFields { get; set; }
}

/// <summary>Özel bir alanın değerinin türü.</summary>
public enum CustomFieldType
{
    /// <summary>Metin (en çok 2000 karakter).</summary>
    Text,

    Number,

    Boolean,

    /// <summary>Gün: <c>2026-06-01</c> biçiminde metin.</summary>
    Date,

    /// <summary>Tanımdaki seçeneklerden biri.</summary>
    Choice,
}

/// <summary>Bir kaynağın özel alanlarından birinin tanımı.</summary>
/// <param name="Key">Değer çantasındaki anahtar.</param>
/// <param name="Label">Alanın kullanıcıya gösterilen adı.</param>
/// <param name="Required">Kayıt bu alan olmadan yazılamaz mı.</param>
/// <param name="Options">Tür <see cref="CustomFieldType.Choice"/> ise kabul edilen değerler.</param>
public sealed record CustomFieldDefinition(
    string Key,
    string Label,
    [property: JsonConverter(typeof(JsonStringEnumConverter<CustomFieldType>))] CustomFieldType Type,
    bool Required,
    IReadOnlyList<string> Options);

/// <summary>Özel bir alanı tanımlayan veya değiştiren isteğin gövdesi; anahtar adrestedir.</summary>
public sealed record CustomFieldDefinitionRequest
{
    [Required]
    [MaxLength(200)]
    public string Label { get; init; } = default!;

    [JsonConverter(typeof(JsonStringEnumConverter<CustomFieldType>))]
    public CustomFieldType Type { get; init; }

    public bool Required { get; init; }

    /// <summary>Tür <see cref="CustomFieldType.Choice"/> ise zorunludur: en çok 100 seçenek, her biri en çok 200 karakter.</summary>
    public IReadOnlyList<string>? Options { get; init; }
}