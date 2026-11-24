namespace Haldus;

/// <summary>
/// Trait zincir metotları. Çalışma zamanında hiçbir şey yapmazlar: generator bunları derleme zamanında
/// okur ve entity'yi, DTO'ları ve endpoint'leri buna göre üretir. Bu yüzden doğrudan <c>Map*</c>
/// çağrısının devamında yazılmaları gerekir.
/// </summary>
public static class ResourceBuilderTraitExtensions
{
    /// <summary>Kayıtları kiracıya göre ayırır: <c>TenantId</c> alanı, otomatik sorgu filtresi ve atama.</summary>
    public static ResourceBuilder<TEntity> MultiTenant<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>Oluşturulma ve değiştirilme zamanını tutar.</summary>
    /// <param name="trackUser">Oluşturan ve değiştiren kullanıcı da tutulsun mu.</param>
    public static ResourceBuilder<TEntity> Auditable<TEntity>(this ResourceBuilder<TEntity> builder, bool trackUser = false)
        where TEntity : class => builder;

    /// <summary>Silmeyi geri alınabilir yapar; geri yükleme ve kalıcı silme endpoint'lerini ekler.</summary>
    public static ResourceBuilder<TEntity> SoftDeletable<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>Güncelleme ve silmede <c>If-Match</c> başlığıyla iyimser eşzamanlılık kontrolü yapar.</summary>
    public static ResourceBuilder<TEntity> ConcurrencyChecked<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>Kaydı silmeden pasifleştirmeyi sağlar; arşivleme endpoint'lerini ekler.</summary>
    public static ResourceBuilder<TEntity> Archivable<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>Verilen standart işlemlerin endpoint'lerini üretmez.</summary>
    public static ResourceBuilder<TEntity> Without<TEntity>(this ResourceBuilder<TEntity> builder, params CrudOperation[] operations)
        where TEntity : class => builder;

    /// <summary>Yalnızca verilen standart işlemlerin endpoint'lerini üretir.</summary>
    public static ResourceBuilder<TEntity> Only<TEntity>(this ResourceBuilder<TEntity> builder, params CrudOperation[] operations)
        where TEntity : class => builder;
}
