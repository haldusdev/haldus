using System.Globalization;
using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Haldus.Generators.Parsing;

/// <summary><c>app.MapCrud&lt;Product&gt;()</c> biçimindeki çağrıları ve onları izleyen zinciri okur.</summary>
internal static class MapCallParser
{
    private const string MapExtensionsType = "Haldus.HaldusEndpointRouteBuilderExtensions";
    private const string ValidationAttributeType = "System.ComponentModel.DataAnnotations.ValidationAttribute";
    private const string RequiredAttributeType = "System.ComponentModel.DataAnnotations.RequiredAttribute";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly HashSet<string> ScalarTypeNames =
    [
        "System.Guid",
        "System.DateTimeOffset",
        "System.DateOnly",
        "System.TimeOnly",
        "System.TimeSpan",
    ];

    public static bool IsCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } name,
            },
        }
        && ArchetypeOf(name.Identifier.ValueText) is not null;

    public static ResourceModel? Parse(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var name = (GenericNameSyntax)((MemberAccessExpressionSyntax)invocation.Expression).Name;
        var typeSyntax = name.TypeArgumentList.Arguments[0];
        var type = context.SemanticModel.GetTypeInfo(typeSyntax, cancellationToken).Type;

        if (!IsHaldusCall(context.SemanticModel, invocation, type, cancellationToken))
        {
            return null;
        }

        var archetype = ArchetypeOf(name.Identifier.ValueText)!;
        var location = LocationInfo.From(name.GetLocation());
        var chain = ChainReader.Read(invocation);
        var (traits, operations) = ChainReader.Interpret(chain);

        if (type is null or IErrorTypeSymbol)
        {
            // Entity hiç yazılmamış: Haldus tamamını üretir.
            return typeSyntax is IdentifierNameSyntax identifier
                ? new ResourceModel(
                    archetype,
                    identifier.Identifier.ValueText,
                    Namespace: null,
                    EntityDeclared: false,
                    IdType: "global::System.Guid",
                    IdKind.Guid,
                    IdDeclared: false,
                    EquatableArray<PropertyModel>.Empty,
                    traits,
                    operations,
                    EquatableArray<string>.Empty,
                    chain,
                    EquatableArray<DiagnosticInfo>.Empty,
                    location)
                : null;
        }

        var entityNamespace = type.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
            ? containingNamespace.ToDisplayString()
            : string.Empty;

        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsRecord: false, ContainingType: null } entity
            || !IsPartialInSource(entity, cancellationToken))
        {
            return Invalid(archetype, type.Name, entityNamespace, chain, location, "HALDUS001", type.Name);
        }

        var properties = new List<PropertyModel>();
        var declaredTraitMembers = new List<string>();
        var traitMemberNames = new HashSet<string>(
            TraitCatalog.For(traits).SelectMany(definition => definition.Members).Select(member => member.Name));
        IPropertySymbol? id = null;

        foreach (var property in PublicInstanceProperties(entity))
        {
            if (property.Name == "Id")
            {
                id = property;
                continue;
            }

            // Trait alanını kullanıcı kendi yazdıysa yeniden üretilmez; istek DTO'larına da girmez.
            if (traitMemberNames.Contains(property.Name))
            {
                declaredTraitMembers.Add(property.Name);
                continue;
            }

            if (IsScalar(property.Type))
            {
                properties.Add(ToModel(property));
            }
        }

        var idKind = IdKind.Guid;
        if (id is not null && !TryGetIdKind(id.Type, out idKind))
        {
            return Invalid(
                archetype, entity.Name, entityNamespace, chain, location,
                "HALDUS003", entity.Name, id.Type.ToDisplayString());
        }

        return new ResourceModel(
            archetype,
            entity.Name,
            entityNamespace,
            EntityDeclared: true,
            IdType: id?.Type.ToDisplayString(TypeFormat) ?? "global::System.Guid",
            idKind,
            IdDeclared: id is not null,
            properties.ToEquatableArray(),
            traits,
            operations,
            declaredTraitMembers.ToEquatableArray(),
            chain,
            EquatableArray<DiagnosticInfo>.Empty,
            location);
    }

    public static string? ArchetypeOf(string methodName) => methodName switch
    {
        "MapCrud" => "Crud",
        _ => null,
    };

    private static bool IsHaldusCall(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        ITypeSymbol? entityType,
        CancellationToken cancellationToken)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
        var method = symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

        if (method is not null)
        {
            return method.ContainingType?.ToDisplayString() == MapExtensionsType;
        }

        // Entity henüz var olmadığında derleyici çağrıyı bağlayamayabilir; bu durumda isim yeterlidir.
        return entityType is null or IErrorTypeSymbol;
    }

    private static bool IsPartialInSource(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        if (type.DeclaringSyntaxReferences.Length == 0)
        {
            return false;
        }

        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(cancellationToken) is not ClassDeclarationSyntax declaration
                || !declaration.Modifiers.Any(modifier => modifier.ValueText == "partial"))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<IPropertySymbol> PublicInstanceProperties(INamedTypeSymbol entity)
    {
        var hierarchy = new Stack<INamedTypeSymbol>();
        for (var current = entity; current is { SpecialType: not SpecialType.System_Object }; current = current.BaseType)
        {
            hierarchy.Push(current);
        }

        var seen = new HashSet<string>();
        foreach (var type in hierarchy)
        {
            foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
            {
                if (property is { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public, GetMethod: not null }
                    && seen.Add(property.Name))
                {
                    yield return property;
                }
            }
        }
    }

    private static bool IsScalar(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return true;
        }

        return type.SpecialType switch
        {
            SpecialType.System_Boolean
                or SpecialType.System_Byte
                or SpecialType.System_Int16
                or SpecialType.System_Int32
                or SpecialType.System_Int64
                or SpecialType.System_Single
                or SpecialType.System_Double
                or SpecialType.System_Decimal
                or SpecialType.System_String
                or SpecialType.System_DateTime => true,
            _ => ScalarTypeNames.Contains(type.ToDisplayString()),
        };
    }

    private static bool TryGetIdKind(ITypeSymbol type, out IdKind kind)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Int32:
                kind = IdKind.Int32;
                return true;
            case SpecialType.System_Int64:
                kind = IdKind.Int64;
                return true;
            default:
                kind = IdKind.Guid;
                return type.ToDisplayString() == "System.Guid";
        }
    }

    private static PropertyModel ToModel(IPropertySymbol property)
    {
        var attributes = new List<AttributeModel>();
        var hasRequired = false;

        foreach (var attribute in property.GetAttributes())
        {
            if (attribute.AttributeClass is not { } attributeClass || !InheritsFrom(attributeClass, ValidationAttributeType))
            {
                continue;
            }

            var isRequired = attributeClass.ToDisplayString() == RequiredAttributeType;
            hasRequired |= isRequired;
            attributes.Add(new AttributeModel(Render(attribute, attributeClass), isRequired));
        }

        var isNonNullableReference = property.Type.IsReferenceType
            && property.NullableAnnotation == NullableAnnotation.NotAnnotated;
        var isNonNullableValue = property.Type.IsValueType
            && property.Type is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

        // Null olamayan referans alanı isteğe gelmezse veritabanına null yazılmasın diye zorunlu sayılır.
        if (isNonNullableReference && !hasRequired)
        {
            attributes.Insert(0, new AttributeModel($"global::{RequiredAttributeType}(AllowEmptyStrings = true)", IsRequired: true));
        }

        var type = property.Type.ToDisplayString(TypeFormat);

        return new PropertyModel(
            property.Name,
            type,
            NullableType: type.EndsWith("?", StringComparison.Ordinal) ? type : type + "?",
            IsWritable: property.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false },
            isNonNullableReference,
            isNonNullableValue,
            attributes.ToEquatableArray());
    }

    private static bool InheritsFrom(INamedTypeSymbol type, string baseTypeName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == baseTypeName)
            {
                return true;
            }
        }

        return false;
    }

    private static string Render(AttributeData attribute, INamedTypeSymbol attributeClass)
    {
        var arguments = attribute.ConstructorArguments
            .Select(RenderConstant)
            .Concat(attribute.NamedArguments.Select(argument => $"{argument.Key} = {RenderConstant(argument.Value)}"));

        var typeName = attributeClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var argumentList = string.Join(", ", arguments);

        return argumentList.Length == 0 ? typeName : $"{typeName}({argumentList})";
    }

    private static string RenderConstant(TypedConstant constant)
    {
        if (constant.Kind == TypedConstantKind.Array)
        {
            var elementType = ((IArrayTypeSymbol)constant.Type!).ElementType.ToDisplayString(TypeFormat);
            return $"new {elementType}[] {{ {string.Join(", ", constant.Values.Select(RenderConstant))} }}";
        }

        // Sonek olmadan yazılan 0 gibi bir double, kopyalanan attribute'ta int overload'ına bağlanır
        // ve kuralın anlamını değiştirir; bu yüzden sayısal tipler sonekleriyle yazılır.
        return constant.Value switch
        {
            double value => value.ToString("R", CultureInfo.InvariantCulture) + "D",
            float value => value.ToString("R", CultureInfo.InvariantCulture) + "F",
            long value => value.ToString(CultureInfo.InvariantCulture) + "L",
            ulong value => value.ToString(CultureInfo.InvariantCulture) + "UL",
            uint value => value.ToString(CultureInfo.InvariantCulture) + "U",
            _ => constant.ToCSharpString(),
        };
    }

    private static ResourceModel Invalid(
        string archetype,
        string entityName,
        string entityNamespace,
        EquatableArray<ChainCall> chain,
        LocationInfo location,
        string diagnosticId,
        params string[] arguments) =>
        new(
            archetype,
            entityName,
            entityNamespace,
            EntityDeclared: true,
            IdType: "global::System.Guid",
            IdKind.Guid,
            IdDeclared: false,
            EquatableArray<PropertyModel>.Empty,
            Traits.None,
            Operations.All,
            EquatableArray<string>.Empty,
            chain,
            new EquatableArray<DiagnosticInfo>([new DiagnosticInfo(diagnosticId, location, arguments.ToEquatableArray())]),
            location);
}
