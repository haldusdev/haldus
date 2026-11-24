using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Haldus;

/// <summary>
/// Kiracının tanımladığı özel alanlar (<c>.CustomFields()</c>): tanımların yönetimi ve değerlerin tanımlara göre
/// doğrulanması. Üretilen uçlar yalnızca çağırır; kurallar buradadır.
/// </summary>
public static partial class HaldusCustomFields
{
    /// <summary>Bir kaynağın bir kiracıda taşıyabileceği en çok özel alan.</summary>
    public const int MaxDefinitions = 100;

    public const int MaxTextLength = 2000;

    /// <summary>Kaynağın geçerli kiracıdaki tanımları, anahtar sırasıyla.</summary>
    public static async Task<IReadOnlyList<CustomFieldDefinition>> ListAsync<TEntity>(DbContext context, CancellationToken cancellationToken)
        where TEntity : class, IHasCustomFields =>
        [.. (await HaldusCustomFieldStore.Definitions<TEntity>(context).AsNoTracking().OrderBy(definition => definition.Key).ToListAsync(cancellationToken))
            .Select(ToDefinition)];

    /// <summary>Bir alanı tanımlar veya tanımını değiştirir. Var olan değerler yeniden doğrulanmaz; bir sonraki yazmada doğrulanır.</summary>
    public static async Task<Results<Ok<CustomFieldDefinition>, ProblemHttpResult>> DefineAsync<TEntity>(
        DbContext context,
        string key,
        CustomFieldDefinitionRequest request,
        IHaldusMessages messages,
        CancellationToken cancellationToken)
        where TEntity : class, IHasCustomFields
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!KeyPattern().IsMatch(key))
        {
            return Invalid(messages, "key", HaldusMessageKeys.CustomFieldKeyInvalid);
        }

        if (!Enum.IsDefined(request.Type))
        {
            return Invalid(messages, "type", HaldusMessageKeys.CustomFieldInvalid, "type");
        }

        var options = request.Options?.Distinct(StringComparer.Ordinal).ToArray() ?? [];

        if (request.Type == CustomFieldType.Choice
            ? options.Length is 0 or > 100 || options.Any(option => option is not { Length: > 0 and <= 200 })
            : options.Length > 0)
        {
            return Invalid(messages, "options", HaldusMessageKeys.CustomFieldOptionsInvalid);
        }

        var definitions = HaldusCustomFieldStore.Definitions<TEntity>(context);
        var existing = await definitions.FirstOrDefaultAsync(definition => definition.Key == key, cancellationToken);

        if (existing is null)
        {
            if (await definitions.CountAsync(cancellationToken) >= MaxDefinitions)
            {
                return Invalid(messages, "key", HaldusMessageKeys.CustomFieldLimit, MaxDefinitions);
            }

            existing = new HaldusCustomFieldDefinition
            {
                Resource = typeof(TEntity).FullName ?? typeof(TEntity).Name,
                TenantId = HaldusCustomFieldStore.TenantOf<TEntity>(context),
                Key = key,
            };

            context.Set<HaldusCustomFieldDefinition>().Add(existing);
        }

        existing.Label = request.Label;
        existing.Type = request.Type;
        existing.Required = request.Required;
        existing.Options = options;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Aynı alanı aynı anda tanımlayan iki istekten ikincisi benzersiz indekse takılır.
            return TypedResults.Problem(
                title: messages.Format(HaldusMessageKeys.StateConflictTitle),
                detail: messages.Format(HaldusMessageKeys.StateChangedDetail),
                statusCode: StatusCodes.Status409Conflict);
        }

        return TypedResults.Ok(ToDefinition(existing));
    }

    /// <summary>Bir alanın tanımını kaldırır; kayıtlardaki değerleri bir sonraki yazmalarında düşer.</summary>
    public static async Task<bool> RemoveAsync<TEntity>(DbContext context, string key, CancellationToken cancellationToken)
        where TEntity : class, IHasCustomFields =>
        await HaldusCustomFieldStore.Definitions<TEntity>(context).Where(definition => definition.Key == key).ExecuteDeleteAsync(cancellationToken) > 0;

    /// <summary>
    /// İstekteki değerleri kaydın değerleriyle birleştirir ve tanımlara göre doğrular. Tanımı olmayan anahtar,
    /// türüne uymayan değer ve eksik zorunlu alan alan düzeyinde 400 olur. Tanımı sonradan kaldırılmış
    /// anahtarlar sonuçtan sessizce düşer.
    /// </summary>
    /// <param name="current">Kaydın şimdiki değerleri; yeni kayıtta <c>null</c>.</param>
    /// <param name="requested">İstekte gönderilen değerler; değeri <c>null</c> olan anahtar silinir.</param>
    /// <param name="merge">Gönderilmeyen anahtarlar kalsın mı (<c>PATCH</c>), yoksa çanta tamamen değişsin mi.</param>
    public static async Task<CustomFieldValues> ApplyAsync<TEntity>(
        DbContext context,
        IReadOnlyDictionary<string, JsonElement>? current,
        IReadOnlyDictionary<string, JsonElement>? requested,
        bool merge,
        IHaldusMessages messages,
        CancellationToken cancellationToken)
        where TEntity : class, IHasCustomFields
    {
        ArgumentNullException.ThrowIfNull(messages);

        var definitions = await HaldusCustomFieldStore.Definitions<TEntity>(context).AsNoTracking().ToDictionaryAsync(
            definition => definition.Key, StringComparer.Ordinal, cancellationToken);

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (merge && current is not null)
        {
            foreach (var (key, value) in current.Where(entry => definitions.ContainsKey(entry.Key)))
            {
                values[key] = value;
            }
        }

        foreach (var (key, value) in requested ?? new Dictionary<string, JsonElement>())
        {
            if (!definitions.TryGetValue(key, out var definition))
            {
                errors[$"customFields.{key}"] = [messages.Format(HaldusMessageKeys.CustomFieldUnknown)];
            }
            else if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                values.Remove(key);
            }
            else if (!Accepts(definition, value))
            {
                errors[$"customFields.{key}"] = [messages.Format(HaldusMessageKeys.CustomFieldInvalid, definition.Type.ToString().ToLowerInvariant())];
            }
            else
            {
                // Değer isteğin belgesine bağlı kalmasın diye kopyalanır.
                values[key] = value.Clone();
            }
        }

        foreach (var definition in definitions.Values.Where(definition => definition.Required && !values.ContainsKey(definition.Key)))
        {
            errors.TryAdd($"customFields.{definition.Key}", [messages.Format(HaldusMessageKeys.ValidationRequired)]);
        }

        return errors.Count == 0
            ? new CustomFieldValues(null, values)
            : new CustomFieldValues(
                TypedResults.Problem(new HttpValidationProblemDetails(errors)
                {
                    Title = messages.Format(HaldusMessageKeys.ValidationTitle),
                    Status = StatusCodes.Status400BadRequest,
                }),
                values);
    }

    private static bool Accepts(HaldusCustomFieldDefinition definition, JsonElement value) => definition.Type switch
    {
        CustomFieldType.Text => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= MaxTextLength,
        CustomFieldType.Number => value.ValueKind == JsonValueKind.Number,
        CustomFieldType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        CustomFieldType.Date => value.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        CustomFieldType.Choice => value.ValueKind == JsonValueKind.String && definition.Options.Contains(value.GetString()),
        _ => false,
    };

    private static CustomFieldDefinition ToDefinition(HaldusCustomFieldDefinition definition) =>
        new(definition.Key, definition.Label, definition.Type, definition.Required, definition.Options);

    private static ProblemHttpResult Invalid(IHaldusMessages messages, string field, string messageKey, params object?[] arguments) =>
        TypedResults.Problem(new HttpValidationProblemDetails(new Dictionary<string, string[]> { [field] = [messages.Format(messageKey, arguments)] })
        {
            Title = messages.Format(HaldusMessageKeys.ValidationTitle),
            Status = StatusCodes.Status400BadRequest,
        });

    // Anahtar JSON'da alan adı, hata yanıtında yol parçası olur: harfle başlar, yalnızca harf, rakam ve alt çizgi taşır.
    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>Doğrulanmış özel alan değerleri ya da neden reddedildikleri.</summary>
public readonly record struct CustomFieldValues(ProblemHttpResult? Rejected, Dictionary<string, JsonElement> Values);