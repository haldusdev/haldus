using Microsoft.AspNetCore.Routing;

namespace Haldus;

/// <summary>
/// Archetype'lar: her biri bir kaynağın hangi standart işlemlere sahip olduğunu ve nasıl adreslendiğini
/// seçer. Endpoint'lerin kendisi derleme zamanında üretilir; bu metotlar onları çalışma zamanında bağlar.
/// </summary>
public static class HaldusEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Kendi kimliği ve yaşam döngüsü olan bağımsız bir entity: liste, tekil okuma, oluşturma, güncelleme
    /// ve silme.
    /// </summary>
    public static ResourceBuilder<TEntity> MapCrud<TEntity>(this IEndpointRouteBuilder endpoints)
        where TEntity : class =>
        new(HaldusEndpointRegistry.Map<TEntity>(endpoints, nameof(MapCrud)));

    /// <summary>İstemcinin hiç yazmadığı kaynak: yalnızca liste ve tekil okuma.</summary>
    public static ResourceBuilder<TEntity> MapReadOnly<TEntity>(this IEndpointRouteBuilder endpoints)
        where TEntity : class =>
        new(HaldusEndpointRegistry.Map<TEntity>(endpoints, nameof(MapReadOnly)));

    /// <summary>İstemcinin oluşturduğu ama sonradan değiştirilemeyen ve silinemeyen kayıtlar.</summary>
    public static ResourceBuilder<TEntity> MapAppendOnly<TEntity>(this IEndpointRouteBuilder endpoints)
        where TEntity : class =>
        new(HaldusEndpointRegistry.Map<TEntity>(endpoints, nameof(MapAppendOnly)));

    /// <summary>
    /// Anahtarını istemcinin verdiği kayıtlar: <c>PUT /{key}</c> kayıt yoksa oluşturur, varsa tam değiştirir.
    /// </summary>
    public static ResourceBuilder<TEntity> MapStore<TEntity>(this IEndpointRouteBuilder endpoints)
        where TEntity : class =>
        new(HaldusEndpointRegistry.Map<TEntity>(endpoints, nameof(MapStore)));

    /// <summary>
    /// Kod ve ad taşıyan küçük referans listesi (ülke, para birimi): sayfalamasız tam liste, koda göre
    /// okuma, ekle-veya-güncelle ve silme.
    /// </summary>
    public static ResourceBuilder<TEntity> MapLookup<TEntity>(this IEndpointRouteBuilder endpoints)
        where TEntity : class =>
        new(HaldusEndpointRegistry.Map<TEntity>(endpoints, nameof(MapLookup)));

    /// <summary>
    /// Kapsam başına tam bir kayıt (ayarlar gibi). Kayıt örtük olarak vardır: hiç yazılmadıysa okuma
    /// varsayılan değerleri döner. <c>.MultiTenant()</c> ile kapsam kiracı olur.
    /// </summary>
    public static ResourceBuilder<TEntity> MapSingleton<TEntity>(this IEndpointRouteBuilder endpoints)
        where TEntity : class =>
        new(HaldusEndpointRegistry.Map<TEntity>(endpoints, nameof(MapSingleton)));

    /// <summary>
    /// İsteği aşan bir iş (rapor üretme, içe aktarma): <c>POST</c> işi oluşturur ve hemen 202 döner, iş arka
    /// planda çalışır, <c>GET</c> durumunu gösterir, <c>POST /{id}/cancel</c> iptal eder. Entity işin
    /// girdisini (yazılabilir alanlar) ve çıktısını (herkese açık setter'ı olmayan alanlar) taşır; mantığı
    /// Haldus'un bulduğu bir <see cref="IJobHandler{TJob}"/> sınıfındadır.
    /// </summary>
    public static ResourceBuilder<TJob> MapJob<TJob>(this IEndpointRouteBuilder endpoints)
        where TJob : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        HaldusJobs.Require<TJob>(endpoints.ServiceProvider, hasLambda: false);

        return new(HaldusEndpointRegistry.Map<TJob>(endpoints, nameof(MapJob)));
    }

    /// <inheritdoc cref="MapJob{TJob}(IEndpointRouteBuilder)"/>
    /// <param name="handler">İşin mantığı; bağımlılıklarını <see cref="JobContext.Services"/> üzerinden alır.</param>
    public static ResourceBuilder<TJob> MapJob<TJob>(
        this IEndpointRouteBuilder endpoints,
        Func<TJob, JobContext, CancellationToken, Task> handler)
        where TJob : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(handler);
        HaldusJobs.Require<TJob>(endpoints.ServiceProvider, hasLambda: true);
        HaldusJobRegistry.SetHandler(handler);

        return new(HaldusEndpointRegistry.Map<TJob>(endpoints, nameof(MapJob)));
    }

    /// <summary>
    /// İki kaynak arasında çoktan-çoğa bağ: <c>PUT</c> ile kur, <c>DELETE</c> ile kaldır, iki yönden de
    /// listele (<c>/products/{id}/tags</c> ve <c>/tags/{id}/products</c>). Bağ kayıtlarının entity'si
    /// (<c>ProductTag</c>) üretilir; iki uç da bir <c>Map*</c> çağrısıyla eşlenmiş olmalıdır.
    /// </summary>
    public static RouteGroupBuilder MapAssociation<TLeft, TRight>(this IEndpointRouteBuilder endpoints)
        where TLeft : class
        where TRight : class =>
        HaldusEndpointRegistry.MapAssociation<TLeft, TRight>(endpoints);
}
