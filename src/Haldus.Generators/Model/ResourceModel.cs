using Haldus.Generators.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Haldus.Generators.Model;

/// <summary>Tek bir <c>Map*</c> çağrısından okunan her şey.</summary>
internal sealed record ResourceModel(
    Archetype Archetype,
    string EntityName,
    string Namespace,
    bool EntityDeclared,
    string KeyName,
    string IdType,
    IdKind IdKind,
    bool IdDeclared,
    EquatableArray<PropertyModel> Properties,
    Traits Traits,
    Operations Operations,
    EquatableArray<string> DeclaredTraitMembers,
    FilterModel? Filter,
    EquatableArray<SearchFieldRequest> SearchRequests,
    EquatableArray<ReferenceDeclaration> ReferenceDeclarations,
    EquatableArray<ChainCall> Chain,
    EquatableArray<DiagnosticInfo> Diagnostics,
    LocationInfo Location)
{
    // Aşağıdaki üçü ayrıştırmada boştur; bağlama adımı, tüm kaynaklar bilindikten sonra doldurur.

    /// <summary>Aranabilir alanlar; istekler başka kaynaklardan gelen alanları da (ör. yabancı anahtar) görebilsin diye sonradan çözülür.</summary>
    public EquatableArray<SearchField> SearchFields { get; init; } = EquatableArray<SearchField>.Empty;

    /// <summary>Hedefinin anahtar tipi ve adı çözülmüş referanslar.</summary>
    public EquatableArray<ResolvedReference> References { get; init; } = EquatableArray<ResolvedReference>.Empty;

    /// <summary><c>.Sortable(...)</c> içinde sayılan, henüz doğrulanmamış alanlar.</summary>
    public EquatableArray<SearchFieldRequest> SortRequests { get; init; } = EquatableArray<SearchFieldRequest>.Empty;

    /// <summary>Liste ucunun sıralanabildiği alanlar; bağlama adımı doldurur.</summary>
    public EquatableArray<SearchField> SortFields { get; init; } = EquatableArray<SearchField>.Empty;

    /// <summary><c>.CreateOnly(...)</c> içinde sayılan alanlar; bağlama adımı ilgili alanları işaretler.</summary>
    public EquatableArray<SearchFieldRequest> CreateOnlyRequests { get; init; } = EquatableArray<SearchFieldRequest>.Empty;

    /// <summary><c>.NaturalKey(...)</c> ile gösterilen, henüz doğrulanmamış alan.</summary>
    public SearchFieldRequest? NaturalKeyRequest { get; init; }

    /// <summary>Kaydın doğal anahtarı; bağlama adımı doldurur.</summary>
    public NaturalKeyModel? NaturalKey { get; init; }

    /// <summary><c>.DerivedFrom&lt;T&gt;()</c> ile gösterilen kaynaklar.</summary>
    public EquatableArray<SourceDeclaration> DerivedFrom { get; init; } = EquatableArray<SourceDeclaration>.Empty;

    /// <summary>Kaydın üretilebildiği kaynaklar (<c>.CreatedFrom&lt;T&gt;()</c>); bağlama adımı doldurur.</summary>
    public EquatableArray<OriginModel> Origins { get; init; } = EquatableArray<OriginModel>.Empty;

    /// <summary>Bu kaynağın geçişlerinin doğurduğu kayıtlar; bağlama adımı doldurur.</summary>
    public EquatableArray<SpawnModel> Spawns { get; init; } = EquatableArray<SpawnModel>.Empty;

    /// <summary>Bu kaynaktan türeyen kaynakların tam adları; bağlama adımı doldurur.</summary>
    public EquatableArray<string> Derivations { get; init; } = EquatableArray<string>.Empty;

    /// <summary>Sunucunun ürettiği anahtarın biçimi (<c>.KeyStrategy(...)</c>).</summary>
    public KeyGeneration KeyGeneration { get; init; }

    /// <summary>Önekli anahtarın öneki: <c>ord</c>.</summary>
    public string? KeyPrefix { get; init; }


    /// <summary><c>MapDetail</c> ile bildirildiyse sahibi olan kaynağın adı ve (çözülebildiyse) tam tip adı.</summary>
    public string? DetailOfName { get; init; }

    public string? DetailOfType { get; init; }

    /// <summary>Detay, üst kaydın DTO'larına gömülmek yerine kendi uçlarına mı ayrıldı (<c>.AsSubresource()</c>).</summary>
    public bool IsSubresource { get; init; }

    /// <summary>Kaynağın adresi ebeveyninin grubuna göre mi kurulur (ayrı uçlu detaylar üst kaynağın grubuna eşlenir).</summary>
    public bool RouteRelativeToParent { get; init; }

    /// <summary>
    /// Gömülü bir detayda satırın sırasını tutan alanı kullanıcı kendi yazdı mı. Alan her durumda Haldus'a
    /// aittir: değeri istekteki sıradan gelir ve DTO'larda yer almaz.
    /// </summary>
    public bool PositionDeclared { get; init; }

    /// <summary>Entity <c>IComputed</c> uyguluyor mu; uyguluyorsa yazma uçları kaydetmeden önce hesaplatır.</summary>
    public bool IsComputed { get; init; }

    /// <summary>
    /// Entity'nin uyguladığı zincir sözleşmeleri (<c>IReversal</c>, <c>ISupersession</c>) ve
    /// <c>ICreatedFrom&lt;T&gt;</c> için kaynak tiplerinin tam adları. Uygulanan sözleşme kopyadan sonra çağrılır.
    /// </summary>
    public EquatableArray<string> Contracts { get; init; } = EquatableArray<string>.Empty;

    /// <summary><c>.Workflow&lt;TStatus&gt;()</c> ile bildirilen durum makinesi.</summary>
    public WorkflowModel? Workflow { get; init; }

    /// <summary><c>.Numbered(...)</c> ile bildirilen numaralama.</summary>
    public NumberingModel? Numbering { get; init; }

    /// <summary><c>.Expiring(...)</c> ile bildirilen son kullanma kuralı.</summary>
    public ExpiryModel? Expiry { get; init; }

    /// <summary>Kaydın, yalnızca bir kez gösterilen sırrı.</summary>
    public SecretModel? Secret { get; init; }

    /// <summary>Kayda oturumsuz erişim sağlayan token.</summary>
    public TokenModel? Token { get; init; }

    /// <summary>Kaydın ters çevrilebildiği durumlar; durum makinesi yoksa <c>null</c> (her zaman).</summary>
    public EquatableArray<string>? ReversibleIn { get; init; }

    /// <summary>Kaydın yerine geçilebildiği durumlar; durum makinesi yoksa <c>null</c> (her zaman).</summary>
    public EquatableArray<string>? SupersedableIn { get; init; }

    /// <summary>
    /// Durum modeli ve numaralama gibi bildirimlerin entity'ye eklediği, istek gövdesinden yazılamayan
    /// alanlar (<c>Status</c>, <c>Number</c>, <c>ExpiresAt</c>). Trait alanları gibi davranırlar.
    /// </summary>
    public EquatableArray<TraitMember> ExtraMembers { get; init; } = EquatableArray<TraitMember>.Empty;

    /// <summary><c>.Batch(...)</c> ile bildirilen toplu işlemler.</summary>
    public BatchModel? Batch { get; init; }

    /// <summary><c>MapTransition</c> ile bildirilen geçişler; bağlama adımı doldurur.</summary>
    public EquatableArray<TransitionModel> Transitions { get; init; } = EquatableArray<TransitionModel>.Empty;

    /// <summary>
    /// Ayrı uçlu bir detayda, sahibinin düzenlenebildiği durumlar. Detay sahibinin parçasıdır; sahibi
    /// düzenlenemezken detayı da yazılamaz.
    /// </summary>
    public OwnerGuardModel? OwnerGuard { get; init; }

    /// <summary>Yanıtta yer alan, Haldus'a ait alanlar: trait alanları ve durum modeli alanları.</summary>
    public IEnumerable<TraitMember> ResponseMembers
    {
        get
        {
            // Aynı alanı hem bir trait hem bir bildirim getirebilir (ör. Auditable bir işin CreatedAt alanı).
            var seen = new HashSet<string>();
            return TraitCatalog.ResponseMembers(Traits)
                .Concat(ExtraMembers.Where(member => member.InResponse))
                .Where(member => seen.Add(member.Name))
                .ToArray();
        }
    }

    /// <summary>Kullanıcıya ait kaynak <c>/me</c> altında mı sunuluyor.</summary>
    public bool OwnedUnderMe => Has(Traits.Owned) && !Has(Traits.OwnedPlainRoute);

    /// <summary>
    /// Kaynağın koleksiyon adresi: <c>products</c>, <c>me/carts</c>. Tek kayıtlı kaynak koleksiyon değildir;
    /// adresi tekildir. Bu kaynağın altına yerleşenler adreslerini bununla başlatır.
    /// </summary>
    public string CollectionRoute =>
        (OwnedUnderMe ? "me/" : string.Empty)
        + Haldus.Generators.Infrastructure.Naming.ToKebabCase(
            Archetype == Archetype.Singleton ? EntityName : Haldus.Generators.Infrastructure.Naming.Pluralize(EntityName));

    /// <summary>Kaydın durum alanının tipi: durum makinesinin enum'u veya bir işin sabit durumları.</summary>
    public string? StatusType => Workflow?.StatusType ?? (Archetype == Archetype.Job ? "global::Haldus.JobStatus" : null);

    /// <summary>Tekrara karşı korunabilecek bir uç var mı: oluşturma veya en az bir geçiş.</summary>
    public bool HasWrites => Has(Operations.Create) || Transitions.Length > 0;

    /// <summary>Bu kaynağın DTO'larına gömülü taşınan detayları; bağlama adımı doldurur.</summary>
    public EquatableArray<ResourceModel> Details { get; init; } = EquatableArray<ResourceModel>.Empty;

    /// <summary>Detay koleksiyonunun üst kayıttaki adı: <c>OrderItems</c>.</summary>
    public string CollectionName => Haldus.Generators.Infrastructure.Naming.Pluralize(EntityName);

    public bool HasErrors => Diagnostics.Length > 0;

    public ResolvedReference? Parent => References.FirstOrDefault(reference => reference.Kind == ReferenceKind.Parent);

    /// <summary>Adrese girmeyen ebeveyn; varsa kaydın görünürlüğünü ve kapsamını o belirler.</summary>
    public ResolvedReference? ShallowParent => References.FirstOrDefault(reference => reference.IsShallowParent);

    /// <summary>Detayın satırları üst kaydın güncelleme isteğiyle değil, tek tek komutlarla mı yazılıyor (<c>.AsCommands()</c>).</summary>
    public bool IsCommandWritten { get; init; }

    public ResolvedReference? SelfReference => References.FirstOrDefault(reference => reference.Kind == ReferenceKind.Self);

    public string QualifiedName => Namespace.Length == 0 ? $"global::{EntityName}" : $"global::{Namespace}.{EntityName}";

    public bool Has(Traits trait) => (Traits & trait) == trait;

    public bool Has(Operations operation) => (Operations & operation) == operation;
}

/// <summary>Sunucunun ürettiği anahtarın biçimi.</summary>
internal enum KeyGeneration
{
    /// <summary>Zamana göre sıralı GUID (v7); varsayılan.</summary>
    Sequential,

    /// <summary>Rastgele GUID (v4).</summary>
    Random,

    /// <summary>Önekli metin kimlik.</summary>
    Prefixed,
}

/// <summary>Kaydın doğal anahtarı: benzersizdir ve kayıt onunla da okunabilir.</summary>
/// <param name="RouteConstraint">Adresteki değerin rota kısıtı (<c>:int</c>, <c>:guid</c> veya boş).</param>
internal sealed record NaturalKeyModel(string PropertyName, string Type, string RouteConstraint);

internal enum IdKind
{
    Guid,
    Int32,
    Int64,
    String,
}

/// <summary>Kaynağın şekli: hangi standart işlemlerin var olduğunu ve nasıl adreslendiğini belirler.</summary>
internal enum Archetype
{
    /// <summary>Kendi kimliği ve yaşam döngüsü olan bağımsız entity.</summary>
    Crud,

    /// <summary>İstemci hiç yazmaz.</summary>
    ReadOnly,

    /// <summary>İstemci oluşturur; kayıt sonra değişmez.</summary>
    AppendOnly,

    /// <summary>Anahtarı istemci verir; anahtarla ekle-veya-güncelle.</summary>
    Store,

    /// <summary>Kod + ad taşıyan küçük referans listesi.</summary>
    Lookup,

    /// <summary>Kapsam başına tam bir kayıt; örtük olarak vardır.</summary>
    Singleton,

    /// <summary>Başka bir kaynağın sahip olduğu satır; kendi başına adreslenmez.</summary>
    Detail,

    /// <summary>İsteği aşan iş: oluşturulur, arka planda çalışır, durumu izlenir, iptal edilebilir.</summary>
    Job,
}

internal static class ArchetypeExtensions
{
    public static Operations BaseOperations(this Archetype archetype) => archetype switch
    {
        Archetype.ReadOnly => Operations.List | Operations.Get,
        Archetype.AppendOnly or Archetype.Job => Operations.List | Operations.Get | Operations.Create,
        Archetype.Store or Archetype.Lookup => Operations.List | Operations.Get | Operations.Update | Operations.Delete,
        Archetype.Singleton => Operations.Get | Operations.Update | Operations.Patch,
        _ => Operations.All,
    };

    /// <summary>Anahtar özelliğinin adı. Singleton'da anahtar entity'de görünmez.</summary>
    public static string KeyName(this Archetype archetype) => archetype switch
    {
        Archetype.Store => "Key",
        Archetype.Lookup => "Code",
        Archetype.Singleton => string.Empty,
        _ => "Id",
    };

    /// <summary>Anahtarı sunucu mu üretir, yoksa istemci mi verir.</summary>
    public static bool HasClientKey(this Archetype archetype) => archetype is Archetype.Store or Archetype.Lookup;
}

[Flags]
internal enum Traits
{
    None = 0,
    MultiTenant = 1,
    Auditable = 2,
    AuditUser = 4,
    SoftDeletable = 8,
    ConcurrencyChecked = 16,
    Archivable = 32,
    Idempotent = 64,
    IdempotencyRequired = 128,

    /// <summary>Kayıt onu oluşturan kullanıcıya aittir.</summary>
    Owned = 256,

    /// <summary>Kullanıcıya ait kaynak <c>/me</c> altına alınmaz; adresi değişmez.</summary>
    OwnedPlainRoute = 512,

    /// <summary>Kayıt serbest bir anahtar-değer çantası taşır.</summary>
    Metadata = 1024,

    /// <summary>Kayıtlar kapsamları içinde bir sıra tutar.</summary>
    Orderable = 2048,

    /// <summary>Kayıt kopyalanabilir.</summary>
    Cloneable = 4096,

    /// <summary>Yazmaların kaydetmeyen bir önizlemesi vardır.</summary>
    Previewable = 8192,

    /// <summary>Kayıt, ona işaret eden bir ters kayıtla nötrlenebilir.</summary>
    Reversible = 16384,

    /// <summary>Kaydın yerine, ona işaret eden yeni bir kayıt geçebilir.</summary>
    Supersedes = 32768,
}

/// <summary>
/// Aynı kaynağın iki kaydını birbirine bağlayan işlem: ters çevirme veya yerine geçme. Yeni kayıt eskisine,
/// eskisi yenisine işaret eder; eski kayıt bundan sonra kapanmıştır.
/// </summary>
/// <param name="Route">Ucun adı: <c>reverse</c>.</param>
/// <param name="Forward">Yeni kayıttaki, eskisine işaret eden alan: <c>ReversesId</c>.</param>
/// <param name="Back">Eski kayıttaki, yenisine işaret eden alan: <c>ReversedById</c>.</param>
/// <param name="Contract">Entity'nin, kopyayı düzeltmek için uygulayabileceği arayüz ve metodu.</param>
/// <param name="Terminal">Yeni kayıt zincirin sonu mu; ters kayıt yeniden ters çevrilemez, yerine geçen kayıt geçilebilir.</param>
internal sealed record ChainOperation(
    Traits Trait,
    string Route,
    string Pascal,
    string Forward,
    string Back,
    string Contract,
    string ContractMethod,
    string LockedDetail,
    bool Terminal)
{
    public static readonly ChainOperation Reverse = new(
        Traits.Reversible, "reverse", "Reverse", "ReversesId", "ReversedById", "IReversal", "Reverse", "ReversedDetail", Terminal: true);

    public static readonly ChainOperation Supersede = new(
        Traits.Supersedes, "supersede", "Supersede", "SupersedesId", "SupersededById", "ISupersession", "Supersede", "SupersededDetail", Terminal: false);

    public static IEnumerable<ChainOperation> For(ResourceModel resource) =>
        new[] { Reverse, Supersede }.Where(chain => resource.Has(chain.Trait));

    /// <summary>İşlemin yapılabildiği durumlar; kaynağın durum makinesi yoksa <c>null</c>.</summary>
    public EquatableArray<string>? AllowedIn(ResourceModel resource) =>
        Trait == Traits.Reversible ? resource.ReversibleIn : resource.SupersedableIn;

    public string IndexName(ResourceModel resource)
    {
        var name = $"UX_{Haldus.Generators.Infrastructure.Naming.Pluralize(resource.EntityName)}_{Forward}";
        return name.Length <= 63 ? name : name.Substring(0, 63);
    }
}

/// <summary>Üretilecek standart işlemler. Üye adları <c>Haldus.CrudOperation</c> ile aynıdır.</summary>
[Flags]
internal enum Operations
{
    None = 0,
    List = 1,
    Get = 2,
    Create = 4,
    Update = 8,
    Patch = 16,
    Delete = 32,
    All = List | Get | Create | Update | Patch | Delete,
}

internal sealed record PropertyModel(
    string Name,
    string Type,
    string NullableType,
    bool IsWritable,
    bool IsNonNullableReference,
    bool IsNonNullableValue,
    SearchKind SearchKind,
    string ValueType,
    EquatableArray<AttributeModel> ValidationAttributes,
    bool IsGenerated = false,
    bool IsEmbedded = false,
    bool IsCreateOnly = false)
{
    public bool IsNonNullable => IsNonNullableReference || IsNonNullableValue;

    /// <summary>Alan güncelleme isteklerinde yer alır mı: yazılabilir ve yalnızca oluştururken yazılan bir alan değil.</summary>
    public bool IsUpdatable => IsWritable && !IsCreateOnly;
}

internal enum ReferenceKind
{
    /// <summary>Başka bir kaynağa kimlikle referans: <c>.References&lt;T&gt;()</c>.</summary>
    Reference,

    /// <summary>Zorunlu ebeveyn; kaynak onun adresi altında yaşar: <c>.Under&lt;T&gt;()</c>.</summary>
    Parent,

    /// <summary>
    /// Kaydın üretildiği kayıt: <c>.CreatedFrom&lt;T&gt;()</c>. Alanı istekle yazılamaz; yalnızca üretme ucu doldurur.
    /// </summary>
    Source,

    /// <summary>Aynı kaynağa referans; ağaç oluşturur: <c>.Hierarchical()</c>.</summary>
    Self,
}

internal enum OnDeleteBehavior
{
    Restrict,
    Cascade,
    SetNull,
}

/// <summary>Zincirden okunan, henüz hedefi çözülmemiş referans bildirimi.</summary>
/// <param name="TargetType">Hedef tip çözülebildiyse tam adı; hedef henüz üretilmemiş bir entity ise <c>null</c>.</param>
/// <param name="TargetKeyType">Hedef bir Haldus kaynağı değilse <c>Id</c> özelliğinden okunan anahtar tipi.</param>
/// <param name="PropertyName">Kullanıcı yabancı anahtar özelliğini kendisi gösterdiyse adı.</param>
internal sealed record ReferenceDeclaration(
    ReferenceKind Kind,
    string TargetName,
    string? TargetType,
    string? TargetKeyType,
    string? PropertyName,
    OnDeleteBehavior OnDelete,
    bool Required,
    LocationInfo Location,
    bool OnDeleteExplicit = false)
{
    /// <summary>Hedef başka bir assembly'de (modülde) derlenmiş bir entity ise anahtar özelliğinin adı.</summary>
    public string? TargetKeyName { get; init; }

    /// <summary>Hedef bu derlemenin dışında mı; öyleyse hakkında bilinenler derlenmiş tipinden okunur.</summary>
    public bool IsExternal { get; init; }

    /// <summary>Dış hedef kiracıya bağlı mı (<c>IMultiTenant</c> uyguluyor mu).</summary>
    public bool TargetTenantScoped { get; init; }

    /// <summary><c>.CreatedFrom&lt;T&gt;(on: "...")</c> ile verilen, kaydı kendiliğinden üreten geçişin adı.</summary>
    public string? Trigger { get; init; }

    /// <summary><c>.Under&lt;T&gt;(shallow: true)</c>: ebeveyn zorunludur ama kaynak kendi adresinde kalır.</summary>
    public bool Shallow { get; init; }
}

/// <summary><c>.SecretOnce(...)</c> ve <c>.Rotatable(...)</c> ile bildirilen sır.</summary>
/// <param name="Recoverable">Sır, sunucu kullanabilsin diye şifrelenmiş olarak da saklanıyor mu.</param>
/// <param name="OverlapTicks">Döndürmeden sonra eski sırrın geçerli kaldığı süre; <c>null</c> ise hemen geçersiz olur.</param>
internal sealed record SecretModel(string Prefix, bool Recoverable, bool Rotatable, long? OverlapTicks);

/// <summary><c>.TokenAccess(...)</c> ile bildirilen token.</summary>
/// <param name="Operations">Token ile çalıştırılabilen geçişlerin ve aksiyonların adları.</param>
internal sealed record TokenModel(string Prefix, EquatableArray<string> Operations);

/// <summary><c>.DerivedFrom&lt;T&gt;()</c> ile gösterilen, henüz çözülmemiş kaynak.</summary>
internal sealed record SourceDeclaration(string Name, string? Type, LocationInfo Location);

/// <summary>Kaydın başka bir kayıttan üretilme biçimi; bağlama adımı, iki kaynak da bilindikten sonra doldurur.</summary>
/// <param name="ForeignKey">Üretilen kayıttaki, kaynağa işaret eden alan: <c>CartId</c>.</param>
/// <param name="SourceIncludes">Kaynağın, sözleşme okuyabilsin diye birlikte yüklenen satır koleksiyonları.</param>
/// <param name="CopiedProperties">İki kaynakta adı ve tipi aynı olan, kendiliğinden kopyalanan alanlar.</param>
/// <param name="Trigger">Kaydı kendiliğinden üreten, kaynağa ait geçişin adı; yoksa <c>null</c>.</param>
/// <param name="HasContract">Entity <c>ICreatedFrom&lt;TSource&gt;</c> uyguluyor mu.</param>
internal sealed record OriginModel(
    string SourceName,
    string SourceType,
    string SourceKeyName,
    string SourceKeyType,
    string SourceRouteConstraint,
    string ForeignKey,
    EquatableArray<string> SourceIncludes,
    EquatableArray<string> CopiedProperties,
    bool CopiesMetadata,
    string? Trigger,
    bool HasContract);

/// <summary>Bir geçişin doğurduğu kayıt: geçiş çalışınca hedefin kurucu metodu çağrılır.</summary>
/// <param name="Transition">Geçişin adı.</param>
/// <param name="TargetType">Üretilen entity'nin tam adı; uç sınıfı ve istek tipi bundan türetilir.</param>
/// <param name="NeedsTime">Hedefin ömrü sunucu tarafından hesaplanıyor mu.</param>
internal sealed record SpawnModel(string Transition, string TargetType, string TargetNamespace, string TargetName, string SourceName, bool NeedsTime);

/// <param name="TargetRoute">Hedef koleksiyonun rota parçası (ör. <c>projects</c>); <c>.Under&lt;T&gt;()</c> adresi kurarken kullanır.</param>
internal sealed record ResolvedReference(
    ReferenceKind Kind,
    string PropertyName,
    string PropertyType,
    bool IsRequired,
    string TargetType,
    string TargetName,
    string TargetKeyName,
    string TargetKeyType,
    string TargetRoute,
    OnDeleteBehavior OnDelete,
    bool IsExternal = false)
{
    /// <summary>
    /// Referans, adrese girmeyen bir ebeveyne mi (<c>.Under&lt;T&gt;(shallow: true)</c>). Öyleyse alan oluşturma
    /// isteğinde verilir, sonradan değişmez ve kaydın görünürlüğü ile silinmesi ebeveyni izler.
    /// </summary>
    public bool IsShallowParent { get; init; }
}

/// <summary>Zincirsiz bir <c>MapAssociation&lt;TLeft, TRight&gt;()</c> çağrısı; uçları henüz çözülmemiştir.</summary>
internal sealed record AssociationDeclaration(
    string LeftName,
    string? LeftType,
    string RightName,
    string? RightType,
    LocationInfo Location)
{
    /// <summary>
    /// Bağ geçerli kullanıcıya mı bağlı (<c>.ForCurrentUser()</c>). Öyleyse sol tip argümanı bir uç değil,
    /// üretilecek bağ entity'sinin adıdır.
    /// </summary>
    public bool ForCurrentUser { get; init; }

    /// <summary>Çağrının bulunduğu namespace; yazılmamış bir bağ entity'si buraya üretilir.</summary>
    public string CallNamespace { get; init; } = string.Empty;

    /// <summary>
    /// Kullanıcının bağ sınıfına (<c>ProductTag</c>) yazdığı skaler alanlar: bağın taşıdığı yük. Sınıf
    /// yazılmadıysa boştur.
    /// </summary>
    public EquatableArray<PropertyModel> Payload { get; init; } = EquatableArray<PropertyModel>.Empty;

    /// <summary>Kullanıcı bağ sınıfını kendisi de yazdı mı; yazdıysa erişim belirleyicisi onunkidir.</summary>
    public bool LinkDeclared { get; init; }

    /// <summary>Bağın durum makinesi (<c>.Workflow&lt;TStatus&gt;()</c>); yoksa <c>null</c>.</summary>
    public WorkflowModel? Workflow { get; init; }

    /// <summary>Kullanıcı bağ sınıfına <c>Status</c> alanını kendisi yazdı mı; yazdıysa yeniden üretilmez.</summary>
    public bool StatusDeclared { get; init; }

    /// <summary>Zincir okunurken bulunan, bildirimi geçersiz kılan sorunlar.</summary>
    public EquatableArray<string> Problems { get; init; } = EquatableArray<string>.Empty;
}

/// <summary>Geçerli kullanıcı ile bir kaynak arasındaki bağ (favori, yıldız): kullanıcı başına, kaynak başına bir kayıt.</summary>
/// <param name="LinkName">Üretilen bağ entity'sinin adı: <c>Favorite</c>.</param>
/// <param name="Declared">Kullanıcı bağ sınıfını kendisi de yazdı mı; yazdıysa erişim belirleyicisi onunkidir.</param>
internal sealed record UserLinkModel(string LinkName, string Namespace, bool Declared, ResourceModel Right)
{
    public string QualifiedLinkName => Namespace.Length == 0 ? $"global::{LinkName}" : $"global::{Namespace}.{LinkName}";

    public string RightKey => Right.EntityName + Right.KeyName;

    public string CollectionName => Haldus.Generators.Infrastructure.Naming.Pluralize(LinkName);

    public string Route => "me/" + Haldus.Generators.Infrastructure.Naming.ToKebabCase(CollectionName);
}

/// <summary>İki ucu da bilinen kaynaklara çözülmüş çoktan-çoğa bağ.</summary>
/// <param name="LeftOnDelete">Sol uç silindiğinde bağlara ne olacağı.</param>
/// <param name="RightOnDelete">Sağ uç silindiğinde bağlara ne olacağı.</param>
internal sealed record AssociationModel(
    ResourceModel Left,
    ResourceModel Right,
    OnDeleteBehavior LeftOnDelete,
    OnDeleteBehavior RightOnDelete)
{
    /// <summary>Bağ kayıtlarını tutan, Haldus'un ürettiği entity: <c>ProductTag</c>.</summary>
    public string LinkName => Left.EntityName + Right.EntityName;

    public string Namespace => Left.Namespace;

    public string QualifiedLinkName => Namespace.Length == 0 ? $"global::{LinkName}" : $"global::{Namespace}.{LinkName}";

    public string LeftKey => Left.EntityName + Left.KeyName;

    public string RightKey => Right.EntityName + Right.KeyName;

    /// <summary>Bağın taşıdığı, kullanıcının bağ sınıfına yazdığı alanlar.</summary>
    public EquatableArray<PropertyModel> Payload { get; init; } = EquatableArray<PropertyModel>.Empty;

    public bool HasPayload => Payload.Length > 0;

    public bool LinkDeclared { get; init; }

    /// <summary>Bağın durum makinesi; yoksa <c>null</c>.</summary>
    public WorkflowModel? Workflow { get; init; }

    public bool StatusDeclared { get; init; }

    /// <summary>Bağın durumunu değiştiren, gövdesiz geçişler.</summary>
    public EquatableArray<TransitionModel> Transitions { get; init; } = EquatableArray<TransitionModel>.Empty;

    /// <summary>Bağ okunduğunda kendi yanıtını mı döndürür (yükü veya durumu varsa), yoksa yalnızca var mıdır.</summary>
    public bool HasResponse => HasPayload || Workflow is not null;
}

/// <summary>Kaydın durum makinesi.</summary>
/// <param name="StatusType">Durum enum'unun tam adı.</param>
/// <param name="Initial">Yeni kaydın durumu (enum üyesinin adı).</param>
/// <param name="EditableIn">Güncellemeye izin verilen durumlar; <c>null</c> ise her durumda düzenlenebilir.</param>
/// <param name="DeletableIn">Silmeye izin verilen durumlar; <c>null</c> ise her durumda silinebilir.</param>
internal sealed record WorkflowModel(
    string StatusType,
    string Initial,
    EquatableArray<string>? EditableIn,
    EquatableArray<string>? DeletableIn);

/// <param name="Pattern">Numara deseni.</param>
/// <param name="Gapless">Numara kayıtla aynı transaction'da bir sayaç satırından mı alınır.</param>
/// <param name="AssignOn">Numaranın atandığı durum; <c>null</c> ise kayıt oluşturulurken atanır.</param>
internal sealed record NumberingModel(string Pattern, bool Gapless, string? AssignOn);

/// <param name="LifetimeTicks">Sunucunun verdiği ömür; <c>null</c> ise son kullanma zamanını istemci verir.</param>
/// <param name="Sliding">Her yazmada süre yeniden başlar mı.</param>
internal sealed record ExpiryModel(long? LifetimeTicks, bool Sliding);

/// <param name="Atomic">Bir öğe başarısızsa hiçbir şey kaydedilmez mi; değilse geçerli öğeler kaydedilir.</param>
/// <param name="MaxItems">Bir istekte kabul edilen en çok öğe.</param>
internal sealed record BatchModel(bool Atomic, int MaxItems);

/// <summary>Bir durum enum'unun üyesi.</summary>
internal sealed record StateReference(string Type, string Name);

/// <summary>
/// Bir <c>MapTransition</c> veya entity'ye bağlı <c>MapAction</c> çağrısı; sahibi ve durumları henüz
/// doğrulanmamıştır. Aksiyon, durum kontrolü ve durum ataması olmayan bir geçiştir.
/// </summary>
/// <param name="Name">Çağrıda açıkça verilen ad; gövdesiz biçimde zorunludur.</param>
/// <param name="NameIsConstant">Ad verildiyse derleme zamanında okunabildi mi.</param>
/// <param name="ImplementsTransition">İstek tipi kendi etkisini uyguluyor mu (<c>ITransition</c> veya <c>IEntityAction</c>).</param>
internal sealed record TransitionDeclaration(
    string OwnerName,
    string? OwnerType,
    string? Name,
    bool NameIsConstant,
    string? RequestName,
    string? RequestType,
    bool ImplementsTransition,
    EquatableArray<StateReference> From,
    StateReference? To,
    bool StatesReadable,
    LocationInfo Location)
{
    public bool IsAction { get; init; }

    /// <summary>Geçiş bir kaynağın değil bir bağın (<c>MapAssociation</c>) builder'ında mı bildirildi.</summary>
    public bool IsLink { get; init; }

    /// <summary>Aksiyon kendi sonucunu döndürüyorsa o tipin tam adı; yoksa yanıt kaydın kendisidir.</summary>
    public string? ResponseType { get; init; }
}

/// <summary>Sahibine bağlanmış ve doğrulanmış bir geçiş.</summary>
/// <param name="Route">Adresin son parçası: <c>cancel</c>.</param>
/// <param name="PascalName">Metot ve işlem adlarında kullanılan biçim: <c>Cancel</c>.</param>
/// <param name="RegistryName">Çağrıda açıkça verilen ad; çalışma zamanındaki kayıt anahtarıdır.</param>
internal sealed record TransitionModel(
    string Route,
    string PascalName,
    string? RegistryName,
    string? RequestType,
    bool ImplementsTransition,
    EquatableArray<string> From,
    string? To)
{
    /// <summary>Durumu değiştirmeyen entity aksiyonu mu.</summary>
    public bool IsAction { get; init; }

    public string? ResponseType { get; init; }

    /// <summary>Üretilen eşleyicinin adı; modül kaydı ve uç üretimi aynı adı kullanır.</summary>
    public string MapperName => $"Map{PascalName}{(IsAction ? "Action" : "Transition")}";
}

/// <summary>Ayrı uçlu bir detayın yazılabilmesi için sahibinin bulunması gereken durumlar.</summary>
internal sealed record OwnerGuardModel(string StatusType, EquatableArray<string> EditableIn);

/// <summary><c>.Searchable(...)</c> içinde sayılan, henüz doğrulanmamış bir alan.</summary>
internal sealed record SearchFieldRequest(string? FieldName, string Expression, LocationInfo Location);

/// <summary>Haldus'un handler arayüzlerinden birini uygulayan, bağımlılık enjeksiyonuna kaydedilecek sınıf.</summary>
internal sealed record HandlerModel(string Implementation, EquatableArray<string> Services);

/// <summary>Bir <c>MapQuery</c> çağrısı: istek tipi sorgu parametrelerinden doldurulur.</summary>
internal sealed record QueryModel(
    string RequestName,
    string RequestNamespace,
    string RequestType,
    string ResponseType,
    bool HasParameterlessConstructor,
    EquatableArray<FilterProperty> Parameters,
    LocationInfo Location);

/// <summary><c>.Filterable&lt;TFilter&gt;()</c> ile bağlanan filtre sınıfı ve sorgu parametresi olacak özellikleri.</summary>
internal sealed record FilterModel(string Type, EquatableArray<FilterProperty> Properties);

internal sealed record FilterProperty(string Name, string NullableType, bool IsArray);

/// <summary>Bir alanın arama gövdesinde hangi koşul tipini aldığı.</summary>
internal enum SearchKind
{
    /// <summary>Metin: eşitlik, içerme, önek/sonek.</summary>
    String,

    /// <summary>Sıralanabilir değer (sayı, tarih): eşitlik ve aralık.</summary>
    Range,

    /// <summary>Yalnızca eşitlik (Guid, bool, enum).</summary>
    Equality,
}

/// <summary><c>.Searchable(...)</c> ile aranabilir ve sıralanabilir kılınan bir alan.</summary>
internal sealed record SearchField(string Name, SearchKind Kind, string ValueType, bool IsNullable);

/// <summary>Entity'den istek DTO'larına kopyalanan bir validasyon attribute'u.</summary>
internal sealed record AttributeModel(string Rendered, bool IsRequired);

/// <summary>Map çağrısını izleyen zincirdeki bir metot (trait, ilişki veya durum modeli bildirimi).</summary>
internal sealed record ChainCall(string Name, EquatableArray<string> TypeArguments, EquatableArray<string> Arguments);

internal sealed record DbContextModel(string Name, string Namespace, EquatableArray<string> MemberNames);

internal sealed record DiagnosticInfo(string Id, LocationInfo Location, EquatableArray<string> Arguments);

internal sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
{
    public static LocationInfo From(Location location) =>
        new(location.SourceTree?.FilePath ?? string.Empty, location.SourceSpan, location.GetLineSpan().Span);

    public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);
}
