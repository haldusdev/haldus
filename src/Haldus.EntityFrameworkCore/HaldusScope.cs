using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Haldus;

/// <summary>Üretilen sorgu filtrelerinin adları. Tek bir filtreyi yok saymak için kullanılır.</summary>
public static class HaldusFilters
{
    public const string Tenant = "Haldus.Tenant";
    public const string SoftDelete = "Haldus.SoftDelete";
}

/// <summary>Üretilen model yapılandırmasının kullandığı sabitler.</summary>
public static class HaldusModel
{
    /// <summary>
    /// Kiracıya bağlı olmayan tek kayıtlı kaynakların anahtarı olan gölge alan. Değeri her zaman 1'dir;
    /// böylece tabloda ikinci bir satır oluşamaz.
    /// </summary>
    public const string SingletonKey = "SingletonKey";

    /// <summary>
    /// Bir ağaçta döngü aranırken köke doğru en fazla kaç düzey çıkılacağı. Daha derin bir zincir, bozuk
    /// veri veya sonsuz döngü olasılığına karşı döngü sayılır.
    /// </summary>
    public const int MaxHierarchyDepth = 64;
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
