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

internal static class HaldusValidator
{
    private static readonly ConcurrentDictionary<Type, PropertyRules[]> Cache = new();

    public static Dictionary<string, string[]> Validate(object instance, IHaldusMessages messages)
    {
        var errors = new Dictionary<string, string[]>();

        foreach (var rules in Cache.GetOrAdd(instance.GetType(), BuildRules))
        {
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
                errors[rules.JsonName] = [.. propertyErrors];
            }
        }

        return errors;
    }

    private static PropertyRules[] BuildRules(Type type) =>
    [
        .. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => new PropertyRules(
                property,
                JsonNamingPolicy.CamelCase.ConvertName(property.Name),
                [.. property.GetCustomAttributes<ValidationAttribute>(inherit: true)]))
            .Where(rules => rules.Attributes.Length > 0),
    ];

    private static string Describe(ValidationAttribute attribute, string name, IHaldusMessages messages)
    {
        // Kullanıcı attribute'a kendi mesajını verdiyse ona dokunulmaz.
        if (!string.IsNullOrEmpty(attribute.ErrorMessage) || attribute.ErrorMessageResourceType is not null)
        {
            return attribute.FormatErrorMessage(name);
        }

        return attribute switch
        {
            RequiredAttribute => messages.Format(HaldusMessageKeys.ValidationRequired, name),
            MaxLengthAttribute maxLength => messages.Format(HaldusMessageKeys.ValidationMaxLength, name, maxLength.Length),
            MinLengthAttribute minLength => messages.Format(HaldusMessageKeys.ValidationMinLength, name, minLength.Length),
            StringLengthAttribute { MinimumLength: > 0 } stringLength => messages.Format(
                HaldusMessageKeys.ValidationStringLength, name, stringLength.MinimumLength, stringLength.MaximumLength),
            StringLengthAttribute stringLength => messages.Format(
                HaldusMessageKeys.ValidationMaxLength, name, stringLength.MaximumLength),
            RangeAttribute range => messages.Format(HaldusMessageKeys.ValidationRange, name, range.Minimum, range.Maximum),
            EmailAddressAttribute or UrlAttribute or PhoneAttribute or RegularExpressionAttribute =>
                messages.Format(HaldusMessageKeys.ValidationInvalidFormat, name),
            _ => attribute.FormatErrorMessage(name),
        };
    }

    private sealed record PropertyRules(PropertyInfo Property, string JsonName, ValidationAttribute[] Attributes);
}
