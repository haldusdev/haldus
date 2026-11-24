using System.Linq.Expressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Haldus;

/// <summary>
/// Bir <c>Map*</c> çağrısının döndürdüğü builder. Trait ve ilişki zincir metotları çalışma zamanında
/// bir şey yapmaz; generator onları derleme zamanında okur. Builder aynı zamanda bir route group gibi
/// davranır, yani standart Minimal API çağrıları zincire eklenebilir.
/// </summary>
public sealed class ResourceBuilder<TEntity>(RouteGroupBuilder group, IEndpointRouteBuilder? root = null) : IEndpointRouteBuilder, IEndpointConventionBuilder
    where TEntity : class
{
    // Kaynağın adresinin dışında kalan ama onunla birlikte eşlenen uçlar (etiket bağları). Builder'a eklenen her
    // kural (yetkilendirme, hız sınırı) onlara da uygulanır; zincirdeki sırasından bağımsız olarak.
    private readonly List<IEndpointConventionBuilder> _companions = [];
    private readonly List<Action<IEndpointConventionBuilder>> _conventions = [];

    public RouteGroupBuilder Group { get; } = group;

    IServiceProvider IEndpointRouteBuilder.ServiceProvider => ((IEndpointRouteBuilder)Group).ServiceProvider;

    ICollection<EndpointDataSource> IEndpointRouteBuilder.DataSources => ((IEndpointRouteBuilder)Group).DataSources;

    // Aşağıdaki iki metot uzantı değil üye metottur: yalnızca böyle .Filterable<ProductFilter>() yazılabilir,
    // çünkü C# bir uzantı metodunda tip argümanlarının bir kısmını çıkarsayamaz.

    /// <summary>
    /// Liste endpoint'ine, sorgu parametrelerinden doldurulan bir filtre bağlar. Filtrenin herkese açık,
    /// yazılabilir skaler özellikleri camelCase sorgu parametresi olur.
    /// </summary>
    public ResourceBuilder<TEntity> Filterable<TFilter>()
        where TFilter : IFilter<TEntity>, new() => this;

    /// <summary>
    /// <c>POST /search</c> ucunu ekler: gövdede VE/VEYA ile birleştirilebilen tipli koşullar ve sıralama.
    /// Yalnızca burada sayılan alanlar aranabilir ve sıralanabilir.
    /// </summary>
    public ResourceBuilder<TEntity> Searchable(params Expression<Func<TEntity, object?>>[] fields) => this;

    /// <summary>
    /// Liste ucunu sıralanabilir yapar: <c>?sort=-createdAt,name</c> (baştaki eksi azalan sıradır). Yalnızca
    /// burada sayılan alanlara göre sıralanabilir; başka bir alan istenirse 400 döner.
    /// </summary>
    public ResourceBuilder<TEntity> Sortable(params Expression<Func<TEntity, object?>>[] fields) => this;

    /// <summary>
    /// Kaynağın, başka bir kaynağın alt tipi olduğunu bildirir (araç ve otomobil): entity sınıfı üst tipten türer
    /// (<c>public partial class Car : Vehicle</c>) ve kayıtları üst tipin tablosunda, onun anahtarı ve trait'leriyle
    /// durur. Alt tipin uçları yalnızca kendi kayıtlarını ve üst tipin alanlarıyla birlikte kendi alanlarını taşır;
    /// üst tipin uçları bütün türleri döndürür ve her kaydın türünü <c>kind</c> alanında söyler.
    /// </summary>
    public ResourceBuilder<TEntity> SubtypeOf<TBase>()
        where TBase : class => this;

    /// <summary>
    /// Kaydın bir tipi olduğunu bildirir (ürün ve ürün tipi, cihaz ve cihaz modeli): tipe zorunlu, kayıt oluşturulduktan
    /// sonra değişmeyen bir referans üretilir. Kayıtta boş bırakılan ve tipte aynı adla bulunan alanlar tipten
    /// devralınır; böylece tip varsayılanları taşır, kayıt yalnızca farklı olanı yazar. Kayıtları olan tip silinemez.
    /// </summary>
    public ResourceBuilder<TEntity> TypedBy<TType>()
        where TType : class => this;

    /// <summary>
    /// Kaydın, başka bir kaydın (oyuncunun) bir rolü olduğunu bildirir (kişinin müşteri ya da çalışan olması):
    /// oyuncuya zorunlu, değişmeyen bir referans üretilir ve bir oyuncunun bu rolden en çok bir kaydı olur (ikincisi
    /// 409). Rol, oyuncunun kimliğiyle de okunur: <c>GET /customers/by-person-id/{personId}</c>. Oyuncu silinince rolü
    /// de silinir.
    /// </summary>
    public ResourceBuilder<TEntity> RoleOf<TPlayer>(OnDelete onDelete = OnDelete.Cascade)
        where TPlayer : class => this;

    /// <summary>
    /// Sayılan alanlar yalnızca yazılır: isteklerde yer alırlar, hiçbir yanıtta, dışa aktarmada ve saklanan sürümde
    /// yer almazlar (kimlik numarası, banka hesabı gibi). Böyle bir alana göre aranamaz ve sıralanamaz.
    /// </summary>
    public ResourceBuilder<TEntity> Sensitive(params Expression<Func<TEntity, object?>>[] fields) => this;

    /// <summary>
    /// Sayılan metin alanları veritabanında şifreli saklanır ve, <see cref="Sensitive"/> alanlar gibi, yalnızca yazılır:
    /// hiçbir yanıtta yer almazlar. Sunucunun kendi kodu alanı açık haliyle okur. Şifreleme ASP.NET Data Protection
    /// iledir; anahtarların kalıcı ve sunucular arasında ortak olması uygulamanın sorumluluğundadır, yoksa veri
    /// okunamaz. Böyle bir alana göre aranamaz, sıralanamaz ve süzülemez.
    /// </summary>
    public ResourceBuilder<TEntity> Encrypted(params Expression<Func<TEntity, object?>>[] fields) => this;

    /// <summary>
    /// Sayılan metin alanları başka dillere çevrilebilir. Alanın kendisi kaynak dildeki metni taşımaya devam eder;
    /// çeviriler kayıtla birlikte saklanır ve yanıtta <c>translations</c> altında görünür. Bir dilin çevirileri
    /// <c>PUT /{id}/translations/{locale}</c> ile yazılır, <c>DELETE</c> ile kaldırılır. Okurken <c>?locale=tr</c>
    /// verilirse çevrilmiş alanlar o dildeki metinle döner (çevirisi olmayan alan kaynak dildeki metinle).
    /// </summary>
    public ResourceBuilder<TEntity> Translatable(params Expression<Func<TEntity, object?>>[] fields) => this;

    /// <summary>
    /// Sayılan alanlar yalnızca kayıt oluşturulurken yazılır: <c>POST</c> isteğinde yer alırlar, <c>PUT</c> ve
    /// <c>PATCH</c> isteklerinde yer almazlar ve sonradan değişmezler.
    /// </summary>
    public ResourceBuilder<TEntity> CreateOnly(params Expression<Func<TEntity, object?>>[] fields) => this;

    /// <summary>
    /// Kaydın başka bir kayıttan üretildiğini bildirir (sepetten sipariş, faturadan alacak dekontu): kaynağa
    /// işaret eden alan (<c>CartId</c>) üretilir ve <c>POST /orders/from-cart/{cartId}</c> ucu eklenir. Aynı adlı
    /// alanlar kaynaktan kopyalanır; entity <see cref="ICreatedFrom{TSource}"/> uygularsa gerisini o doldurur.
    /// </summary>
    /// <param name="on">
    /// Verilirse kayıt, kaynağın bu adlı geçişi çalıştığında ve onunla aynı transaction'da kendiliğinden üretilir.
    /// Derleme zamanında okunabilmesi için sabit olmalıdır.
    /// </param>
    /// <param name="onDelete">Kaynak silindiğinde bu kayda ne olacağı.</param>
    public ResourceBuilder<TEntity> CreatedFrom<TSource>(string? on = null, OnDelete onDelete = OnDelete.Restrict)
        where TSource : class => this;

    /// <summary>
    /// Kaynağın başka kayıtlardan hesaplandığını bildirir (hareketlerden stok seviyesi): her
    /// <typeparamref name="TSource"/> kaydı oluşturulduğunda, aynı transaction'da
    /// <see cref="IDerivation{TSource, TTarget}"/> uygulayan sınıfın çağrılır. Kaynak salt-okunur olmalıdır.
    /// </summary>
    public ResourceBuilder<TEntity> DerivedFrom<TSource>()
        where TSource : class => this;

    /// <summary>
    /// Alanı kaydın doğal anahtarı yapar: değeri (kiracı, sahip ve ebeveyn içinde) benzersizdir, çakışan yazma
    /// 409 döner ve kayıt bu değerle de okunabilir (<c>GET /coupons/by-code/{code}</c>). Diğer uçlar <c>Id</c>
    /// ile adreslenmeye devam eder.
    /// </summary>
    public ResourceBuilder<TEntity> NaturalKey(Expression<Func<TEntity, object?>> field) => this;

    /// <summary>
    /// Kayda, kendisine bağlı <typeparamref name="TChild"/> kayıtlarının sayısını tutan bir alan ekler
    /// (<c>CommentCount</c>). Alan yalnızca yanıtta görünür; bağlı bir kayıt eklendiğinde, silindiğinde veya başka
    /// bir kayda taşındığında baştan hesaplanır. <typeparamref name="TChild"/> bu kaynağa <c>Under</c>,
    /// <c>References</c> veya <c>AttachedTo</c> ile bağlı olmalıdır.
    /// </summary>
    /// <param name="into">Alanın adı; verilmezse alt kaynağın adından türetilir.</param>
    /// <param name="acrossOwners">
    /// Alt kayıtlar kullanıcılara aitse (<c>OwnedByCurrentUser</c>) ve bu kayıt o kullanıcılara ait değilse, sayı
    /// başkalarının göremediği kayıtlardan oluşur ve bu kaydı gören herkese açılır. Beğeni sayısı gibi bilerek
    /// istenen durumlarda <c>true</c> verilir; verilmezse bildirim derlenmez.
    /// </param>
    public ResourceBuilder<TEntity> Counts<TChild>(string? into = null, bool acrossOwners = false)
        where TChild : class => this;

    /// <summary>
    /// Kayda, kendisine bağlı <typeparamref name="TChild"/> kayıtlarının bir alanının toplamını tutan bir alan ekler
    /// (<c>PaidAmount</c>). Entity <see cref="IComputed"/> uyguluyorsa toplam değiştikçe <c>Compute()</c> yeniden
    /// çağrılır; böylece toplamdan türeyen bir durum (ödendi / kısmen ödendi) güncel kalır.
    /// </summary>
    /// <param name="field">Toplanan sayısal alan.</param>
    /// <param name="into">Alanın adı; verilmezse alt kaynağın ve alanın adından türetilir.</param>
    /// <param name="acrossOwners"><see cref="Counts{TChild}"/> ile aynı: kullanıcılara ait kayıtların ortak bir kayıtta toplanmasına izin verir.</param>
    public ResourceBuilder<TEntity> Sums<TChild>(Expression<Func<TChild, object?>> field, string? into = null, bool acrossOwners = false)
        where TChild : class => this;

    /// <summary>
    /// İşi (<c>MapJob</c>) bir dışa aktarma yapar: iş, <typeparamref name="TResource"/> kaynağının işi başlatana
    /// görünen bütün kayıtlarını bir dosyaya döker (<c>format</c>: <c>csv</c> veya <c>json</c>). Dosya, iş bitince
    /// <c>GET /{id}/content</c> ile indirilir. Mantığı Haldus üretir; handler yazılmaz.
    /// </summary>
    public ResourceBuilder<TEntity> Exports<TResource>()
        where TResource : class => this;

    /// <summary>
    /// İşi (<c>MapJob</c>) bir içe aktarma yapar: iş, isteğin <c>content</c> alanındaki satırları (başlıklı CSV veya
    /// JSON dizisi) <typeparamref name="TResource"/> kaynağında, tekil oluşturmanın bütün kurallarıyla oluşturur.
    /// Satırlar tek bir transaction içindedir: biri bile reddedilirse hiçbiri oluşturulmaz ve nedenleri işin
    /// <c>errors</c> alanında döner. Kaynak <c>.Batch()</c> bildirmiş olmalıdır.
    /// </summary>
    public ResourceBuilder<TEntity> Imports<TResource>()
        where TResource : class => this;

    /// <summary>
    /// Kaydı birkaç farklı kaynağa takılabilir yapar (ek, yorum, not): her çağrı bir hedef ekler. Kaynak her hedefin
    /// adresi altında sunulur (<c>/orders/{orderId}/comments</c>, <c>/invoices/{invoiceId}/comments</c>); her hedef
    /// için isteğe bağlı bir yabancı anahtar üretilir ve bir kayıtta tam olarak biri doludur. Kayıt, takıldığı kayıt
    /// görünürse görünür ve onunla birlikte silinir.
    /// </summary>
    public ResourceBuilder<TEntity> AttachedTo<TTarget>(OnDelete onDelete = OnDelete.Cascade)
        where TTarget : class => this;

    /// <summary>
    /// Bu kaynağın kayıtlarına dosya eklenebilir: <typeparamref name="TFile"/> (<c>MapFile</c> ile eşlenmiş bir
    /// kaynak) bu kaynağın adresi altında da sunulur. Dosya kaynağında <c>.AttachedTo&lt;TEntity&gt;()</c> yazmakla aynıdır.
    /// </summary>
    public ResourceBuilder<TEntity> Attachable<TFile>()
        where TFile : class => this;

    /// <summary>
    /// Bu kaynağın kayıtlarına yorum yazılabilir: <typeparamref name="TComment"/> bu kaynağın adresi altında da
    /// sunulur. Yorum kaynağında <c>.AttachedTo&lt;TEntity&gt;()</c> yazmakla aynıdır.
    /// </summary>
    public ResourceBuilder<TEntity> Commentable<TComment>()
        where TComment : class => this;

    /// <summary>
    /// Bu kaynağın kayıtları <typeparamref name="TTag"/> ile etiketlenebilir: <c>MapAssociation&lt;TEntity, TTag&gt;()</c>
    /// yazmakla aynıdır (<c>PUT</c> / <c>DELETE /{id}/tags/{tagId}</c>, etiketleri topluca değiştiren <c>PUT /{id}/tags</c>).
    /// </summary>
    public ResourceBuilder<TEntity> Taggable<TTag>()
        where TTag : class
    {
        if (root is null)
        {
            throw new InvalidOperationException(
                $"Taggable<{typeof(TTag).Name}>() maps endpoints outside the address of '{typeof(TEntity).Name}', " +
                "so it must be called on the builder a Map call returned.");
        }

        // Bağın uçları iki kaynağın adresi altındadır; kaynağın grubuna değil köke eşlenir ve kuralları buradan alır.
        var links = HaldusEndpointRegistry.MapAssociation<TEntity, TTag>(root);

        foreach (var convention in _conventions)
        {
            convention(links);
        }

        _companions.Add(links);
        return this;
    }

    /// <summary>
    /// Kaydı belirli bir dönemde geçerli kılar: <c>EffectiveFrom</c> (zorunlu) ve <c>EffectiveTo</c> (boşsa hâlâ
    /// geçerli) alanları üretilir. Bir kapsamda (kiracı, sahip, ebeveyn ve sayılan alanlar) iki kaydın dönemi
    /// çakışamaz (409); açık uçlu bir dönemin içinde başlayan yeni kayıt, öncekini kendi başlangıç gününde kapatır.
    /// Liste <c>?asOf=2026-01-01</c> ile o gün geçerli olan kayıtlara süzülür.
    /// </summary>
    /// <param name="per">Dönemlerin ayrı ayrı tutulduğu alanlar (ör. para birimi başına bir fiyat).</param>
    public ResourceBuilder<TEntity> EffectiveDated(params Expression<Func<TEntity, object?>>[] per) => this;

    /// <summary>
    /// Başka bir kaynağa kimlikle referans verir: yabancı anahtar alanı (ör. <c>CategoryId</c>) üretilir ve
    /// yazılırken hedefin var olduğu doğrulanır.
    /// </summary>
    /// <param name="onDelete">Hedef silindiğinde bu kayda ne olacağı.</param>
    /// <param name="required">Referans zorunlu mu.</param>
    public ResourceBuilder<TEntity> References<TTarget>(OnDelete onDelete = OnDelete.Restrict, bool required = true)
        where TTarget : class => this;

    /// <summary>Yabancı anahtar alanını kendin yazdıysan onu gösterirsin; zorunluluğu alanın tipinden okunur.</summary>
    public ResourceBuilder<TEntity> References<TTarget>(
        Expression<Func<TEntity, object?>> foreignKey,
        OnDelete onDelete = OnDelete.Restrict)
        where TTarget : class => this;

    /// <summary>
    /// Kaynağı bir ebeveynin adresi altına yerleştirir (<c>/projects/{projectId}/tasks</c>). Kaynak yine
    /// kendi kimliğine ve kurallarına sahiptir; ebeveyn istek gövdesinden değil adresten gelir.
    /// </summary>
    /// <param name="onDelete">Ebeveyn silindiğinde bu kayda ne olacağı.</param>
    /// <param name="shallow">
    /// Kaynak kendi adresinde kalsın mı (<c>/tasks/{id}</c>). Ebeveyn yine zorunludur ve kaydın görünürlüğünü
    /// belirler, ama adresten değil oluşturma isteğinin gövdesinden gelir ve sonradan değişmez; liste, ebeveynler
    /// arasında döner ve <c>?projectId=</c> ile süzülür.
    /// </param>
    public ResourceBuilder<TEntity> Under<TParent>(OnDelete onDelete = OnDelete.Cascade, bool shallow = false)
        where TParent : class => this;

    /// <summary>
    /// Kayıtları bir ağaca dizer: isteğe bağlı <c>ParentId</c> alanı üretilir ve bir düğümün kendi altına
    /// taşınması engellenir.
    /// </summary>
    public ResourceBuilder<TEntity> Hierarchical() => this;

    /// <summary>
    /// <typeparamref name="TValue"/> tipindeki alanları gömülü değer nesnesi sayar (para, adres gibi):
    /// kendi tablosu ve kimliği olmaz, sahibinin kolonlarında saklanır ve DTO'larda iç içe nesne olarak görünür.
    /// </summary>
    public ResourceBuilder<TEntity> Embeds<TValue>() => this;

    /// <summary>
    /// Bu kaynağın sahip olduğu satırları bildirir (sipariş → sipariş satırı). Satır kendi başına bir kaynak
    /// değildir: bu kaynak silinince gider ve onun görünürlüğünü devralır. Varsayılan olarak bu kaynağın
    /// istek ve yanıtlarında gömülü taşınır; <see cref="DetailBuilder{TEntity, TDetail}.AsSubresource"/> ile
    /// kendi uçlarına ayrılır.
    /// </summary>
    public DetailBuilder<TEntity, TDetail> MapDetail<TDetail>()
        where TDetail : class
    {
        // Satır ayrı uçlara sahipse onlar bu kaynağın grubuna, yani adresinin altına eşlenir.
        HaldusEndpointRegistry.MapIfRegistered<TDetail>(Group);
        return new DetailBuilder<TEntity, TDetail>();
    }

    /// <summary>
    /// Kayda bir durum makinesi ekler: <c>Status</c> alanı üretilir, istek gövdesinden yazılamaz ve yalnızca
    /// <c>MapTransition</c> ile bildirilen geçişlerle değişir.
    /// </summary>
    /// <param name="initial">Yeni kaydın durumu.</param>
    public ResourceBuilder<TEntity> Workflow<TStatus>(TStatus initial = default)
        where TStatus : struct, Enum => this;

    /// <summary>
    /// Kaydın, adım adım hazırlanıp sonunda tek seferde uygulanan düzenleme oturumlarını açar (verilmiş bir siparişin
    /// düzenlenmesi gibi): <c>POST /{id}/edits</c> bir küme başlatır, <c>PATCH /{id}/edits/{editId}</c> kısmi güncelleme
    /// gövdesiyle aynı biçimde değişiklik ekler, <c>GET …/preview</c> uygulanırsa kaydın nasıl olacağını gösterir,
    /// <c>POST …/commit</c> uygular, <c>DELETE</c> vazgeçer. Uygulama, kaydın kısmi güncellemesinin bütün kurallarından
    /// geçer; yalnızca <c>EditableIn</c> kuralının yerini burada sayılan durumlar alır.
    /// </summary>
    /// <param name="states">
    /// Kaydın bir değişiklik kümesiyle düzenlenebildiği durumlar. Kaynağın durum makinesi varsa sayılmaları zorunludur.
    /// </param>
    public ResourceBuilder<TEntity> ChangeSets(params Enum[] states) => this;

    /// <summary>
    /// Kayda, asıl durumundan bağımsız ilerleyen ek bir durum ekseni ekler (siparişin kendi durumunun yanında ödeme
    /// ve sevkiyat durumu gibi). Alan, durum tipinin adını taşır (<c>PaymentStatus</c>), istek gövdesinden yazılamaz
    /// ve yalnızca durumları o tipten olan <c>MapTransition</c> geçişleriyle değişir. <c>EditableIn</c> ve
    /// <c>DeletableIn</c> kuralları yalnızca asıl duruma (<c>Workflow</c>) bakar.
    /// </summary>
    /// <param name="initial">Yeni kaydın bu eksendeki durumu.</param>
    public ResourceBuilder<TEntity> Axis<TStatus>(TStatus initial = default)
        where TStatus : struct, Enum => this;

    /// <summary>Güncellemeye (<c>PUT</c>, <c>PATCH</c>) yalnızca sayılan durumlarda izin verir; diğerlerinde 409 döner.</summary>
    public ResourceBuilder<TEntity> EditableIn(params Enum[] states) => this;

    /// <summary>Silmeye yalnızca sayılan durumlarda izin verir; diğerlerinde 409 döner.</summary>
    public ResourceBuilder<TEntity> DeletableIn(params Enum[] states) => this;

    /// <summary>
    /// Gövdesiz bir geçiş ekler: <c>POST /{id}/{name}</c>. Kayıt <paramref name="from"/> durumlarından
    /// birindeyse <paramref name="to"/> durumuna geçer; değilse 409 döner.
    /// </summary>
    public RouteHandlerBuilder MapTransition(string name, StateSet from, Enum to) =>
        HaldusTransitionRegistry.Map<TEntity>(Group, name, apply: null);

    /// <inheritdoc cref="MapTransition(string, StateSet, Enum)"/>
    /// <param name="apply">Durum değişmeden önce kayıt üzerinde çalışır.</param>
    public RouteHandlerBuilder MapTransition(string name, StateSet from, Enum to, Action<TEntity> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        return HaldusTransitionRegistry.Map<TEntity>(Group, name, (entity, _, _) =>
        {
            apply(entity);
            return Task.CompletedTask;
        });
    }

    /// <inheritdoc cref="MapTransition(string, StateSet, Enum)"/>
    /// <param name="apply">Durum değişmeden önce kayıt üzerinde çalışır; bağımlılıklarını isteğin servis sağlayıcısından alır.</param>
    public RouteHandlerBuilder MapTransition(
        string name,
        StateSet from,
        Enum to,
        Func<TEntity, IServiceProvider, CancellationToken, Task> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return HaldusTransitionRegistry.Map(Group, name, apply);
    }

    /// <summary>
    /// İstek gövdesi alan bir geçiş ekler; adresi istek tipinin adından türetilir
    /// (<c>CancelOrder</c> → <c>POST /{id}/cancel</c>). İş mantığı ya istek tipindedir
    /// (<see cref="ITransition{TEntity}"/>) ya da Haldus'un bulduğu bir
    /// <see cref="ITransitionHandler{TEntity, TRequest}"/> sınıfındadır.
    /// </summary>
    /// <param name="name">Adresin son parçası; verilmezse istek tipinin adından türetilir.</param>
    public RouteHandlerBuilder MapTransition<TRequest>(StateSet from, Enum to, string? name = null)
        where TRequest : class =>
        HaldusTransitionRegistry.Map<TEntity, TRequest>(Group, name, apply: null);

    /// <inheritdoc cref="MapTransition{TRequest}(StateSet, Enum, string?)"/>
    /// <param name="apply">Durum değişmeden önce kayıt üzerinde çalışır.</param>
    public RouteHandlerBuilder MapTransition<TRequest>(StateSet from, Enum to, Action<TEntity, TRequest> apply, string? name = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(apply);

        return HaldusTransitionRegistry.Map<TEntity, TRequest>(Group, name, (entity, request, _, _) =>
        {
            apply(entity, request);
            return Task.CompletedTask;
        });
    }

    /// <inheritdoc cref="MapTransition{TRequest}(StateSet, Enum, string?)"/>
    /// <param name="apply">Durum değişmeden önce kayıt üzerinde çalışır; bağımlılıklarını isteğin servis sağlayıcısından alır.</param>
    public RouteHandlerBuilder MapTransition<TRequest>(
        StateSet from,
        Enum to,
        Func<TEntity, TRequest, IServiceProvider, CancellationToken, Task> apply,
        string? name = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(apply);
        return HaldusTransitionRegistry.Map(Group, name, apply);
    }

    // Entity aksiyonları. Bu metotlar, bir kaynağın builder'ında çağrıldığında, aynı adlı ve entity'ye bağlı
    // olmayan uzantı metotlarının (app.MapAction<TRequest>()) yerine geçer: builder üzerindeki MapAction her
    // zaman o kaynağın bir kaydı üzerinde çalışır.

    /// <summary>
    /// Bir kayıt üzerinde çalışan, durumunu değiştirmeyen bir işlem ekler: <c>POST /{id}/{name}</c>. Haldus
    /// kaydı bulur, sürümünü denetler, <paramref name="apply"/> çalıştıktan sonra kaydeder ve kaydın güncel
    /// halini döndürür.
    /// </summary>
    public RouteHandlerBuilder MapAction(string name, Action<TEntity> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        return HaldusTransitionRegistry.Map<TEntity>(Group, name, (entity, _, _) =>
        {
            apply(entity);
            return Task.CompletedTask;
        });
    }

    /// <inheritdoc cref="MapAction(string, Action{TEntity})"/>
    public RouteHandlerBuilder MapAction(string name, Func<TEntity, IServiceProvider, CancellationToken, Task> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return HaldusTransitionRegistry.Map(Group, name, apply);
    }

    /// <summary>
    /// İstek gövdesi alan bir entity aksiyonu ekler; adresi istek tipinin adından türetilir
    /// (<c>ChangeEmail</c> → <c>POST /{id}/change-email</c>). İş mantığı ya istek tipindedir
    /// (<see cref="IEntityAction{TEntity}"/>) ya da Haldus'un bulduğu bir
    /// <see cref="IEntityActionHandler{TEntity, TRequest}"/> sınıfındadır.
    /// </summary>
    /// <param name="name">Adresin son parçası; verilmezse istek tipinin adından türetilir.</param>
    public RouteHandlerBuilder MapAction<TRequest>(string? name = null)
        where TRequest : class =>
        HaldusTransitionRegistry.Map<TEntity, TRequest>(Group, name, apply: null);

    /// <inheritdoc cref="MapAction{TRequest}(string?)"/>
    /// <param name="apply">Kayıt üzerinde çalışır.</param>
    public RouteHandlerBuilder MapAction<TRequest>(Action<TEntity, TRequest> apply, string? name = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(apply);

        return HaldusTransitionRegistry.Map<TEntity, TRequest>(Group, name, (entity, request, _, _) =>
        {
            apply(entity, request);
            return Task.CompletedTask;
        });
    }

    /// <inheritdoc cref="MapAction{TRequest}(string?)"/>
    /// <param name="apply">Kayıt üzerinde çalışır; bağımlılıklarını isteğin servis sağlayıcısından alır.</param>
    public RouteHandlerBuilder MapAction<TRequest>(
        Func<TEntity, TRequest, IServiceProvider, CancellationToken, Task> apply,
        string? name = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(apply);
        return HaldusTransitionRegistry.Map(Group, name, apply);
    }

    /// <summary>
    /// Kendi sonucunu döndüren bir entity aksiyonu ekler; yanıt kaydın kendisi değil <typeparamref name="TResponse"/>
    /// olur. İş mantığı Haldus'un bulduğu bir <see cref="IEntityActionHandler{TEntity, TRequest, TResponse}"/>
    /// sınıfındadır. Kayıtta yapılan değişiklikler yine kaydedilir.
    /// </summary>
    /// <param name="name">Adresin son parçası; verilmezse istek tipinin adından türetilir.</param>
    public RouteHandlerBuilder MapAction<TRequest, TResponse>(string? name = null)
        where TRequest : class =>
        HaldusTransitionRegistry.Map<TEntity, TRequest, TResponse>(Group, name, apply: null);

    /// <inheritdoc cref="MapAction{TRequest, TResponse}(string?)"/>
    /// <param name="apply">Kayıt üzerinde çalışır ve sonucu üretir.</param>
    public RouteHandlerBuilder MapAction<TRequest, TResponse>(Func<TEntity, TRequest, TResponse> apply, string? name = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(apply);

        return HaldusTransitionRegistry.Map<TEntity, TRequest, TResponse>(
            Group, name, (entity, request, _, _) => Task.FromResult(apply(entity, request)));
    }

    /// <inheritdoc cref="MapAction{TRequest, TResponse}(string?)"/>
    /// <param name="apply">Kayıt üzerinde çalışır ve sonucu üretir; bağımlılıklarını isteğin servis sağlayıcısından alır.</param>
    public RouteHandlerBuilder MapAction<TRequest, TResponse>(
        Func<TEntity, TRequest, IServiceProvider, CancellationToken, Task<TResponse>> apply,
        string? name = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(apply);
        return HaldusTransitionRegistry.Map(Group, name, apply);
    }

    IApplicationBuilder IEndpointRouteBuilder.CreateApplicationBuilder() =>
        ((IEndpointRouteBuilder)Group).CreateApplicationBuilder();

    void IEndpointConventionBuilder.Add(Action<EndpointBuilder> convention) =>
        Apply(builder => builder.Add(convention));

    void IEndpointConventionBuilder.Finally(Action<EndpointBuilder> finallyConvention) =>
        Apply(builder => builder.Finally(finallyConvention));

    private void Apply(Action<IEndpointConventionBuilder> convention)
    {
        _conventions.Add(convention);
        convention(Group);

        foreach (var companion in _companions)
        {
            convention(companion);
        }
    }
}
