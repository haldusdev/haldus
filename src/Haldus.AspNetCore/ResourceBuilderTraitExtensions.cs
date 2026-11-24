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

    /// <summary>
    /// Kayda okunabilir, benzersiz bir numara verir (<c>Number</c> alanı). Desen sabit metin ve
    /// <c>{seq}</c>, <c>{seq:6}</c>, <c>{yyyy}</c>, <c>{yy}</c>, <c>{MM}</c>, <c>{dd}</c> parçalarından oluşur;
    /// tarih parçaları sayacın ne zaman yeniden başlayacağını da belirler.
    /// </summary>
    /// <param name="pattern">Numara deseni, ör. <c>ORD-{yyyy}-{seq:6}</c>.</param>
    /// <param name="assignOn">
    /// Numaranın atanacağı durum (<c>Workflow</c> gerekir); verilmezse numara kayıt oluşturulurken atanır.
    /// </param>
    /// <param name="gapless">
    /// <c>true</c> ise numara kayıtla aynı transaction'da bir sayaç satırından alınır: geri alınan işlem numara
    /// yakmaz, sayaç kiracı ve dönem başına tutulur, aynı kapsamdaki eşzamanlı yazmalar sıraya girer.
    /// <c>false</c> ise bir veritabanı sequence'i kullanılır: yazmalar birbirini beklemez, ama numaralarda
    /// boşluk oluşabilir ve sayaç kiracı ya da dönem başına sıfırlanmaz.
    /// </param>
    public static ResourceBuilder<TEntity> Numbered<TEntity>(
        this ResourceBuilder<TEntity> builder,
        string pattern = "{seq:6}",
        Enum? assignOn = null,
        bool gapless = true)
        where TEntity : class => builder;

    /// <summary>
    /// Kayda bir son kullanma zamanı ekler (<c>ExpiresAt</c>); zamanı istemci verir, boş bırakılırsa kayıt
    /// süresizdir. Süresi dolan kayıt silinmez ama okunamaz ve yazılamaz; <c>POST /{id}/expire</c> kaydı hemen
    /// sonlandırır.
    /// </summary>
    public static ResourceBuilder<TEntity> Expiring<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Kayda sabit bir ömür verir: <c>ExpiresAt</c> oluşturulurken sunucu tarafından hesaplanır ve istek
    /// gövdesinden yazılamaz.
    /// </summary>
    /// <param name="lifetime">Ömür; derleme zamanında okunabilmesi için <c>TimeSpan.FromMinutes(15)</c> gibi sabit bir ifade olmalıdır.</param>
    /// <param name="sliding">Her başarılı yazmada süre yeniden başlasın mı (sepet gibi kullanıldıkça yaşayan kayıtlar için).</param>
    public static ResourceBuilder<TEntity> Expiring<TEntity>(this ResourceBuilder<TEntity> builder, TimeSpan lifetime, bool sliding = false)
        where TEntity : class => builder;

    /// <summary>
    /// Oluşturma ve geçiş uçlarını <c>Idempotency-Key</c> başlığıyla tekrara karşı korur: aynı anahtarla
    /// yeniden gönderilen istek işlemi tekrarlamaz, ilk isteğin yanıtını döndürür.
    /// </summary>
    /// <param name="required">Başlık zorunlu mu; zorunluysa eksik olduğunda 400 döner.</param>
    public static ResourceBuilder<TEntity> Idempotent<TEntity>(this ResourceBuilder<TEntity> builder, bool required = false)
        where TEntity : class => builder;

    /// <summary>
    /// Kaynağın sahip olduğu işlemlerin toplu biçimlerini ekler: <c>POST /batch-get</c>,
    /// <c>/batch-create</c>, <c>/batch-update</c> ve <c>/batch-delete</c>. Her öğeye tekil ucun kuralları
    /// aynen uygulanır.
    /// </summary>
    /// <param name="atomic">
    /// <c>true</c> ise ya hepsi ya hiçbiri: bir öğe başarısızsa hiçbir şey kaydedilmez ve yanıt o öğenin
    /// hatasıdır. <c>false</c> ise geçerli öğeler kaydedilir ve yanıt 207 ile öğe başına sonuç döner.
    /// </param>
    /// <param name="maxItems">Bir istekte kabul edilen en çok öğe.</param>
    public static ResourceBuilder<TEntity> Batch<TEntity>(
        this ResourceBuilder<TEntity> builder,
        bool atomic = true,
        int maxItems = HaldusBatch.DefaultMaxItems)
        where TEntity : class => builder;

    /// <summary>Verilen standart işlemlerin endpoint'lerini üretmez.</summary>
    public static ResourceBuilder<TEntity> Without<TEntity>(this ResourceBuilder<TEntity> builder, params CrudOperation[] operations)
        where TEntity : class => builder;

    /// <summary>Yalnızca verilen standart işlemlerin endpoint'lerini üretir.</summary>
    public static ResourceBuilder<TEntity> Only<TEntity>(this ResourceBuilder<TEntity> builder, params CrudOperation[] operations)
        where TEntity : class => builder;
}
