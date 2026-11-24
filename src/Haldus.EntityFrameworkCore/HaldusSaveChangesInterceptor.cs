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
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
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
