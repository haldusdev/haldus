using Microsoft.CodeAnalysis;

namespace Haldus.Generators.Diagnostics;

internal static class HaldusDiagnostics
{
    private const string Category = "Haldus";

    public static readonly DiagnosticDescriptor EntityMustBePartialClass = new(
        "HALDUS001",
        "Entity must be a partial class",
        "'{0}' must be declared as a top-level partial class so Haldus can generate its other half",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EntityMappedMoreThanOnce = new(
        "HALDUS002",
        "Entity is mapped more than once",
        "'{0}' is already mapped by another Map call; an entity can be declared only once",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedIdType = new(
        "HALDUS003",
        "Unsupported Id type",
        "'{0}.Id' has type '{1}'; supported Id types are Guid, int and long",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DeclarationDetachedFromMapCall = new(
        "HALDUS004",
        "Declaration is not part of a Map call chain",
        "'{0}' must be chained directly onto the Map call; Haldus reads the chain at compile time and cannot see calls made on a variable",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor FieldCannotBeSearched = new(
        "HALDUS005",
        "Field cannot be searched",
        "'{0}' cannot be searched: write it as 'item => item.Property', where the property is the Id, a scalar property or a trait field of '{1}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SearchableNeedsFields = new(
        "HALDUS006",
        "Searchable needs at least one field",
        "List the fields of '{0}' that may be searched and sorted, for example Searchable(item => item.Name); nothing is searchable by default",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static DiagnosticDescriptor Get(string id) => id switch
    {
        "HALDUS005" => FieldCannotBeSearched,
        "HALDUS006" => SearchableNeedsFields,
        "HALDUS001" => EntityMustBePartialClass,
        "HALDUS002" => EntityMappedMoreThanOnce,
        "HALDUS003" => UnsupportedIdType,
        "HALDUS004" => DeclarationDetachedFromMapCall,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown Haldus diagnostic."),
    };
}
