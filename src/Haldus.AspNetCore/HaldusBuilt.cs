using Microsoft.AspNetCore.Http.HttpResults;

namespace Haldus;

/// <summary>
/// Üretilen kodun, başka bir kayıttan kurduğu yeni kaydı döndürme biçimi: ya kayıt ya da onu reddeden hata.
/// İkisi de doğrudan döndürülebilir; çağıran önce hataya bakar.
/// </summary>
public readonly struct HaldusBuilt<TEntity>
    where TEntity : class
{
    private HaldusBuilt(TEntity? record, ProblemHttpResult? problem)
    {
        Record = record;
        Problem = problem;
    }

    public TEntity? Record { get; }

    public ProblemHttpResult? Problem { get; }

    public static implicit operator HaldusBuilt<TEntity>(TEntity record) => new(record, null);

    public static implicit operator HaldusBuilt<TEntity>(ProblemHttpResult problem) => new(null, problem);
}
