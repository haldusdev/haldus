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
