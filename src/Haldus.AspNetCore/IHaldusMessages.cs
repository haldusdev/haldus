using System.Globalization;
using System.Resources;

namespace Haldus;

/// <summary>
/// Haldus'un son kullanıcıya döndürdüğü metinlerin kaynağı. Tüketen proje bu servisi değiştirerek
/// metinleri ezebilir veya yeni dil ekleyebilir.
/// </summary>
public interface IHaldusMessages
{
    string Format(string key, params object?[] arguments);
}

public static class HaldusMessageKeys
{
    public const string NotFoundTitle = "NotFound.Title";
    public const string NotFoundDetail = "NotFound.Detail";
    public const string PreconditionRequiredTitle = "PreconditionRequired.Title";
    public const string PreconditionRequiredDetail = "PreconditionRequired.Detail";
    public const string PreconditionFailedTitle = "PreconditionFailed.Title";
    public const string PreconditionFailedDetail = "PreconditionFailed.Detail";
    public const string ReferenceNotFound = "Reference.NotFound";
    public const string HierarchyCycle = "Hierarchy.Cycle";
    public const string DetailNotFound = "Detail.NotFound";
    public const string InUseTitle = "InUse.Title";
    public const string InUseDetail = "InUse.Detail";
    public const string StateConflictTitle = "StateConflict.Title";
    public const string StateConflictDetail = "StateConflict.Detail";
    public const string StateChangedDetail = "StateChanged.Detail";
    public const string TransitionRejectedTitle = "TransitionRejected.Title";
    public const string IdempotencyKeyInvalidTitle = "IdempotencyKeyInvalid.Title";
    public const string IdempotencyKeyInvalidDetail = "IdempotencyKeyInvalid.Detail";
    public const string IdempotencyKeyReusedTitle = "IdempotencyKeyReused.Title";
    public const string IdempotencyKeyReusedDetail = "IdempotencyKeyReused.Detail";
    public const string BatchTooLargeTitle = "BatchTooLarge.Title";
    public const string BatchTooLargeDetail = "BatchTooLarge.Detail";
    public const string BatchItemMissingDetail = "BatchItemMissing.Detail";
    public const string SearchTooComplexTitle = "SearchTooComplex.Title";
    public const string SearchTooComplexDetail = "SearchTooComplex.Detail";
    public const string UserRequiredTitle = "UserRequired.Title";
    public const string UserRequiredDetail = "UserRequired.Detail";
    public const string TenantRequiredTitle = "TenantRequired.Title";
    public const string TenantRequiredDetail = "TenantRequired.Detail";
    public const string ValidationTitle = "Validation.Title";
    public const string ValidationRequired = "Validation.Required";
    public const string ValidationMaxLength = "Validation.MaxLength";
    public const string ValidationMinLength = "Validation.MinLength";
    public const string ValidationStringLength = "Validation.StringLength";
    public const string ValidationRange = "Validation.Range";
    public const string ValidationInvalidFormat = "Validation.InvalidFormat";
}

internal sealed class ResourceHaldusMessages : IHaldusMessages
{
    private static readonly ResourceManager Resources =
        new("Haldus.AspNetCore.Resources.HaldusMessages", typeof(ResourceHaldusMessages).Assembly);

    public string Format(string key, params object?[] arguments)
    {
        var template = Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        return arguments.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, arguments);
    }
}
