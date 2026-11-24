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
    EquatableArray<ChainCall> Chain,
    EquatableArray<DiagnosticInfo> Diagnostics,
    LocationInfo Location)
{
    public bool HasErrors => Diagnostics.Length > 0;
}

internal enum IdKind
{
    Guid,
    Int32,
    Int64,
}

internal sealed record PropertyModel(
    string Name,
    string Type,
    bool IsWritable,
    bool IsNonNullableReference,
    EquatableArray<string> ValidationAttributes);

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
