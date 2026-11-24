using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Haldus;

/// <summary>Üretilen endpoint'lerin döndürdüğü yerelleştirilmiş hata yanıtları.</summary>
public static class HaldusProblems
{
    public static ProblemHttpResult NotFound(IHaldusMessages messages, string resourceName, object id)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.NotFoundTitle),
            detail: messages.Format(HaldusMessageKeys.NotFoundDetail, resourceName, id),
            statusCode: StatusCodes.Status404NotFound);
    }

    /// <summary>Eşzamanlılık kontrollü bir kayıt için If-Match başlığı gönderilmedi.</summary>
    public static ProblemHttpResult PreconditionRequired(IHaldusMessages messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.PreconditionRequiredTitle),
            detail: messages.Format(HaldusMessageKeys.PreconditionRequiredDetail),
            statusCode: StatusCodes.Status428PreconditionRequired);
    }

    /// <summary>Kayıt, istemcinin okuduğu sürümden sonra değişti.</summary>
    public static ProblemHttpResult PreconditionFailed(IHaldusMessages messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.PreconditionFailedTitle),
            detail: messages.Format(HaldusMessageKeys.PreconditionFailedDetail),
            statusCode: StatusCodes.Status412PreconditionFailed);
    }

    /// <summary>
    /// Bir referans alanı, var olmayan (veya isteği yapanın göremediği) bir kaydı gösteriyor. Yanıt, alan
    /// düzeyindeki diğer doğrulama hatalarıyla aynı biçimdedir.
    /// </summary>
    public static ProblemHttpResult ReferenceNotFound(IHaldusMessages messages, string field, string targetName, object value)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return FieldProblem(messages, field, messages.Format(HaldusMessageKeys.ReferenceNotFound, targetName, value));
    }

    /// <summary>Bir düğüm, kendi altındaki bir düğümün çocuğu yapılmak isteniyor.</summary>
    public static ProblemHttpResult HierarchyCycle(IHaldusMessages messages, string field)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return FieldProblem(messages, field, messages.Format(HaldusMessageKeys.HierarchyCycle));
    }

    /// <summary>Kayıt, başka kayıtlar ona referans verdiği için silinemiyor.</summary>
    public static ProblemHttpResult InUse(IHaldusMessages messages, string resourceName, object id)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.InUseTitle),
            detail: messages.Format(HaldusMessageKeys.InUseDetail, resourceName, id),
            statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>Arama isteği izin verilen derinliği, koşul sayısını veya liste uzunluğunu aştı.</summary>
    public static ProblemHttpResult SearchTooComplex(IHaldusMessages messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.SearchTooComplexTitle),
            detail: messages.Format(
                HaldusMessageKeys.SearchTooComplexDetail,
                SearchBudget.MaxDepth,
                SearchBudget.MaxConditions,
                SearchBudget.MaxListValues,
                SearchBudget.MaxSortFields),
            statusCode: StatusCodes.Status400BadRequest);
    }

    private static ProblemHttpResult FieldProblem(IHaldusMessages messages, string field, string error) =>
        TypedResults.Problem(new HttpValidationProblemDetails(new Dictionary<string, string[]> { [field] = [error] })
        {
            Title = messages.Format(HaldusMessageKeys.ValidationTitle),
            Status = StatusCodes.Status400BadRequest,
        });

    /// <summary>Kiracıya bağlı bir kaynağa, kiracısı çözülemeyen bir istekle erişildi.</summary>
    public static ProblemHttpResult TenantRequired(IHaldusMessages messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.TenantRequiredTitle),
            detail: messages.Format(HaldusMessageKeys.TenantRequiredDetail),
            statusCode: StatusCodes.Status403Forbidden);
    }
}
