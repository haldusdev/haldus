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

    /// <summary>
    /// Güncellenen bir satırın kimliği bu kayda ait değil (veya aynı kimlik istekte iki kez geçiyor).
    /// </summary>
    public static ProblemHttpResult DetailNotFound(IHaldusMessages messages, string field, string detailName, object id)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return FieldProblem(messages, field, messages.Format(HaldusMessageKeys.DetailNotFound, detailName, id));
    }

    /// <summary>Bir düğüm, kendi altındaki bir düğümün çocuğu yapılmak isteniyor.</summary>
    public static ProblemHttpResult HierarchyCycle(IHaldusMessages messages, string field)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return FieldProblem(messages, field, messages.Format(HaldusMessageKeys.HierarchyCycle));
    }

    /// <summary>Doğal anahtarı aynı olan başka bir kayıt var.</summary>
    /// <param name="field">Doğal anahtar alanının adı.</param>
    public static ProblemHttpResult Duplicate(IHaldusMessages messages, string resourceName, string field, object? value)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.DuplicateTitle),
            detail: messages.Format(HaldusMessageKeys.DuplicateDetail, resourceName, field, value),
            statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>Liste, sıralanabilir olarak bildirilmemiş bir alana göre sıralanmak isteniyor.</summary>
    /// <param name="requested">İstekte geçen, tanınmayan alan.</param>
    /// <param name="sortable">Sıralanabilir alanlar.</param>
    public static ProblemHttpResult SortInvalid(IHaldusMessages messages, string requested, string sortable)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return FieldProblem(messages, "sort", messages.Format(HaldusMessageKeys.SortInvalid, requested, sortable));
    }

    /// <summary>Metadata çantası, birleştirildikten sonra sınırlarını aşıyor.</summary>
    public static ProblemHttpResult MetadataInvalid(IHaldusMessages messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return FieldProblem(messages, "metadata", MetadataMessage(messages, "Metadata"));
    }

    internal static string MetadataMessage(IHaldusMessages messages, string name) => messages.Format(
        HaldusMessageKeys.MetadataInvalid, name, HaldusMetadata.MaxEntries, HaldusMetadata.MaxKeyLength, HaldusMetadata.MaxValueLength);

    /// <summary>Kayıt, başka kayıtlar ona referans verdiği için silinemiyor.</summary>
    public static ProblemHttpResult InUse(IHaldusMessages messages, string resourceName, object id)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.InUseTitle),
            detail: messages.Format(HaldusMessageKeys.InUseDetail, resourceName, id),
            statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>İşleme (güncelleme, silme veya bir geçiş) kaydın bulunduğu durumda izin verilmiyor.</summary>
    /// <param name="operation">İşlemin adı: <c>update</c>, <c>delete</c> veya geçişin adı.</param>
    /// <param name="state">Kaydın bulunduğu durum.</param>
    public static ProblemHttpResult StateConflict(IHaldusMessages messages, string operation, object state)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.StateConflictTitle),
            detail: messages.Format(HaldusMessageKeys.StateConflictDetail, operation, state),
            statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Kaydın durumu, istek işlenirken başka bir istek tarafından değiştirildi; bu istek eski durumu
    /// görerek karar verdiği için hiçbir şey kaydedilmedi.
    /// </summary>
    public static ProblemHttpResult StateChanged(IHaldusMessages messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.StateConflictTitle),
            detail: messages.Format(HaldusMessageKeys.StateChangedDetail),
            statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>Geçişin iş mantığı geçişi reddetti; açıklama onu reddeden koddan gelir.</summary>
    public static ProblemHttpResult TransitionRejected(IHaldusMessages messages, string reason)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.TransitionRejectedTitle),
            detail: reason,
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary><c>Idempotency-Key</c> başlığı zorunlu olduğu halde gönderilmedi veya çok uzun.</summary>
    public static ProblemHttpResult IdempotencyKeyInvalid(IHaldusMessages messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.IdempotencyKeyInvalidTitle),
            detail: messages.Format(HaldusMessageKeys.IdempotencyKeyInvalidDetail, HaldusIdempotency.MaxKeyLength),
            statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>Aynı <c>Idempotency-Key</c> daha önce farklı bir istek gövdesiyle kullanıldı.</summary>
    public static ProblemHttpResult IdempotencyKeyReused(IHaldusMessages messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.IdempotencyKeyReusedTitle),
            detail: messages.Format(HaldusMessageKeys.IdempotencyKeyReusedDetail),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>Toplu istek, izin verilenden çok öğe taşıyor.</summary>
    public static ProblemHttpResult BatchTooLarge(IHaldusMessages messages, int maxItems)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.BatchTooLargeTitle),
            detail: messages.Format(HaldusMessageKeys.BatchTooLargeDetail, maxItems),
            statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>Toplu istekteki bir öğe boş (<c>null</c>) gönderildi.</summary>
    public static ProblemHttpResult BatchItemMissing(IHaldusMessages messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.ValidationTitle),
            detail: messages.Format(HaldusMessageKeys.BatchItemMissingDetail),
            statusCode: StatusCodes.Status400BadRequest);
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

    /// <summary>Kullanıcıya ait bir kaynağa, kullanıcısı çözülemeyen bir istekle erişildi.</summary>
    public static ProblemHttpResult UserRequired(IHaldusMessages messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return TypedResults.Problem(
            title: messages.Format(HaldusMessageKeys.UserRequiredTitle),
            detail: messages.Format(HaldusMessageKeys.UserRequiredDetail),
            statusCode: StatusCodes.Status401Unauthorized);
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
