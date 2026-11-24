using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Haldus;

/// <summary>
/// Değişiklik kümesinin içeriğini kaydın kısmi güncelleme isteğine çevirir. Küme, isteğin JSON'u olarak saklanır;
/// böylece "gönderilmedi" ile "null gönderildi" ayrımı uygulama anına kadar korunur.
/// </summary>
public static class HaldusChangeSetRequests
{
    /// <summary>Bir kümenin taşıyabileceği en büyük içerik (karakter).</summary>
    public const int MaxLength = 1_000_000;

    /// <summary>
    /// Gönderilen değişiklikleri doğrular ve kümeye ekler: aynı alan yeniden gönderildiyse sonuncusu geçerlidir.
    /// Değişiklik kısmi güncelleme isteğiyle aynı kurallardan geçer; geçmezse küme değişmez.
    /// </summary>
    public static ProblemHttpResult? Stage<TRequest>(HaldusChangeSet set, JsonElement changes, HttpContext httpContext, IHaldusMessages messages)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(messages);

        if (changes.ValueKind != JsonValueKind.Object)
        {
            return HaldusProblems.ChangeSetInvalid(messages);
        }

        if (Parse<TRequest>(changes.GetRawText(), httpContext, messages, out _) is { } invalid)
        {
            return invalid;
        }

        var merged = JsonNode.Parse(set.Changes)!.AsObject();

        foreach (var property in changes.EnumerateObject())
        {
            // Alan adları büyük-küçük harf ayırmadan okunur; aynı alanın iki yazımı kümede birlikte duramaz.
            foreach (var existing in merged.Where(entry => string.Equals(entry.Key, property.Name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                merged.Remove(existing.Key);
            }

            merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        var text = merged.ToJsonString();

        if (text.Length > MaxLength)
        {
            return HaldusProblems.ChangeSetInvalid(messages);
        }

        set.Changes = text;
        return null;
    }

    /// <summary>
    /// Kümenin biriken içeriğini kısmi güncelleme isteği olarak okur ve doğrular. Küme hazırlanırken geçerli olan
    /// içerik uygulanırken de geçerli olmalıdır.
    /// </summary>
    public static ProblemHttpResult? Read<TRequest>(HaldusChangeSet set, HttpContext httpContext, IHaldusMessages messages, out TRequest request)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(messages);

        return Parse(set.Changes, httpContext, messages, out request);
    }

    private static ProblemHttpResult? Parse<TRequest>(string json, HttpContext httpContext, IHaldusMessages messages, out TRequest request)
        where TRequest : class
    {
        // İstek gövdeleriyle aynı ayarlar kullanılır; küme, o gövdelerin birikmiş halidir.
        var options = httpContext.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;

        try
        {
            request = JsonSerializer.Deserialize<TRequest>(json, options)!;
        }
        catch (JsonException)
        {
            request = null!;
            return HaldusProblems.ChangeSetInvalid(messages);
        }

        return request is null ? HaldusProblems.ChangeSetInvalid(messages) : HaldusBatch.Validate(request, messages);
    }
}
