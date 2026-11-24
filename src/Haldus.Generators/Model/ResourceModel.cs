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

    /// <summary>Başka bir kayıt silinmesini engelleyecek biçimde bu kaynağa referans veriyor mu.</summary>
    public bool IsReferenced { get; init; }

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

    /// <summary>Bu kaynağın DTO'larına gömülü taşınan detayları; bağlama adımı doldurur.</summary>
    public EquatableArray<ResourceModel> Details { get; init; } = EquatableArray<ResourceModel>.Empty;

    /// <summary>Detay koleksiyonunun üst kayıttaki adı: <c>OrderItems</c>.</summary>
    public string CollectionName => Haldus.Generators.Infrastructure.Naming.Pluralize(EntityName);

    public bool HasErrors => Diagnostics.Length > 0;

    public ResolvedReference? Parent => References.FirstOrDefault(reference => reference.Kind == ReferenceKind.Parent);

    public ResolvedReference? SelfReference => References.FirstOrDefault(reference => reference.Kind == ReferenceKind.Self);

    public string QualifiedName => Namespace.Length == 0 ? $"global::{EntityName}" : $"global::{Namespace}.{EntityName}";

    public bool Has(Traits trait) => (Traits & trait) == trait;

    public bool Has(Operations operation) => (Operations & operation) == operation;
}

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
}

internal static class ArchetypeExtensions
{
    public static Operations BaseOperations(this Archetype archetype) => archetype switch
    {
        Archetype.ReadOnly => Operations.List | Operations.Get,
        Archetype.AppendOnly => Operations.List | Operations.Get | Operations.Create,
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
    bool IsEmbedded = false)
{
    public bool IsNonNullable => IsNonNullableReference || IsNonNullableValue;
}

internal enum ReferenceKind
{
    /// <summary>Başka bir kaynağa kimlikle referans: <c>.References&lt;T&gt;()</c>.</summary>
    Reference,

    /// <summary>Zorunlu ebeveyn; kaynak onun adresi altında yaşar: <c>.Under&lt;T&gt;()</c>.</summary>
    Parent,

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
    bool OnDeleteExplicit = false);

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
    OnDeleteBehavior OnDelete);

/// <summary>Zincirsiz bir <c>MapAssociation&lt;TLeft, TRight&gt;()</c> çağrısı; uçları henüz çözülmemiştir.</summary>
internal sealed record AssociationDeclaration(
    string LeftName,
    string? LeftType,
    string RightName,
    string? RightType,
    LocationInfo Location);

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
}

/// <summary><c>.Searchable(...)</c> içinde sayılan, henüz doğrulanmamış bir alan.</summary>
internal sealed record SearchFieldRequest(string? FieldName, string Expression, LocationInfo Location);

/// <summary><c>IActionHandler</c> veya <c>IQueryHandler</c> uygulayan, bağımlılık enjeksiyonuna kaydedilecek sınıf.</summary>
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
