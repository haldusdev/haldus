using Microsoft.EntityFrameworkCore;

namespace Haldus;

/// <summary>Bir dönemin, kapsamındaki öteki dönemlerle birlikte yazılıp yazılamayacağı.</summary>
public enum EffectiveOutcome
{
    /// <summary>Dönem yazılabilir.</summary>
    Accepted,

    /// <summary>Bitiş, başlangıçtan sonra değil.</summary>
    Invalid,

    /// <summary>Dönem, kapsamındaki başka bir kaydın dönemiyle çakışıyor.</summary>
    Overlap,
}

/// <summary>
/// Dönemli kayıtların (<c>.EffectiveDated()</c>) kuralı: bir kapsamda herhangi bir gün için en çok bir kayıt
/// geçerlidir. Üretilen uçlar bunu kaydetmeden hemen önce, kapsamın kilidi altında çağırır.
/// </summary>
public static class HaldusEffectiveDating
{
    /// <summary>
    /// Kaydın dönemini kapsamındaki öteki kayıtlarla karşılaştırır. Yeni dönem, açık uçlu tek bir önceki dönemin
    /// içinde başlıyorsa önceki dönem yeni dönemin başladığı gün kapatılır (ücret değişince eskisinin bitmesi
    /// gibi); başka her çakışma reddedilir.
    /// </summary>
    /// <param name="siblings">Kapsamdaki kayıtlar; izlenen bir sorgu olmalıdır.</param>
    public static async Task<EffectiveOutcome> ApplyAsync<TEntity>(
        IQueryable<TEntity> siblings,
        TEntity entity,
        CancellationToken cancellationToken)
        where TEntity : class, IEffectiveDated
    {
        ArgumentNullException.ThrowIfNull(siblings);
        ArgumentNullException.ThrowIfNull(entity);

        var from = entity.EffectiveFrom;
        var to = entity.EffectiveTo;

        if (to is not null && to <= from)
        {
            return EffectiveOutcome.Invalid;
        }

        var overlapping = await siblings
            .Where(other => (to == null || other.EffectiveFrom < to) && (other.EffectiveTo == null || other.EffectiveTo > from))
            .ToListAsync(cancellationToken);

        // Kaydın kendisi de (izlenen aynı nesne olarak) sorgudan dönebilir.
        overlapping.RemoveAll(other => ReferenceEquals(other, entity));

        // Veritabanındaki hali çakışan ama bu istekte değişmiş (ör. az önce kapatılmış) kayıtlar bellekteki haliyle sınanır.
        overlapping.RemoveAll(other => !((to is null || other.EffectiveFrom < to) && (other.EffectiveTo is null || other.EffectiveTo > from)));

        if (overlapping is [{ EffectiveTo: null } previous] && previous.EffectiveFrom < from)
        {
            previous.EffectiveTo = from;
            return EffectiveOutcome.Accepted;
        }

        return overlapping.Count == 0 ? EffectiveOutcome.Accepted : EffectiveOutcome.Overlap;
    }
}