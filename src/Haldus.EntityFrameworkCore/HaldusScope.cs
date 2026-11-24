using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Haldus;

/// <summary>Üretilen sorgu filtrelerinin adları. Tek bir filtreyi yok saymak için kullanılır.</summary>
public static class HaldusFilters
{
    public const string Tenant = "Haldus.Tenant";
    public const string SoftDelete = "Haldus.SoftDelete";

    /// <summary>Ebeveyn altındaki kaydı, ebeveyni isteği yapana görünür değilse gizleyen filtre.</summary>
    public const string Parent = "Haldus.Parent";

    /// <summary>Çoktan-çoğa bir bağı, iki ucundan biri isteği yapana görünür değilse gizleyen filtre.</summary>
    public const string Association = "Haldus.Association";

    /// <summary>Kaydı, sahibi olmayan kullanıcıdan gizleyen filtre.</summary>
    public const string Owner = "Haldus.Owner";

    /// <summary>Süresi dolmuş kaydı gizleyen filtre.</summary>
    public const string Expiry = "Haldus.Expiry";
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

    /// <summary>
    /// <typeparamref name="TEntity"/> bu bağlamda mı yaşıyor. Başka bir modülün entity'sine referans veren
    /// üretilen model kodu, yabancı anahtar kısıtını yalnızca iki entity aynı bağlamdaysa kurar; ayrı
    /// bağlamlarda referans yalnızca kimlikle tutulur.
    /// </summary>
    public static bool Shares<TEntity>(Microsoft.EntityFrameworkCore.DbContext context)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);

        return HaldusContextOptions.From(context) is not { } map
            || map.ContextOf(typeof(TEntity).Assembly) == context.GetType();
    }

    /// <summary>
    /// Bir kaynağın ebeveyni onunla aynı bağlamda olmalıdır: görünürlüğü ve zincirleme silme ebeveynin
    /// tablosuna dayanır. Değilse hata, ilk istekte değil model kurulurken verilir.
    /// </summary>
    public static void RequireShared<TParent>(Microsoft.EntityFrameworkCore.DbContext context, string child)
        where TParent : class
    {
        if (!Shares<TParent>(context))
        {
            throw new InvalidOperationException(
                $"'{child}' is declared under '{typeof(TParent).Name}', but they live in different DbContexts. " +
                "A resource and its parent must share a context; use References<T>() to refer to a record of another module by its key.");
        }
    }
}

/// <summary>Üretilen sorgu filtrelerinin, geçerli bağlam üzerinden istek kapsamındaki değerlere ulaştığı yer.</summary>
public static class HaldusScope
{
    /// <summary>Geçerli kiracı. Kiracı çözülemediyse <c>null</c> döner ve filtre hiçbir kaydı eşlemez.</summary>
    public static Guid? TenantId(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return HaldusAmbient.Current is { } ambient ? ambient.TenantId : context.GetService<ITenantProvider>().TenantId;
    }

    /// <summary>Geçerli kullanıcı.</summary>
    public static string? UserId(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return HaldusAmbient.Current is { } ambient ? ambient.UserId : context.GetService<ICurrentUser>().UserId;
    }

    /// <summary>Sorgunun çalıştığı an; süresi dolmuş kayıtları gizleyen filtre her sorguda bunu yeniden okur.</summary>
    public static DateTimeOffset UtcNow(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return HaldusTime.UtcNow(context.GetService<TimeProvider>());
    }
}

/// <summary>
/// Bir HTTP isteği dışında çalışan kodun (arka plandaki bir iş gibi) kimliği. Etkinken sorgu filtreleri ve
/// trait alanları, istekten çözülen kiracı ve kullanıcı yerine bunu kullanır; böylece iş, onu başlatan
/// kiracının verisini görür ve yazdığı kayıtlar o kiracıya ait olur.
/// </summary>
public static class HaldusAmbient
{
    private static readonly AsyncLocal<Identity?> Value = new();

    internal static Identity? Current => Value.Value;

    /// <summary>Dönen nesne atılana kadar, bu asenkron akışta verilen kimlik geçerli olur.</summary>
    public static IDisposable Use(Guid? tenantId, string? userId)
    {
        var previous = Value.Value;
        Value.Value = new Identity(tenantId, userId);
        return new Scope(previous);
    }

    internal sealed record Identity(Guid? TenantId, string? UserId);

    private sealed class Scope(Identity? previous) : IDisposable
    {
        public void Dispose() => Value.Value = previous;
    }
}