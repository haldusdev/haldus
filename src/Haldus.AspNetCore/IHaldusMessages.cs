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
    public const string DuplicateTitle = "Duplicate.Title";
    public const string DuplicateDetail = "Duplicate.Detail";
    public const string SortInvalid = "Sort.Invalid";
    public const string LocaleInvalid = "Locale.Invalid";
    public const string PurgeCriteria = "Purge.Criteria";
    public const string PurgeEverything = "Purge.Everything";
    public const string ChangeSetClosedTitle = "ChangeSet.Closed.Title";
    public const string ChangeSetCommittedDetail = "ChangeSet.Committed.Detail";
    public const string ChangeSetDiscardedDetail = "ChangeSet.Discarded.Detail";
    public const string ChangeSetInvalid = "ChangeSet.Invalid";
    public const string BalanceInsufficientTitle = "Balance.Insufficient.Title";
    public const string BalanceInsufficientDetail = "Balance.Insufficient.Detail";
    public const string BalanceChangedDetail = "Balance.Changed.Detail";
    public const string BalanceBucketUnknown = "Balance.Bucket.Unknown";
    public const string BalanceAmountInvalid = "Balance.Amount.Invalid";
    public const string BalanceMoveSame = "Balance.Move.Same";
    public const string BalanceHoldClosed = "Balance.Hold.Closed";
    public const string FileLinkInvalid = "File.LinkInvalid";
    public const string BalanceHoldLimit = "Balance.Hold.Limit";
    public const string TranslationLimit = "Translation.Limit";
    public const string MetadataInvalid = "Metadata.Invalid";
    public const string ApprovalRoleRequired = "Approval.RoleRequired";
    public const string RecurrenceByDayInvalid = "Recurrence.ByDayInvalid";
    public const string RecurrenceTimeZoneInvalid = "Recurrence.TimeZoneInvalid";
    public const string ChainLockedTitle = "ChainLocked.Title";
    public const string ReversedDetail = "Reversed.Detail";
    public const string ReversalDetail = "Reversal.Detail";
    public const string ReversalLockedDetail = "ReversalLocked.Detail";
    public const string SupersededDetail = "Superseded.Detail";
    public const string ChainMemberDetail = "ChainMember.Detail";
    public const string AmendedDetail = "Amended.Detail";
    public const string TransferFormatInvalid = "Transfer.FormatInvalid";
    public const string ImportTooLarge = "Import.TooLarge";
    public const string ImportMalformed = "Import.Malformed";
    public const string ImportRow = "Import.Row";
    public const string ImportRowUnreadable = "Import.RowUnreadable";
    public const string CustomFieldUnknown = "CustomField.Unknown";
    public const string CustomFieldInvalid = "CustomField.Invalid";
    public const string CustomFieldKeyInvalid = "CustomField.KeyInvalid";
    public const string CustomFieldOptionsInvalid = "CustomField.OptionsInvalid";
    public const string CustomFieldLimit = "CustomField.Limit";
    public const string FileTooLargeTitle = "FileTooLarge.Title";
    public const string FileTooLargeDetail = "FileTooLarge.Detail";
    public const string FileTypeNotAllowedTitle = "FileTypeNotAllowed.Title";
    public const string FileTypeNotAllowedDetail = "FileTypeNotAllowed.Detail";
    public const string FileEmpty = "File.Empty";
    public const string FileContentMissingDetail = "FileContentMissing.Detail";
    public const string EffectivePeriodInvalid = "EffectivePeriod.Invalid";
    public const string EffectivePeriodOverlapTitle = "EffectivePeriodOverlap.Title";
    public const string EffectivePeriodOverlapDetail = "EffectivePeriodOverlap.Detail";
    public const string ApprovalDeniedTitle = "ApprovalDenied.Title";
    public const string ApprovalUserRequired = "Approval.UserRequired";
    public const string ApprovalOwnRequest = "Approval.OwnRequest";
    public const string ApprovalAlreadyApproved = "Approval.AlreadyApproved";
    public const string TokenInvalidTitle = "TokenInvalid.Title";
    public const string TokenInvalidDetail = "TokenInvalid.Detail";
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
