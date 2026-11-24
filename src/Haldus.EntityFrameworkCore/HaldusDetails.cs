using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace Haldus;

/// <summary>
/// Gömülü satırların (<c>MapDetail</c>) sahipleri. Satır sahibinin parçasıdır: bir satır eklendiğinde, değiştiğinde
/// veya silindiğinde sahibi de değişmiş sayılır. Üretilen uçlar bunu kendileri yapar; burada aynı kural, satırları
/// başka bir kodun (bir aksiyonun veya geçişin mantığı, uygulamanın kendi kodu) değiştirdiği durum için de
/// uygulanır. Böylece sahibin sürümü ve değiştirilme zamanı ilerler, anlık görüntüsü alınır ve toplanan onayları düşer.
/// </summary>
public static class HaldusDetails
{
    private static readonly ConcurrentDictionary<Type, (Type Owner, string ForeignKey)> Owners = new();

    /// <summary>Bir satır tipinin sahibini ve sahibine işaret eden alanını bildirir. Üretilen modül kodu çağırır.</summary>
    public static void Register<TOwner, TDetail>(string foreignKey)
        where TOwner : class
        where TDetail : class =>
        Owners[typeof(TDetail)] = (typeof(TOwner), foreignKey);

    /// <summary>Satırı değişen, kendisi değişmemiş görünen her sahibi değişmiş olarak işaretler.</summary>
    internal static void TouchOwners(DbContext context)
    {
        if (Owners.IsEmpty)
        {
            return;
        }

        var entries = context.ChangeTracker.Entries().ToList();

        foreach (var row in entries)
        {
            if (row.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)
                || !Owners.TryGetValue(row.Metadata.ClrType, out var link))
            {
                continue;
            }

            var foreignKey = row.Property(link.ForeignKey);
            var ownerKey = row.State == EntityState.Deleted ? foreignKey.OriginalValue : foreignKey.CurrentValue;

            if (ownerKey is null || context.Model.FindEntityType(link.Owner)?.FindPrimaryKey() is not { Properties: [var key] })
            {
                continue;
            }

            // Sahip çoğunlukla zaten izlenir (satırlara ondan ulaşılmıştır); izlenmiyorsa yüklenir.
            var owner = entries.FirstOrDefault(candidate =>
                    candidate.Metadata.ClrType == link.Owner && Equals(candidate.Property(key.Name).CurrentValue, ownerKey))
                ?? (context.Find(link.Owner, ownerKey) is { } found ? context.Entry(found) : null);

            if (owner is { State: EntityState.Unchanged })
            {
                owner.State = EntityState.Modified;
            }
        }
    }
}
