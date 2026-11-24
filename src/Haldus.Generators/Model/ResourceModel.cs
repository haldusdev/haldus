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
    EquatableArray<SearchField> SearchFields,
    EquatableArray<ChainCall> Chain,
    EquatableArray<DiagnosticInfo> Diagnostics,
    LocationInfo Location)
{
    public bool HasErrors => Diagnostics.Length > 0;

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
    bool IsGenerated = false)
{
    public bool IsNonNullable => IsNonNullableReference || IsNonNullableValue;
}

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
