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

    /// <summary>
    /// Kayıtları onları oluşturan kullanıcıya bağlar: <c>OwnerId</c> alanı üretilir, kayıt oluşturulurken geçerli
    /// kullanıcı atanır ve herkes yalnızca kendi kayıtlarını görür. Kullanıcısı çözülemeyen istek 401 alır.
    /// </summary>
    /// <param name="underMe">
    /// Kaynak <c>/me</c> altında mı sunulsun (<c>/me/carts</c>). <c>false</c> ise adres değişmez (<c>/carts</c>);
    /// kayıtlar yine yalnızca sahibine görünür.
    /// </param>
    public static ResourceBuilder<TEntity> OwnedByCurrentUser<TEntity>(this ResourceBuilder<TEntity> builder, bool underMe = true)
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
    /// Kaydı yinelenen bir seri yapar (haftalık toplantı, aylık dönem). İlk oluş (<c>StartsAt</c>) ve kural
    /// (<c>RecurrenceFrequency</c>, <c>RecurrenceInterval</c>, <c>RecurrenceUntil</c>, <c>RecurrenceCount</c>) kaydın
    /// yazılabilir alanlarıdır; oluşlar saklanmaz, <c>GET /{id}/occurrences?from=&amp;to=</c> ile kuraldan hesaplanır.
    /// Tek bir oluş <c>PUT /{id}/occurrences/{start}</c> ile ertelenir veya iptal edilir, <c>DELETE</c> ile kurala
    /// geri döner.
    /// </summary>
    public static ResourceBuilder<TEntity> Recurring<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Kayıt değiştikçe olay yayınlar: <c>order.created</c>, <c>order.updated</c>, <c>order.deleted</c> ve her geçiş
    /// için <c>order.{geçiş}</c>. Olay, kaydın o andaki halini (yanıt biçiminde) taşır, kayıtla aynı kaydetmede
    /// bekleme tablosuna yazılır ve kayıt kalıcı olduktan sonra uygulamanın kaydettiği yayıncılara
    /// (<see cref="IHaldusEventPublisher"/>) sırayla teslim edilir. Teslimat en az bir kezdir: yayıncı hata
    /// fırlatırsa olay yeniden denenir.
    /// </summary>
    public static ResourceBuilder<TEntity> PublishesEvents<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Ölçüte uyan kayıtları topluca siler: <c>POST /purge?…&amp;force=true</c>. Ölçüt, kaynağın filtresidir
    /// (<c>Filterable</c>) ve liste ucundaki sorgu parametreleriyle verilir; en az biri zorunludur. <c>force</c>
    /// verilmezse hiçbir şey silinmez, yanıt yalnızca kaç kaydın silineceğini ve bir örneğini gösterir. Her kayıt
    /// tekil silmenin kurallarından geçer; biri bile silinemiyorsa hiçbiri silinmez. <c>Filterable</c> ve
    /// <c>Batch</c> ister.
    /// </summary>
    public static ResourceBuilder<TEntity> Purgeable<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Bitmiş bir işi aynı girdilerle yeniden çalıştırır: <c>POST /{id}/rerun</c> yeni bir iş kaydı oluşturur (202)
    /// ve onu, <c>RerunOfId</c> ile asıl işe bağlar. Bekleyen veya çalışan iş yeniden çalıştırılamaz (409).
    /// </summary>
    public static ResourceBuilder<TEntity> Rerunnable<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Bakiyeyi adlandırılmış kovalara böler (stokta "available", "reserved", "damaged" gibi). Her kova ayrı bir alan
    /// olur ve ayrı düzeltilir; <c>POST /{key}/move</c> bir miktarı bir kovadan ötekine tek işlemde taşır. Verilmezse
    /// bakiyenin tek bir kovası vardır: <c>quantity</c>.
    /// </summary>
    /// <param name="names">Kovaların adları; küçük harfli sözcükler, '-' ile ayrılır. İlki varsayılan kovadır.</param>
    public static ResourceBuilder<TEntity> Buckets<TEntity>(this ResourceBuilder<TEntity> builder, params string[] names)
        where TEntity : class => builder;

    /// <summary>Bakiyenin sıfırın altına düşmesine izin verir (açık hesap, eksi stok).</summary>
    public static ResourceBuilder<TEntity> AllowNegative<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Oluşturma ve geçiş uçlarını <c>Idempotency-Key</c> başlığıyla tekrara karşı korur: aynı anahtarla
    /// yeniden gönderilen istek işlemi tekrarlamaz, ilk isteğin yanıtını döndürür. Bakiyede (<c>MapBalance</c>)
    /// düzeltme, mutlak yazma ve taşıma uçlarını korur.
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

    /// <summary>
    /// Kayda serbest bir anahtar-değer çantası ekler (<c>Metadata</c>): değerler metindir ve Haldus anlamlarına
    /// bakmaz. <c>PATCH</c> çantayı birleştirir (gönderilen anahtar eklenir veya değişir, <c>null</c> gönderilen
    /// silinir); <c>PUT</c> tamamını değiştirir.
    /// </summary>
    public static ResourceBuilder<TEntity> Metadata<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Kaydın kopyalanmasını sağlar: <c>POST /{id}/clone</c>. Yazılabilir alanlar ve gömülü satırlar kopyaya taşınır;
    /// kimlik, durum, numara, zaman damgaları ve sahip yeni bir kayıtta olduğu gibi atanır. İstek, isteğe bağlı
    /// bir gövdeyle kopyadaki alanları değiştirebilir (benzersiz bir alanın yeni değeri böyle verilir).
    /// </summary>
    public static ResourceBuilder<TEntity> Cloneable<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Kayıtlara bir sıra verir: <c>Position</c> alanı üretilir (0'dan başlar, boşluksuzdur), liste varsayılan
    /// olarak bu sırayla döner ve <c>POST /{id}/move</c> kaydı taşır. Sıra, kaydın kapsamı içindedir: aynı kiracı,
    /// sahip ve ebeveyn (ağaçta aynı üst düğüm).
    /// </summary>
    public static ResourceBuilder<TEntity> Orderable<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Yazmaların önizlemesini ekler: <c>POST /preview</c> yeni bir kaydın, <c>POST /{id}/preview</c> bir
    /// güncellemenin sonucunu, doğrulayıp hesaplanan alanlarıyla döndürür ama kaydetmez. Sunucunun kaydederken
    /// atadığı alanlar (kimlik, numara, zaman damgaları) önizlemede boştur.
    /// </summary>
    public static ResourceBuilder<TEntity> Previewable<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Kaydın ters kaydını üretir: <c>POST /{id}/reverse</c>. Ters kayıt asıl kaydın kopyası olarak doğar ve ona
    /// işaret eder (<c>ReversesId</c>); entity <see cref="IReversal{TEntity}"/> uygularsa farkı (ör. tutarın
    /// işaretini) o yazar. Bir kayıt yalnızca bir kez ters çevrilir ve ters çevrildikten sonra değiştirilemez.
    /// </summary>
    /// <param name="states">
    /// Kaydın ters çevrilebildiği durumlar. Kaynağın bir durum makinesi varsa zorunludur: ters çevirme, kaydın
    /// düzenlenemediği durumlarda da çalışan bir yazmadır ve hangi durumlarda yapılabileceği açıkça söylenmelidir.
    /// </param>
    public static ResourceBuilder<TEntity> Reversible<TEntity>(this ResourceBuilder<TEntity> builder, params Enum[] states)
        where TEntity : class => builder;

    /// <summary>
    /// Kaydı sabit bir yaşam döngüsü olan bir belge yapar: taslak → gönder → iptal → düzelt. <c>Status</c> alanı
    /// (<see cref="DocumentStatus"/>) üretilir; taslak düzenlenir ve silinir, gönderilen belge kilitlidir.
    /// <c>POST /{id}/submit</c> ve <c>POST /{id}/cancel</c> kendiliğinden üretilir (iş mantığı gerekiyorsa aynı adla
    /// <c>MapTransition</c> yazılır ve onun yerine geçer); <c>POST /{id}/amend</c> iptal edilmiş belgenin kopyası olan,
    /// ona işaret eden (<c>AmendedFromId</c>) yeni bir taslak oluşturur. <c>Workflow</c> ile birlikte kullanılmaz.
    /// </summary>
    public static ResourceBuilder<TEntity> Document<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Kaydı ikinci bir kişinin onayına bağlar: <c>POST /{id}/approve</c>, <c>POST /{id}/reject</c> (isteğe bağlı
    /// gövde: <c>{ "note": "..." }</c>) ve kararların listesi <c>GET /{id}/approvals</c>. İçeriği son yazan kişi
    /// kendi yazdığına karar veremez; içerik değişince toplanan onaylar geçersiz olur; onaylanmış kayıt
    /// değiştirilemez, reddedilmiş kayıt düzenlenince yeniden karar bekler.
    /// </summary>
    /// <param name="levels">Kaydın onaylanması için gereken, birbirinden farklı kişilerin sırayla vereceği onay sayısı.</param>
    /// <param name="requiredFor">Kayıt onaylanmadan çalışmayan geçişlerin ve aksiyonların adları.</param>
    public static ResourceBuilder<TEntity> Approvable<TEntity>(this ResourceBuilder<TEntity> builder, int levels = 1, params string[] requiredFor)
        where TEntity : class => builder;

    /// <summary>
    /// Kaydın her yazmadan sonraki halini saklar: <c>GET /{id}/revisions</c> sürümleri listeler,
    /// <c>GET /{id}/revisions/{revisionId}</c> kaydın o andaki halini döndürür,
    /// <c>POST /{id}/revisions/{revisionId}/rollback</c> kaydın alanlarını ve gömülü satırlarını o sürüme geri yükler
    /// (bu da yeni bir sürümdür). Görüntüyü, kaydı hangi kod yazarsa yazsın, Haldus kaydederken alır.
    /// </summary>
    public static ResourceBuilder<TEntity> Revisioned<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Kaydı taslak ve yayın olarak ikiye ayırır. Kaynağın sıradan uçları taslağı okur ve yazar;
    /// <c>POST /{id}/publish</c> taslağın o andaki halini (alanları ve gömülü satırlarıyla) yayına alır,
    /// <c>POST /{id}/unpublish</c> yayından kaldırır. Yayındaki hal <c>GET /published</c> ve
    /// <c>GET /published/{id}</c> ile okunur; taslak sonradan değişse de bir sonraki yayına kadar aynı kalır.
    /// Yayındaki kayıt silinemez; önce yayından kaldırılır.
    /// </summary>
    public static ResourceBuilder<TEntity> Publishable<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Kayıtları, oluşturulduktan verilen süre sonra kendiliğinden siler (olay günlüğü, bildirim gibi biriken
    /// kayıtlar için). Silme arka plandaki bakım sırasında yapılır ve geri alınamaz; <c>CreatedAt</c> alanı
    /// kendiliğinden eklenir.
    /// </summary>
    /// <param name="period">Kaydın saklandığı süre; derleme zamanında okunabilen bir değer olmalıdır.</param>
    public static ResourceBuilder<TEntity> Retention<TEntity>(this ResourceBuilder<TEntity> builder, TimeSpan period)
        where TEntity : class => builder;

    /// <summary>
    /// Kullanıcıya ait kayıtların (bildirim, mesaj) okunup okunmadığını izler: <c>ReadAt</c> alanı üretilir,
    /// <c>POST /{id}/read</c> ve <c>/unread</c> kaydı işaretler, <c>POST /read-all</c> hepsini okundu yapar,
    /// <c>GET /unread-count</c> okunmamışları sayar ve liste <c>?unread=true</c> ile süzülür.
    /// <c>OwnedByCurrentUser</c> ile birlikte kullanılır.
    /// </summary>
    public static ResourceBuilder<TEntity> ReadTracking<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Kayda, alanlarını kiracının tanımladığı bir değer çantası ekler (<c>customFields</c>). Tanımlar kaynağın
    /// adresinde yönetilir: <c>GET /custom-fields</c>, <c>PUT</c> ve <c>DELETE /custom-fields/{key}</c>; her alanın
    /// bir türü (metin, sayı, evet/hayır, gün, seçenek) vardır ve zorunlu olabilir. Değerler her yazmada tanımlara
    /// göre doğrulanır: tanımı olmayan anahtar ve türüne uymayan değer 400 döner. <c>PATCH</c> çantayı birleştirir.
    /// </summary>
    public static ResourceBuilder<TEntity> CustomFields<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Kaynağa bir değişiklik akışı ekler: <c>GET /changes?since={imleç}</c>, imleçten sonra değişen kayıtların
    /// şimdiki halini ve silinenlerin kimliklerini döndürür; yanıttaki <c>next</c> bir sonraki isteğin imlecidir.
    /// </summary>
    public static ResourceBuilder<TEntity> ChangeFeed<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>
    /// Kaydın yerine geçen yeni bir kayıt üretir (düzeltme, yeniden planlama): <c>POST /{id}/supersede</c>. Yeni
    /// kayıt eskisinin kopyası olarak doğar, istekte gönderilen alanlar onun yerine geçer ve eskisine işaret eder
    /// (<c>SupersedesId</c>). Eski kayıt kalır ama artık değiştirilemez.
    /// </summary>
    /// <param name="states">
    /// Kaydın yerine geçilebildiği durumlar. Kaynağın bir durum makinesi varsa zorunludur: yerine geçme, kaydın
    /// düzenlenemediği durumlarda da çalışan bir yazmadır ve hangi durumlarda yapılabileceği açıkça söylenmelidir.
    /// </param>
    public static ResourceBuilder<TEntity> Supersedes<TEntity>(this ResourceBuilder<TEntity> builder, params Enum[] states)
        where TEntity : class => builder;

    /// <summary>
    /// Kayda, yalnızca bir kez gösterilen bir sır verir (API anahtarı, webhook imza sırrı). Sır sunucuda üretilir
    /// (<c>önek_…</c>), yalnızca oluşturma yanıtında (<c>secret</c>) görünür ve veritabanında özeti saklanır; listede
    /// son dört karakteri (<c>SecretHint</c>) gösterilir. Sunulan bir sırrın kaydı
    /// <see cref="HaldusSecretQueries.FindBySecretAsync"/> ile bulunur.
    /// </summary>
    /// <param name="prefix">Sırrın öneki: küçük harfle başlar, küçük harf ve rakamdan oluşur, en çok 12 karakterdir.</param>
    /// <param name="recoverable">
    /// Sır ayrıca şifrelenerek saklansın mı; sunucunun onu kendisinin kullanması gerekiyorsa (ör. gönderileri
    /// imzalamak). <see cref="HaldusSecretProtection.Reveal"/> ile açılır.
    /// </param>
    public static ResourceBuilder<TEntity> SecretOnce<TEntity>(this ResourceBuilder<TEntity> builder, string prefix, bool recoverable = false)
        where TEntity : class => builder;

    /// <summary>Sırrın döndürülmesini sağlar: <c>POST /{id}/rotate</c> yeni bir sır üretir ve onu bir kez gösterir.</summary>
    /// <param name="overlap">
    /// Eski sırrın geçerli kalacağı süre; verilmezse eski sır hemen geçersiz olur. Derleme zamanında okunabilmesi
    /// için <c>TimeSpan.FromHours(24)</c> gibi sabit bir ifade olmalıdır.
    /// </param>
    public static ResourceBuilder<TEntity> Rotatable<TEntity>(this ResourceBuilder<TEntity> builder, TimeSpan? overlap = null)
        where TEntity : class => builder;

    /// <summary>
    /// Kaydın, oturumu olmayan biri tarafından gizli bir token ile okunmasını sağlar (davet bağlantısı gibi):
    /// <c>GET /by-token</c>. Token oluşturma yanıtında bir kez gösterilir (<c>token</c>) ve isteklerde
    /// <c>X-Access-Token</c> başlığında taşınır. Token ile yalnızca burada sayılan geçişler ve aksiyonlar
    /// çalıştırılabilir (<c>POST /by-token/accept</c>); başka hiçbir yazma yapılamaz. Bu uçlar kimlik doğrulama
    /// istemez.
    /// </summary>
    /// <param name="prefix">Token'ın öneki; kuralları sır önekiyle aynıdır.</param>
    /// <param name="operations">Token ile çalıştırılabilen geçişlerin ve aksiyonların adları.</param>
    public static ResourceBuilder<TEntity> TokenAccess<TEntity>(this ResourceBuilder<TEntity> builder, string prefix, params string[] operations)
        where TEntity : class => builder;

    /// <summary>
    /// Kayda yalnızca token'ı ile ulaşılır (misafir sepeti gibi, sahibi bir kullanıcı olmayan kayıtlar için).
    /// <c>.TokenAccess()</c> ile birlikte yazılır: kimlikle çalışan hiçbir uç üretilmez (okuma, liste, güncelleme,
    /// silme, geçişlerin ve aksiyonların <c>/{id}/…</c> adresi, <c>/{id}/expire</c>); geriye oluşturma ile
    /// <c>/by-token</c> uçları kalır. Kaydın bütün geçişleri ve aksiyonları <c>TokenAccess</c>'te sayılmış olmalıdır.
    /// </summary>
    public static ResourceBuilder<TEntity> TokenOnly<TEntity>(this ResourceBuilder<TEntity> builder)
        where TEntity : class => builder;

    /// <summary>Sunucunun ürettiği anahtarın biçimini seçer; varsayılanı zamana göre sıralı GUID'dir.</summary>
    /// <param name="strategy">Anahtarın biçimi.</param>
    /// <param name="prefix">
    /// <see cref="Haldus.KeyStrategy.Prefixed"/> için önek (<c>"ord"</c> → <c>ord_01j9…</c>): küçük harfle başlar,
    /// küçük harf ve rakamdan oluşur, en çok 12 karakterdir. Derleme zamanında okunabilmesi için sabit olmalıdır.
    /// </param>
    public static ResourceBuilder<TEntity> KeyStrategy<TEntity>(
        this ResourceBuilder<TEntity> builder,
        KeyStrategy strategy,
        string? prefix = null)
        where TEntity : class => builder;

    /// <summary>Verilen standart işlemlerin endpoint'lerini üretmez.</summary>
    public static ResourceBuilder<TEntity> Without<TEntity>(this ResourceBuilder<TEntity> builder, params CrudOperation[] operations)
        where TEntity : class => builder;

    /// <summary>Yalnızca verilen standart işlemlerin endpoint'lerini üretir.</summary>
    public static ResourceBuilder<TEntity> Only<TEntity>(this ResourceBuilder<TEntity> builder, params CrudOperation[] operations)
        where TEntity : class => builder;
}
