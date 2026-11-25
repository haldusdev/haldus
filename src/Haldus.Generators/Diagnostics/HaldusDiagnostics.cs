using Microsoft.CodeAnalysis;

namespace Haldus.Generators.Diagnostics;

internal static class HaldusDiagnostics
{
    private const string Category = "Haldus";

    // Her tanı, kılavuzda kendi başlığına bağlanır; derleme çıktısını okuyan (insan veya ajan) çözümü orada bulur.
    private const string Guide = "https://github.com/haldusdev/haldus/blob/main/docs/guide.md#";

    public static readonly DiagnosticDescriptor EntityMustBePartialClass = new(
        "HALDUS001",
        "Entity must be a partial class",
        "'{0}' must be declared as a top-level partial class so Haldus can generate its other half",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: Guide + "haldus001");

    public static readonly DiagnosticDescriptor EntityMappedMoreThanOnce = new(
        "HALDUS002",
        "Entity is mapped more than once",
        "'{0}' is already mapped by another Map call; an entity can be declared only once",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: Guide + "haldus002");

    public static readonly DiagnosticDescriptor UnsupportedIdType = new(
        "HALDUS003",
        "Unsupported key type",
        "'{0}' has type '{1}'; supported key types for this archetype are {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: Guide + "haldus003");

    public static readonly DiagnosticDescriptor InvalidCombination = new(
        "HALDUS007",
        "Declarations cannot be combined",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: Guide + "haldus007");

    public static readonly DiagnosticDescriptor DeclarationDetachedFromMapCall = new(
        "HALDUS004",
        "Declaration is not part of a Map call chain",
        "'{0}' must be chained directly onto the Map call; Haldus reads the chain at compile time and cannot see calls made on a variable",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: Guide + "haldus004");

    public static readonly DiagnosticDescriptor FieldCannotBeSearched = new(
        "HALDUS005",
        "Field cannot be searched",
        "'{0}' cannot be searched: write it as 'item => item.Property', where the property is the Id, a scalar property or a trait field of '{1}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: Guide + "haldus005");

    public static readonly DiagnosticDescriptor SearchableNeedsFields = new(
        "HALDUS006",
        "Searchable needs at least one field",
        "List the fields of '{0}' that may be searched and sorted, for example Searchable(item => item.Name); nothing is searchable by default",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: Guide + "haldus006");

    public static readonly DiagnosticDescriptor QueryRequestNeedsParameterlessConstructor = new(
        "HALDUS008",
        "Query request needs a parameterless constructor",
        "'{0}' must have a public parameterless constructor and settable properties so Haldus can fill it from the query string",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: Guide + "haldus008");

    public static readonly DiagnosticDescriptor ReferenceTargetUnknown = new(
        "HALDUS009",
        "Reference target cannot be resolved",
        "'{0}', which '{1}' refers to, is neither mapped with a Map call nor a declared entity type; a parent must be a mapped resource",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: Guide + "haldus009");

    public static readonly DiagnosticDescriptor InvalidDeclaration = new(
        "HALDUS010",
        "Declaration cannot be read at compile time or is invalid",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: Guide + "haldus010");

    public static DiagnosticDescriptor Get(string id) => id switch
    {
        "HALDUS010" => InvalidDeclaration,
        "HALDUS009" => ReferenceTargetUnknown,
        "HALDUS005" => FieldCannotBeSearched,
        "HALDUS006" => SearchableNeedsFields,
        "HALDUS007" => InvalidCombination,
        "HALDUS001" => EntityMustBePartialClass,
        "HALDUS002" => EntityMappedMoreThanOnce,
        "HALDUS003" => UnsupportedIdType,
        "HALDUS004" => DeclarationDetachedFromMapCall,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown Haldus diagnostic."),
    };
}
