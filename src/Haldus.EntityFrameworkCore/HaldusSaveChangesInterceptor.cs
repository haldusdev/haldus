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
        var tenants = new Lazy<ITenantProvider>(context.GetService<ITenantProvider>);
        var users = new Lazy<ICurrentUser>(context.GetService<ICurrentUser>);

        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    OnAdded(entry, now, tenants, users);
                    break;
                case EntityState.Modified:
                    OnModified(entry, now, users);
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

    private static void OnAdded(EntityEntry entry, DateTimeOffset now, Lazy<ITenantProvider> tenants, Lazy<ICurrentUser> users)
    {
        if (entry.Entity is IMultiTenant tenantOwned)
        {
            if (tenants.Value.TenantId is { } tenantId)
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

        if (entry.Entity is IAuditable auditable)
        {
            auditable.CreatedAt = now;
        }

        if (entry.Entity is IUserAuditable userAuditable)
        {
            userAuditable.CreatedBy = users.Value.UserId;
        }
    }

    private static void OnModified(EntityEntry entry, DateTimeOffset now, Lazy<ICurrentUser> users)
    {
        if (entry.Entity is IMultiTenant)
        {
            // Kayıt başka bir kiracıya taşınamaz.
            entry.Property(nameof(IMultiTenant.TenantId)).IsModified = false;
        }

        if (entry.Entity is IAuditable)
        {
            entry.Property(nameof(IAuditable.UpdatedAt)).CurrentValue = now;
        }

        if (entry.Entity is IUserAuditable)
        {
            entry.Property(nameof(IUserAuditable.UpdatedBy)).CurrentValue = users.Value.UserId;
        }
    }
}
