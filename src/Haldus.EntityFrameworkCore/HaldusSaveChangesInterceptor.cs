using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Haldus;

/// <summary>Trait arayüzlerini taşıyan entity'lerin alanlarını kaydetmeden hemen önce doldurur.</summary>
public sealed class HaldusSaveChangesInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);

        if (eventData.Context is { } context)
        {
            HaldusRollups.Begin(context);
        }

        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);

        if (eventData.Context is { } context)
        {
            await HaldusRollups.BeginAsync(context, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is { } context)
        {
            // Eşzamanlı kaydetmede de aynı kural geçerlidir; üretilen kod her zaman asenkron kaydeder.
            HaldusRollups.ApplyAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        }

        HaldusEvents.Notify();

        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            await HaldusRollups.ApplyAsync(context, cancellationToken);
        }

        // Yazılan olayların dağıtıcısı beklemeden uyanır.
        HaldusEvents.Notify();

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context)
        {
            HaldusRollups.Forget(context);
        }

        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            HaldusRollups.Forget(context);
        }

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        if (eventData.Context is { } context)
        {
            // İptal edilen kaydetme de başarısız olan gibi geri alınır; açılan transaction bağlamda asılı kalmaz.
            HaldusRollups.Forget(context);
        }

        base.SaveChangesCanceled(eventData);
    }

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            HaldusRollups.Forget(context);
        }

        return base.SaveChangesCanceledAsync(eventData, cancellationToken);
    }

    private static void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = HaldusTime.UtcNow(context.GetService<TimeProvider>());
        // Kimlik, bir isteğin dışında çalışan kodda (arka plandaki iş) HaldusAmbient'ten gelir.
        var tenants = new Lazy<Guid?>(() => HaldusScope.TenantId(context));
        var users = new Lazy<string?>(() => HaldusScope.UserId(context));

        // Gömülü satır sahibinin parçasıdır: satırı değişen sahip de değişmiş sayılır ve aşağıdaki kurallardan geçer.
        HaldusDetails.TouchOwners(context);

        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    OnAdded(entry, now, tenants, users);

                    if (entry.Entity is IApprovable approvable)
                    {
                        // Yeni kayıt, onu oluşturandan başka birinin kararını bekler; alanlar elle verilemez.
                        HaldusApprovals.Reset(approvable, context);
                    }

                    break;
                case EntityState.Modified:
                    OnModified(entry, now, users);

                    if (entry.Entity is IApprovable changed)
                    {
                        // Onay, onaylayanın gördüğü içeriğe verilmiştir: içerik hangi yoldan değişirse değişsin
                        // (güncelleme, aksiyon, satır komutu, uygulamanın kendi kodu) toplanan onaylar düşer. Tek
                        // istisna, onayın zaten izin verdiği işlemin kendi yazdıklarıdır.
                        var exempt = HaldusApprovals.TakeExemption(changed);

                        if (!exempt && HaldusApprovals.ContentChanged(entry))
                        {
                            HaldusApprovals.Reset(changed, context);
                        }
                    }

                    break;
                case EntityState.Deleted when entry.Entity is ISoftDeletable:
                    // Silme, silinme zamanını yazan bir güncellemeye çevrilir.
                    entry.State = EntityState.Unchanged;
                    entry.Property(nameof(ISoftDeletable.DeletedAt)).CurrentValue = now;
                    OnModified(entry, now, users);
                    break;
            }
        }

        // Anlık görüntü ve değişiklik günlüğü, trait alanları yazıldıktan sonra ve kaydı hangi kod yazarsa yazsın alınır.
        HaldusRevisions.Capture(context, now, users);
        HaldusChanges.Capture(context, now);

        // Olaylar kayıtla aynı kaydetmede yazılır; kayıt kalıcı olmadıysa olay da yoktur.
        HaldusEvents.Capture(context, now, users);

        // Başka kayıtlardan toplanan alanlar, bağlı kayıt kaydedildikten sonra baştan hesaplanır.
        HaldusRollups.Collect(context);
    }

    private static void OnAdded(EntityEntry entry, DateTimeOffset now, Lazy<Guid?> tenants, Lazy<string?> users)
    {
        if (entry.Entity is IMultiTenant tenantOwned)
        {
            if (tenants.Value is { } tenantId)
            {
                tenantOwned.TenantId = tenantId;
            }
            else if (tenantOwned.TenantId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Cannot save '{entry.Metadata.DisplayName()}' without a tenant. " +
                    "Either resolve a tenant for the current scope or set TenantId explicitly.");
            }
        }

        if (entry.Entity is IOwned owned)
        {
            if (users.Value is { Length: > 0 } ownerId)
            {
                owned.OwnerId = ownerId;
            }
            else if (string.IsNullOrEmpty(owned.OwnerId))
            {
                throw new InvalidOperationException(
                    $"Cannot save '{entry.Metadata.DisplayName()}' without an owner. " +
                    "Either resolve a user for the current scope or set OwnerId explicitly.");
            }
        }

        if (entry.Entity is IAuditable auditable)
        {
            auditable.CreatedAt = now;
        }

        if (entry.Entity is IUserAuditable userAuditable)
        {
            userAuditable.CreatedBy = users.Value;
        }
    }

    private static void OnModified(EntityEntry entry, DateTimeOffset now, Lazy<string?> users)
    {
        if (entry.Entity is IMultiTenant)
        {
            // Kayıt başka bir kiracıya taşınamaz.
            entry.Property(nameof(IMultiTenant.TenantId)).IsModified = false;
        }

        if (entry.Entity is IOwned)
        {
            // Kayıt başka bir kullanıcıya devredilemez.
            entry.Property(nameof(IOwned.OwnerId)).IsModified = false;
        }

        if (entry.Entity is IAuditable)
        {
            entry.Property(nameof(IAuditable.UpdatedAt)).CurrentValue = now;
        }

        if (entry.Entity is IUserAuditable)
        {
            entry.Property(nameof(IUserAuditable.UpdatedBy)).CurrentValue = users.Value;
        }
    }
}
