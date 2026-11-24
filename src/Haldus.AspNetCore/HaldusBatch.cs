using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Haldus;

// Toplu işlemler (.Batch()). Her toplu uç, tekil ucun kurallarını öğe başına aynen uygular; fark yalnızca
// başarısızlıkta ne olduğudur: atomik modda hiçbir şey kaydedilmez, kısmi modda geçerli öğeler kaydedilir.

/// <summary>Kimlikleriyle verilen kayıtlar üzerinde toplu okuma ve silme isteği.</summary>
public sealed record BatchIdsRequest<TKey>
{
    [Required]
    public IReadOnlyList<TKey> Ids { get; init; } = default!;
}

/// <summary>Atomik bir toplu yazmanın sonucu: öğeler, istekteki sırayla.</summary>
public sealed record BatchResult<T>(IReadOnlyList<T> Items);

/// <summary>Toplu okumanın sonucu: bulunan kayıtlar ve bulunamayan (veya isteği yapanın göremediği) kimlikler.</summary>
public sealed record BatchGetResult<T, TKey>(IReadOnlyList<T> Items, IReadOnlyList<TKey> NotFound);

/// <summary>Kısmi başarılı bir toplu işlemin sonucu: her öğe için ayrı sonuç, istekteki sırayla.</summary>
public sealed record BatchResults<T>(IReadOnlyList<BatchItemResult<T>> Results);

/// <summary>Toplu işlemdeki tek bir öğenin sonucu.</summary>
/// <param name="Index">Öğenin istekteki sırası.</param>
/// <param name="Status">Öğe tek başına gönderilseydi dönecek HTTP durum kodu.</param>
/// <param name="Item">Başarılıysa sonuç.</param>
/// <param name="Problem">Başarısızsa neden; tekil ucun döndüreceği hata ile aynıdır.</param>
public sealed record BatchItemResult<T>(int Index, int Status, T? Item, ProblemDetails? Problem);

/// <summary>Üretilen toplu uçların kullandığı yardımcılar.</summary>
public static class HaldusBatch
{
    /// <summary>Bir toplu istekte, <c>.Batch(maxItems: ...)</c> ile değiştirilmedikçe kabul edilen en çok öğe.</summary>
    public const int DefaultMaxItems = 100;

    /// <summary>
    /// Bir toplu işlemin veya tek bir öğesinin sınırını açar: açık bir transaction yoksa yeni bir transaction,
    /// varsa onun içinde bir kayıt noktası. <see cref="BatchScope.CommitAsync"/> çağrılmadan atılırsa yaptığı
    /// her şey geri alınır ve bağlamdaki kaydedilmemiş değişiklikler bırakılır.
    /// </summary>
    public static async Task<BatchScope> BeginAsync(DbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Database.CurrentTransaction is { } ambient)
        {
            var savepoint = $"haldus_batch_{Guid.NewGuid():N}";
            await ambient.CreateSavepointAsync(savepoint, cancellationToken);
            return new BatchScope(context, ambient, savepoint);
        }

        return new BatchScope(context, await context.Database.BeginTransactionAsync(cancellationToken), savepoint: null);
    }

    /// <summary>Atomik modda bir öğenin hatasını, hangi öğeye ait olduğunu belirterek tüm isteğin hatası yapar.</summary>
    public static ProblemHttpResult ItemProblem(ProblemHttpResult problem, int index)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return TypedResults.Problem(Describe(problem.ProblemDetails, index, prefix: $"items[{index}]."));
    }

    public static BatchItemResult<T> Succeeded<T>(int index, int status, T item) => new(index, status, item, Problem: null);

    public static BatchItemResult<T> Failed<T>(int index, ProblemHttpResult problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new BatchItemResult<T>(index, problem.StatusCode, Item: default, Describe(problem.ProblemDetails, index, prefix: string.Empty));
    }

    /// <summary>Kısmi modda bir öğeyi tek başına doğrular; atomik modda bunu isteğin doğrulama filtresi yapar.</summary>
    public static ProblemHttpResult? Validate(object item, IHaldusMessages messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return item is null
            ? HaldusProblems.BatchItemMissing(messages)
            : HaldusValidation.Validate(item, messages) is { } invalid ? TypedResults.Problem(invalid.ProblemDetails) : null;
    }

    /// <summary>
    /// Hatayı yanıt gövdesine yazılabilir hale getirir. Alan düzeyindeki hatalar türetilmiş bir tipte
    /// durduğu için temel tipe kopyalanır; aksi halde JSON'a yazılırken kaybolurlardı.
    /// </summary>
    private static ProblemDetails Describe(ProblemDetails source, int index, string prefix)
    {
        var copy = new ProblemDetails
        {
            Type = source.Type,
            Title = source.Title,
            Status = source.Status,
            Detail = source.Detail,
            Instance = source.Instance,
        };

        foreach (var extension in source.Extensions)
        {
            copy.Extensions[extension.Key] = extension.Value;
        }

        if (source is HttpValidationProblemDetails { Errors.Count: > 0 } validation)
        {
            copy.Extensions["errors"] = validation.Errors.ToDictionary(error => prefix + error.Key, error => error.Value);
        }

        if (prefix.Length > 0)
        {
            copy.Extensions["index"] = index;
        }

        return copy;
    }
}

/// <summary>Bir toplu işlemin veya öğesinin geri alınabilir sınırı.</summary>
public sealed class BatchScope : IAsyncDisposable
{
    private readonly DbContext _context;
    private readonly IDbContextTransaction _transaction;
    private readonly string? _savepoint;
    private bool _committed;

    internal BatchScope(DbContext context, IDbContextTransaction transaction, string? savepoint)
    {
        _context = context;
        _transaction = transaction;
        _savepoint = savepoint;
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_savepoint is null)
        {
            await _transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await _transaction.ReleaseSavepointAsync(_savepoint, cancellationToken);
        }

        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_committed)
        {
            // Kaydedilmemiş değişiklikler bir sonraki öğeyle birlikte kaydedilmesin diye bırakılır. Değişmemiş
            // kayıtlara dokunulmaz; aynı bağlamı paylaşan başka kod (ör. idempotency kaydı) onları izliyor olabilir.
            foreach (var entry in _context.ChangeTracker.Entries().Where(entry => entry.State != EntityState.Unchanged).ToList())
            {
                entry.State = EntityState.Detached;
            }

            if (_savepoint is not null)
            {
                await _transaction.RollbackToSavepointAsync(_savepoint, CancellationToken.None);
            }
        }

        if (_savepoint is null)
        {
            await _transaction.DisposeAsync();
        }
    }
}
