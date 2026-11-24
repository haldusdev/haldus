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
    private const string GuidType = "global::System.Guid";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>Özel tipi olmayan ama skaler sayılan tipler ve sıralanabilir olup olmadıkları.</summary>
    private static readonly Dictionary<string, SearchKind> KnownScalarTypes = new()
    {
        ["System.Guid"] = SearchKind.Equality,
        ["System.DateTimeOffset"] = SearchKind.Range,
        ["System.DateOnly"] = SearchKind.Range,
        ["System.TimeOnly"] = SearchKind.Range,
        ["System.TimeSpan"] = SearchKind.Range,
    };

    public static bool IsCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } name,
            },
        }
        && ArchetypeOf(name.Identifier.ValueText) is not null;

    public static string? ArchetypeOf(string methodName) => methodName switch
    {
        "MapCrud" => "Crud",
        _ => null,
    };

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
        var diagnostics = new List<DiagnosticInfo>();

        string entityName;
        string? entityNamespace;
        INamedTypeSymbol? entity = null;

        if (type is null or IErrorTypeSymbol)
        {
            // Entity hiç yazılmamış: Haldus tamamını üretir ve projenin kök namespace'ine koyar.
            if (typeSyntax is not IdentifierNameSyntax identifier)
            {
                return null;
            }

            entityName = identifier.Identifier.ValueText;
            entityNamespace = null;
        }
        else
        {
            entityName = type.Name;
            entityNamespace = type.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
                ? containingNamespace.ToDisplayString()
                : string.Empty;

            if (type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsRecord: false, ContainingType: null } declared
                && IsPartialInSource(declared, cancellationToken))
            {
                entity = declared;
            }
            else
            {
                diagnostics.Add(new DiagnosticInfo("HALDUS001", location, new EquatableArray<string>([entityName])));
            }
        }

        var properties = new List<PropertyModel>();
        var declaredTraitMembers = new List<string>();
        var traitMemberNames = new HashSet<string>(
            TraitCatalog.For(traits).SelectMany(definition => definition.Members).Select(member => member.Name));
        IPropertySymbol? id = null;

        foreach (var property in entity is null ? [] : PublicInstanceProperties(entity))
        {
            if (property.Name == "Id")
            {
                id = property;
            }
            else if (traitMemberNames.Contains(property.Name))
            {
                // Trait alanını kullanıcı kendi yazdıysa yeniden üretilmez; istek DTO'larına da girmez.
                declaredTraitMembers.Add(property.Name);
            }
            else if (TryClassify(property.Type, out var kind, out var valueType))
            {
                properties.Add(ToModel(property, kind, valueType));
            }
        }

        var idKind = IdKind.Guid;
        if (id is not null && !TryGetIdKind(id.Type, out idKind))
        {
            diagnostics.Add(new DiagnosticInfo(
                "HALDUS003", location, new EquatableArray<string>([entityName, id.Type.ToDisplayString()])));
        }

        var idType = id?.Type.ToDisplayString(TypeFormat) ?? GuidType;

        FilterModel? filter = null;
        var searchFields = new List<SearchField>();

        foreach (var (callName, call) in ChainReader.Nodes(invocation))
        {
            switch (callName)
            {
                case GenericNameSyntax { Identifier.ValueText: "Filterable", TypeArgumentList.Arguments: { Count: 1 } typeArguments }:
                    filter = ReadFilter(context.SemanticModel, typeArguments[0], cancellationToken);
                    break;
                case { Identifier.ValueText: "Searchable" }:
                    ReadSearchFields(
                        callName, call, entityName,
                        SearchCandidates(idType, idKind, properties, traits),
                        searchFields, diagnostics);
                    break;
            }
        }

        return new ResourceModel(
            archetype,
            entityName,
            entityNamespace,
            EntityDeclared: entity is not null,
            idType,
            idKind,
            IdDeclared: id is not null,
            properties.ToEquatableArray(),
            traits,
            operations,
            declaredTraitMembers.ToEquatableArray(),
            filter,
            searchFields.ToEquatableArray(),
            chain,
            diagnostics.ToEquatableArray(),
            location);
    }

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

    private static IEnumerable<IPropertySymbol> PublicInstanceProperties(INamedTypeSymbol type)
    {
        var hierarchy = new Stack<INamedTypeSymbol>();
        for (var current = type; current is { SpecialType: not SpecialType.System_Object }; current = current.BaseType)
        {
            hierarchy.Push(current);
        }

        var seen = new HashSet<string>();
        foreach (var level in hierarchy)
        {
            foreach (var property in level.GetMembers().OfType<IPropertySymbol>())
            {
                if (property is { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public, GetMethod: not null }
                    && seen.Add(property.Name))
                {
                    yield return property;
                }
            }
        }
    }

    /// <summary>Tip skaler ise hangi arama koşulunu aldığını ve nullable sarmalayıcısı çıkarılmış adını verir.</summary>
    private static bool TryClassify(ITypeSymbol type, out SearchKind kind, out string valueType)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        valueType = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (type.TypeKind == TypeKind.Enum)
        {
            kind = SearchKind.Equality;
            return true;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                kind = SearchKind.String;
                return true;
            case SpecialType.System_Boolean:
                kind = SearchKind.Equality;
                return true;
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_DateTime:
                kind = SearchKind.Range;
                return true;
            default:
                return KnownScalarTypes.TryGetValue(type.ToDisplayString(), out kind);
        }
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

    private static PropertyModel ToModel(IPropertySymbol property, SearchKind searchKind, string valueType)
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
            NullableType: AsNullable(type),
            IsWritable: property.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false },
            isNonNullableReference,
            isNonNullableValue,
            searchKind,
            valueType,
            attributes.ToEquatableArray());
    }

    private static string AsNullable(string type) => type.EndsWith("?", StringComparison.Ordinal) ? type : type + "?";

    private static FilterModel? ReadFilter(SemanticModel semanticModel, TypeSyntax syntax, CancellationToken cancellationToken)
    {
        if (semanticModel.GetTypeInfo(syntax, cancellationToken).Type is not INamedTypeSymbol { TypeKind: not TypeKind.Error } filter)
        {
            return null;
        }

        var properties = PublicInstanceProperties(filter)
            .Where(property => property.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false })
            .Where(property => TryClassify(
                property.Type is IArrayTypeSymbol array ? array.ElementType : property.Type, out _, out _))
            .Select(property => new FilterProperty(
                property.Name,
                AsNullable(property.Type.ToDisplayString(TypeFormat)),
                IsArray: property.Type is IArrayTypeSymbol));

        return new FilterModel(filter.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), properties.ToEquatableArray());
    }

    /// <summary>Aranabilecek alanlar: kimlik, kullanıcının skaler alanları ve etkin trait'lerin açık alanları.</summary>
    private static Dictionary<string, SearchField> SearchCandidates(
        string idType,
        IdKind idKind,
        List<PropertyModel> properties,
        Traits traits)
    {
        var candidates = new Dictionary<string, SearchField>
        {
            ["Id"] = new("Id", idKind == IdKind.Guid ? SearchKind.Equality : SearchKind.Range, idType, IsNullable: false),
        };

        foreach (var property in properties)
        {
            candidates[property.Name] = new SearchField(
                property.Name, property.SearchKind, property.ValueType, IsNullable: !property.IsNonNullable);
        }

        foreach (var member in TraitCatalog.For(traits).SelectMany(definition => definition.Members))
        {
            if (member.Search is not null)
            {
                candidates[member.Name] = member.Search;
            }
        }

        return candidates;
    }

    private static void ReadSearchFields(
        SimpleNameSyntax callName,
        InvocationExpressionSyntax call,
        string entityName,
        Dictionary<string, SearchField> candidates,
        List<SearchField> fields,
        List<DiagnosticInfo> diagnostics)
    {
        if (call.ArgumentList.Arguments.Count == 0)
        {
            diagnostics.Add(new DiagnosticInfo(
                "HALDUS006", LocationInfo.From(callName.GetLocation()), new EquatableArray<string>([entityName])));
            return;
        }

        foreach (var argument in call.ArgumentList.Arguments)
        {
            // Beklenen biçim: item => item.Name
            var fieldName = argument.Expression is LambdaExpressionSyntax { Body: MemberAccessExpressionSyntax member }
                ? member.Name.Identifier.ValueText
                : null;

            if (fieldName is not null && candidates.TryGetValue(fieldName, out var field))
            {
                if (fields.All(existing => existing.Name != field.Name))
                {
                    fields.Add(field);
                }
            }
            else
            {
                diagnostics.Add(new DiagnosticInfo(
                    "HALDUS005",
                    LocationInfo.From(argument.GetLocation()),
                    new EquatableArray<string>([argument.Expression.ToString(), entityName])));
            }
        }
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
}
