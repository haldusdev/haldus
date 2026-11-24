using Haldus.Generators.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Haldus.Generators.Model;

/// <summary>Tek bir <c>Map*</c> çağrısından okunan her şey.</summary>
internal sealed record ResourceModel(
    string Archetype,
    string EntityName,
    string? Namespace,
    bool EntityDeclared,
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
    EquatableArray<AttributeModel> ValidationAttributes)
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
