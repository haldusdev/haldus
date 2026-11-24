using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Haldus;

/// <summary>Üretilen sorgu filtrelerinin adları. Tek bir filtreyi yok saymak için kullanılır.</summary>
public static class HaldusFilters
{
    public const string Tenant = "Haldus.Tenant";
    public const string SoftDelete = "Haldus.SoftDelete";
}

/// <summary>Üretilen sorgu filtrelerinin, geçerli bağlam üzerinden istek kapsamındaki değerlere ulaştığı yer.</summary>
public static class HaldusScope
{
    /// <summary>Geçerli kiracı. Kiracı çözülemediyse <c>null</c> döner ve filtre hiçbir kaydı eşlemez.</summary>
    public static Guid? TenantId(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetService<ITenantProvider>().TenantId;
    }
}
