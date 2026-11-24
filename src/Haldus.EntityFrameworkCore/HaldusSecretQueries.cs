using Microsoft.EntityFrameworkCore;

namespace Haldus;

/// <summary>Sır taşıyan kayıtların, sunulan sırla bulunması (ör. bir isteğin API anahtarını doğrulamak için).</summary>
public static class HaldusSecretQueries
{
    /// <summary>
    /// Sunulan sırrın ait olduğu kaydı bulur; yoksa <c>null</c> döner. Döndürülmüş bir sırrın eskisi, örtüşme
    /// süresi dolana kadar kabul edilir. Sorgu, verilen kaynağın filtrelerinden geçer: kimlik doğrulama gibi
    /// kiracının henüz bilinmediği bir yerde çağrılıyorsa kaynağı, çağıran kendisi filtresiz verir.
    /// </summary>
    /// <param name="now">Şimdiki zaman; eski sırrın süresinin dolup dolmadığına bununla bakılır.</param>
    public static Task<TEntity?> FindBySecretAsync<TEntity>(
        this IQueryable<TEntity> source,
        string? secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        where TEntity : class, IHasSecret
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrEmpty(secret) || secret.Length > 512)
        {
            return Task.FromResult<TEntity?>(null);
        }

        var hash = HaldusSecrets.Hash(secret);

        return typeof(IHasRotatingSecret).IsAssignableFrom(typeof(TEntity))
            ? source.FirstOrDefaultAsync(
                item => item.SecretHash == hash
                    || (EF.Property<string?>(item, nameof(IHasRotatingSecret.PreviousSecretHash)) == hash
                        && EF.Property<DateTimeOffset?>(item, nameof(IHasRotatingSecret.PreviousSecretExpiresAt)) > now),
                cancellationToken)
            : source.FirstOrDefaultAsync(item => item.SecretHash == hash, cancellationToken);
    }
}
