using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Haldus;

/// <summary>
/// Üretilen istek DTO'larındaki DataAnnotations kurallarını çalıştırır. .NET'in yerleşik validasyon
/// generator'ı başka bir generator'ın ürettiği tipleri göremediği için kurallar burada işletilir.
/// </summary>
public sealed class HaldusValidationFilter<TRequest>(IHaldusMessages messages) : IEndpointFilter
    where TRequest : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var argument in context.Arguments)
        {
            if (argument is not TRequest request)
            {
                continue;
            }

            var errors = HaldusValidator.Validate(request, messages);
            if (errors.Count > 0)
            {
                return TypedResults.ValidationProblem(errors, title: messages.Format(HaldusMessageKeys.ValidationTitle));
            }
        }

        return await next(context);
    }
}

/// <summary>Üretilen kodun, endpoint argümanı olmayan bir nesneyi (ör. sorgu isteğini) doğrulaması için.</summary>
public static class HaldusValidation
{
    /// <summary>Nesne geçerliyse <c>null</c>, değilse yerelleştirilmiş doğrulama hatasını döner.</summary>
    public static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? Validate(object instance, IHaldusMessages messages)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(messages);

        var errors = HaldusValidator.Validate(instance, messages);

        return errors.Count == 0
            ? null
            : TypedResults.ValidationProblem(errors, title: messages.Format(HaldusMessageKeys.ValidationTitle));
    }
}

internal static class HaldusValidator
{
    private static readonly ConcurrentDictionary<Type, PropertyRules[]> Cache = new();

    /// <summary>İç içe değer nesnelerinde en fazla kaç düzey inileceği; kendine referans veren tiplere karşı.</summary>
    private const int MaxDepth = 8;

    public static Dictionary<string, string[]> Validate(object instance, IHaldusMessages messages)
    {
        var errors = new Dictionary<string, string[]>();
        Validate(instance, messages, prefix: string.Empty, depth: 0, errors);
        return errors;
    }

    private static void Validate(
        object instance,
        IHaldusMessages messages,
        string prefix,
        int depth,
        Dictionary<string, string[]> errors)
    {
        var patch = instance as IHaldusPatch;

        foreach (var rules in Cache.GetOrAdd(instance.GetType(), BuildRules))
        {
            // Kısmi güncellemede gönderilmeyen alanın kuralı çalışmaz.
            if (patch is not null && !patch.IsSet(rules.Property.Name))
            {
                continue;
            }

            var value = rules.Property.GetValue(instance);
            var context = new ValidationContext(instance) { MemberName = rules.Property.Name };
            List<string>? propertyErrors = null;

            foreach (var attribute in rules.Attributes)
            {
                if (attribute.GetValidationResult(value, context) is not null)
                {
                    (propertyErrors ??= []).Add(Describe(attribute, rules.Property.Name, messages));
                }
            }

            if (propertyErrors is not null)
            {
                errors[prefix + rules.JsonName] = [.. propertyErrors];
            }

            if (value is null || depth >= MaxDepth)
            {
                continue;
            }

            if (rules.IsNested)
            {
                // Gömülü değer nesnesinin kendi kuralları da çalışır; hataları "price.amount" gibi adreslenir.
                Validate(value, messages, $"{prefix}{rules.JsonName}.", depth + 1, errors);
            }
            else if (rules.IsNestedCollection)
            {
                // Gömülü satırların her biri doğrulanır; hataları "items[2].quantity" gibi adreslenir.
                var index = 0;
                foreach (var element in (System.Collections.IEnumerable)value)
                {
                    if (element is not null)
                    {
                        Validate(element, messages, $"{prefix}{rules.JsonName}[{index}].", depth + 1, errors);
                    }

                    index++;
                }
            }
        }
    }

    private static PropertyRules[] BuildRules(Type type)
    {
        // Konumsal bir record'da attribute'lar özelliğe değil constructor parametresine yazılır;
        // onlar da kural sayılır.
        var parameters = type.GetConstructors() is [var constructor]
            ? constructor.GetParameters().ToDictionary(parameter => parameter.Name ?? string.Empty, StringComparer.Ordinal)
            : [];

        return
        [
            .. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod is not null)
                .Select(property => new PropertyRules(
                    property,
                    JsonNamingPolicy.CamelCase.ConvertName(property.Name),
                    [
                        .. property.GetCustomAttributes<ValidationAttribute>(inherit: true),
                        .. parameters.TryGetValue(property.Name, out var parameter)
                            ? parameter.GetCustomAttributes<ValidationAttribute>()
                            : [],
                    ],
                    IsNested: IsValueObject(property.PropertyType),
                    IsNestedCollection: ElementType(property.PropertyType) is { } element && IsValueObject(element)))
                .Where(rules => rules.Attributes.Length > 0 || rules.IsNested || rules.IsNestedCollection),
        ];
    }

    /// <summary>Tip bir koleksiyonsa eleman tipini verir.</summary>
    private static Type? ElementType(Type type) =>
        type == typeof(string)
            ? null
            : type.IsArray
                ? type.GetElementType()
                : type.GetInterfaces()
                    .Append(type)
                    .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    ?.GetGenericArguments()[0];

    /// <summary>Kendi alanları olan, kullanıcı tanımlı bir tip mi (skaler, koleksiyon veya çerçeve tipi değil).</summary>
    private static bool IsValueObject(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        return !type.IsPrimitive
            && !type.IsEnum
            && type != typeof(string)
            && type.Namespace?.StartsWith("System", StringComparison.Ordinal) != true
            && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }

    private static string Describe(ValidationAttribute attribute, string name, IHaldusMessages messages)
    {
        // Kullanıcı attribute'a kendi mesajını verdiyse ona dokunulmaz.
        if (!string.IsNullOrEmpty(attribute.ErrorMessage) || attribute.ErrorMessageResourceType is not null)
        {
            return attribute.FormatErrorMessage(name);
        }

        return attribute switch
        {
            RequiredAttribute or NotNullWhenSetAttribute or HasValueAttribute => messages.Format(HaldusMessageKeys.ValidationRequired, name),
            MaxLengthAttribute maxLength => messages.Format(HaldusMessageKeys.ValidationMaxLength, name, maxLength.Length),
            MinLengthAttribute minLength => messages.Format(HaldusMessageKeys.ValidationMinLength, name, minLength.Length),
            StringLengthAttribute { MinimumLength: > 0 } stringLength => messages.Format(
                HaldusMessageKeys.ValidationStringLength, name, stringLength.MinimumLength, stringLength.MaximumLength),
            StringLengthAttribute stringLength => messages.Format(
                HaldusMessageKeys.ValidationMaxLength, name, stringLength.MaximumLength),
            RangeAttribute range => messages.Format(HaldusMessageKeys.ValidationRange, name, range.Minimum, range.Maximum),
            MetadataAttribute => HaldusProblems.MetadataMessage(messages, name),
            RecurrenceDaysAttribute => messages.Format(HaldusMessageKeys.RecurrenceByDayInvalid, name),
            TimeZoneIdAttribute => messages.Format(HaldusMessageKeys.RecurrenceTimeZoneInvalid, name),
            EmailAddressAttribute or UrlAttribute or PhoneAttribute or RegularExpressionAttribute =>
                messages.Format(HaldusMessageKeys.ValidationInvalidFormat, name),
            _ => attribute.FormatErrorMessage(name),
        };
    }

    private sealed record PropertyRules(
        PropertyInfo Property,
        string JsonName,
        ValidationAttribute[] Attributes,
        bool IsNested,
        bool IsNestedCollection);
}
