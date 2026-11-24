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

    internal static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
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

    public static Archetype? ArchetypeOf(string methodName) => methodName switch
    {
        "MapCrud" => Archetype.Crud,
        "MapReadOnly" => Archetype.ReadOnly,
        "MapAppendOnly" => Archetype.AppendOnly,
        "MapStore" => Archetype.Store,
        "MapLookup" => Archetype.Lookup,
        "MapSingleton" => Archetype.Singleton,
        "MapDetail" => Archetype.Detail,
        "MapJob" => Archetype.Job,
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

        var archetype = ArchetypeOf(name.Identifier.ValueText)!.Value;
        var keyName = archetype.KeyName();
        var location = LocationInfo.From(name.GetLocation());
        var chain = ChainReader.Read(invocation);
        var (traits, operations, unsupportedOperations) = ChainReader.Interpret(chain, archetype.BaseOperations());
        var diagnostics = new List<DiagnosticInfo>();

        string entityName;
        string entityNamespace;
        INamedTypeSymbol? entity = null;

        if (type is null or IErrorTypeSymbol)
        {
            // Entity hiç yazılmamış: Haldus tamamını üretir. Çağrının bulunduğu namespace'e konur ki
            // çağrı yerinden her zaman görülebilsin (üst düzey Program.cs'te bu, global namespace'tir).
            if (typeSyntax is not IdentifierNameSyntax identifier)
            {
                return null;
            }

            entityName = identifier.Identifier.ValueText;
            entityNamespace = context.SemanticModel.GetEnclosingSymbol(invocation.SpanStart, cancellationToken)
                ?.ContainingNamespace is { IsGlobalNamespace: false } callSiteNamespace
                    ? callSiteNamespace.ToDisplayString()
                    : string.Empty;
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

        // Durum makinesi, numaralama ve son kullanma bildirimleri de trait'ler gibi entity'ye alan ekler.
        var lifecycle = LifecycleReader.Read(context.SemanticModel, invocation, entityName, diagnostics, cancellationToken);

        if (archetype == Archetype.Job)
        {
            lifecycle.Members.AddRange(LifecycleReader.JobMembers());
        }

        var properties = new List<PropertyModel>();
        var declaredTraitMembers = new List<string>();
        var traitMemberNames = new HashSet<string>(
            TraitCatalog.For(traits).SelectMany(definition => definition.Members)
                .Concat(lifecycle.Members)
                .Select(member => member.Name));
        IPropertySymbol? id = null;

        // .Embeds<T>() ile bildirilen tipler: bu tipteki alanlar skaler olmadıkları halde DTO'lara girer.
        var embeddedTypes = new Dictionary<string, string>();
        foreach (var (callName, _) in ChainReader.Nodes(invocation))
        {
            if (callName is GenericNameSyntax { Identifier.ValueText: "Embeds", TypeArgumentList.Arguments: { Count: 1 } values }
                && context.SemanticModel.GetTypeInfo(values[0], cancellationToken).Type is INamedTypeSymbol { TypeKind: not TypeKind.Error } value)
            {
                embeddedTypes[value.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)] = value.Name;
            }
        }

        var usedEmbeddedTypes = new HashSet<string>();

        foreach (var property in entity is null ? [] : PublicInstanceProperties(entity))
        {
            if (keyName.Length > 0 && property.Name == keyName)
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
            else if (EmbeddedTypeOf(property.Type) is { } embeddedType && embeddedTypes.ContainsKey(embeddedType))
            {
                usedEmbeddedTypes.Add(embeddedType);
                var embedded = ToModel(property, SearchKind.Equality, embeddedType) with { IsEmbedded = true };

                if (!embedded.IsNonNullable)
                {
                    diagnostics.Add(new DiagnosticInfo(
                        "HALDUS007",
                        location,
                        new EquatableArray<string>([$"'{entityName}.{property.Name}' is an embedded value and cannot be nullable"])));
                }

                properties.Add(embedded);
            }
        }

        foreach (var unused in embeddedTypes.Where(pair => !usedEmbeddedTypes.Contains(pair.Key)))
        {
            // Etkisiz bir bildirim sessizce geçmesin: alanın adı yanlış yazılmış veya tipi farklı olabilir.
            diagnostics.Add(new DiagnosticInfo(
                "HALDUS007",
                location,
                new EquatableArray<string>([$"Embeds<{unused.Value}> has no effect: '{entityName}' has no property of type '{unused.Value}'"])));
        }

        // Anahtarı istemcinin verdiği archetype'larda varsayılan tip metindir; diğerlerinde sunucu Guid üretir.
        var idKind = archetype.HasClientKey() ? IdKind.String : IdKind.Guid;
        if (id is not null && !TryGetIdKind(id.Type, archetype, out idKind))
        {
            diagnostics.Add(new DiagnosticInfo(
                "HALDUS003",
                location,
                new EquatableArray<string>([$"{entityName}.{keyName}", id.Type.ToDisplayString(), SupportedKeyTypes(archetype)])));
        }

        else if (id is not null && archetype == Archetype.Job && idKind != IdKind.Guid)
        {
            // İşin kimliği kuyruklarda taşınır ve Haldus tarafından atanır.
            diagnostics.Add(new DiagnosticInfo(
                "HALDUS003",
                location,
                new EquatableArray<string>([$"{entityName}.{keyName}", id.Type.ToDisplayString(), "Guid"])));
        }

        var idType = id?.Type.ToDisplayString(TypeFormat) ?? (archetype.HasClientKey() ? "string" : GuidType);

        if (archetype == Archetype.Lookup && properties.All(property => property.Name != "Name"))
        {
            properties.Insert(0, LookupName());
        }

        if (lifecycle.Expiry is { LifetimeTicks: null } && properties.All(property => property.Name != "ExpiresAt"))
        {
            // Son kullanma zamanını istemci veriyor: sıradan, yazılabilir bir alandır.
            properties.Add(LifecycleReader.ClientExpiry());
        }

        foreach (var message in Conflicts(archetype, traits, operations, unsupportedOperations, chain))
        {
            diagnostics.Add(new DiagnosticInfo("HALDUS007", location, new EquatableArray<string>([message])));
        }

        FilterModel? filter = null;
        var searchRequests = new List<SearchFieldRequest>();
        var references = new List<ReferenceDeclaration>();

        foreach (var (callName, call) in ChainReader.Nodes(invocation))
        {
            switch (callName)
            {
                case GenericNameSyntax { Identifier.ValueText: "Filterable", TypeArgumentList.Arguments: { Count: 1 } typeArguments }:
                    filter = ReadFilter(context.SemanticModel, typeArguments[0], cancellationToken);
                    break;
                case { Identifier.ValueText: "Searchable" }:
                    ReadSearchRequests(callName, call, entityName, searchRequests, diagnostics);
                    break;
                case GenericNameSyntax { Identifier.ValueText: "References", TypeArgumentList.Arguments: { Count: 1 } targets }:
                    references.Add(ReadReference(
                        ReferenceKind.Reference, targets[0], call, context.SemanticModel, OnDeleteBehavior.Restrict, cancellationToken));
                    break;
                case GenericNameSyntax { Identifier.ValueText: "Under", TypeArgumentList.Arguments: { Count: 1 } parents }:
                    references.Add(ReadReference(
                        ReferenceKind.Parent, parents[0], call, context.SemanticModel, OnDeleteBehavior.Cascade, cancellationToken));
                    break;
                case { Identifier.ValueText: "Hierarchical" }:
                    references.Add(new ReferenceDeclaration(
                        ReferenceKind.Self,
                        entityName,
                        TargetType: null,
                        TargetKeyType: null,
                        PropertyName: null,
                        OnDeleteBehavior.Restrict,
                        Required: false,
                        LocationInfo.From(callName.GetLocation())));
                    break;
            }
        }

        // Detayın sahibi, MapDetail'in çağrıldığı builder'ın tipinden okunur: ResourceBuilder<Order>.
        string? detailOfName = null;
        string? detailOfType = null;

        if (archetype == Archetype.Detail)
        {
            var receiver = ((MemberAccessExpressionSyntax)invocation.Expression).Expression;

            if (context.SemanticModel.GetTypeInfo(receiver, cancellationToken).Type
                is not INamedTypeSymbol { TypeArguments: { Length: 1 } owners })
            {
                return null;
            }

            detailOfName = owners[0].Name;
            detailOfType = owners[0] is IErrorTypeSymbol ? null : owners[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        return new ResourceModel(
            archetype,
            entityName,
            entityNamespace,
            EntityDeclared: entity is not null,
            keyName,
            idType,
            idKind,
            IdDeclared: id is not null,
            properties.ToEquatableArray(),
            traits,
            operations,
            declaredTraitMembers.ToEquatableArray(),
            filter,
            searchRequests.ToEquatableArray(),
            references.ToEquatableArray(),
            chain,
            diagnostics.ToEquatableArray(),
            location)
        {
            DetailOfName = detailOfName,
            DetailOfType = detailOfType,
            IsSubresource = chain.Any(call => call.Name == "AsSubresource"),
            Workflow = lifecycle.Workflow,
            Numbering = lifecycle.Numbering,
            Expiry = lifecycle.Expiry,
            ExtraMembers = lifecycle.Members.ToEquatableArray(),
            IsComputed = entity?.AllInterfaces.Any(candidate => candidate.ToDisplayString() == "Haldus.IComputed") == true,
        };
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
            // Archetype'lar uzantı metodudur; MapDetail ise üst kaynağın builder'ının üyesidir.
            return method.ContainingType?.ToDisplayString() == MapExtensionsType
                || method.ContainingType?.OriginalDefinition.ToDisplayString() == "Haldus.ResourceBuilder<TEntity>";
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

    internal static IEnumerable<IPropertySymbol> PublicInstanceProperties(INamedTypeSymbol type)
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
    internal static bool TryClassify(ITypeSymbol type, out SearchKind kind, out string valueType)
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

    private static bool TryGetIdKind(ITypeSymbol type, Archetype archetype, out IdKind kind)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                kind = IdKind.String;
                return archetype.HasClientKey();
            case SpecialType.System_Int32:
                kind = IdKind.Int32;
                return archetype != Archetype.Lookup;
            case SpecialType.System_Int64:
                kind = IdKind.Int64;
                return archetype != Archetype.Lookup;
            default:
                kind = IdKind.Guid;
                return archetype != Archetype.Lookup && type.ToDisplayString() == "System.Guid";
        }
    }

    private static string SupportedKeyTypes(Archetype archetype) => archetype switch
    {
        Archetype.Lookup => "string",
        Archetype.Store => "string, Guid, int and long",
        _ => "Guid, int and long",
    };

    /// <summary>Lookup'ın kullanıcı yazmadıysa üretilen ad alanı.</summary>
    private static PropertyModel LookupName() => new(
        "Name",
        "string",
        NullableType: "string?",
        IsWritable: true,
        IsNonNullableReference: true,
        IsNonNullableValue: false,
        SearchKind.String,
        "string",
        new EquatableArray<AttributeModel>(
        [
            new AttributeModel($"global::{RequiredAttributeType}", IsRequired: true),
            new AttributeModel("global::System.ComponentModel.DataAnnotations.MaxLengthAttribute(200)", IsRequired: false),
        ]),
        IsGenerated: true);

    /// <summary>
    /// Archetype'ın anlamıyla çelişen zincir bildirimlerini bulur. Bunlar sessizce yok sayılsaydı,
    /// örneğin liste işlemi kaldırılmış bir kaynakta arama ucu açık kalabilirdi.
    /// </summary>
    private static IEnumerable<string> Conflicts(
        Archetype archetype,
        Traits traits,
        Operations operations,
        Operations unsupportedOperations,
        EquatableArray<ChainCall> chain)
    {
        var name = $"Map{archetype}";

        if (unsupportedOperations != Operations.None)
        {
            yield return $"{name} does not have the operation(s) '{unsupportedOperations}'; they cannot be selected with Without or Only";
        }

        var forbidden = archetype switch
        {
            Archetype.ReadOnly => Traits.ConcurrencyChecked,
            Archetype.AppendOnly => Traits.ConcurrencyChecked | Traits.SoftDeletable,
            Archetype.Store or Archetype.Lookup => Traits.ConcurrencyChecked | Traits.SoftDeletable,
            Archetype.Singleton => Traits.ConcurrencyChecked | Traits.SoftDeletable | Traits.Archivable,

            // İşin yaşam döngüsü sabittir; istemci onu güncelleyemez veya silemez.
            Archetype.Job => Traits.ConcurrencyChecked | Traits.SoftDeletable | Traits.Archivable,
            _ => Traits.None,
        };

        foreach (var trait in new[] { Traits.ConcurrencyChecked, Traits.SoftDeletable, Traits.Archivable })
        {
            if ((traits & forbidden & trait) != 0)
            {
                yield return $"{trait} cannot be combined with {name}";
            }
        }

        foreach (var relation in chain.Select(call => call.Name).Distinct())
        {
            var allowed = relation switch
            {
                "References" => archetype != Archetype.Singleton,

                // Ebeveyn altında yaşamak ve ağaç oluşturmak, sunucunun anahtar ürettiği koleksiyonlar içindir.
                "Under" or "Hierarchical" => archetype is Archetype.Crud or Archetype.ReadOnly or Archetype.AppendOnly,
                _ => true,
            };

            if (!allowed)
            {
                yield return $"{relation} cannot be combined with {name}";
            }
        }

        if (chain.Count(call => call.Name == "Under") > 1)
        {
            yield return "A resource can live under only one parent; Under is declared more than once";
        }

        foreach (var declaration in chain.Select(call => call.Name).Distinct())
        {
            var allowed = declaration switch
            {
                // Durum makinesi, sunucunun anahtar ürettiği koleksiyonlar içindir; salt-okunur kaynakta
                // durum yalnızca geçişlerle değişir.
                "Workflow" => archetype is Archetype.Crud or Archetype.ReadOnly or Archetype.AppendOnly,

                // Numara ve ömür kayıt oluşturulurken verilir.
                "Numbered" or "Expiring" => archetype is Archetype.Crud or Archetype.AppendOnly,

                // Anahtarla ekle-veya-güncelle zaten tekrara dayanıklıdır; tek kaydın oluşturma ucu yoktur.
                "Idempotent" => archetype is Archetype.Crud or Archetype.ReadOnly or Archetype.AppendOnly or Archetype.Job,
                _ => true,
            };

            if (!allowed)
            {
                yield return $"{declaration} cannot be combined with {name}";
            }
        }

        foreach (var declaration in new[] { "Filterable", "Searchable" })
        {
            if (chain.All(call => call.Name != declaration))
            {
                continue;
            }

            if (archetype is Archetype.Singleton or Archetype.Lookup)
            {
                yield return $"{declaration} cannot be combined with {name}";
            }
            else if ((operations & Operations.List) == 0)
            {
                yield return $"{declaration} requires the List operation, which has been removed from this resource";
            }
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

    /// <summary>Alanın tipinin, nullable sarmalayıcısı ve işareti çıkarılmış tam adı.</summary>
    private static string EmbeddedTypeOf(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        return type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    internal static string AsNullable(string type) => type.EndsWith("?", StringComparison.Ordinal) ? type : type + "?";

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

    /// <summary>
    /// <c>.Searchable(...)</c> içinde sayılan alanları kaydeder. Alanlar burada doğrulanmaz, çünkü başka
    /// kaynaklara bağlı olarak üretilen alanlar (ör. yabancı anahtar) ancak bağlama adımında bilinir.
    /// </summary>
    private static void ReadSearchRequests(
        SimpleNameSyntax callName,
        InvocationExpressionSyntax call,
        string entityName,
        List<SearchFieldRequest> requests,
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
            requests.Add(new SearchFieldRequest(
                DirectMember(argument.Expression),
                argument.Expression.ToString(),
                LocationInfo.From(argument.GetLocation())));
        }
    }

    /// <summary><c>item => item.Name</c> biçimindeki bir ifadeden <c>Name</c>'i okur; başka biçimde <c>null</c> döner.</summary>
    private static string? DirectMember(ExpressionSyntax expression) =>
        expression is LambdaExpressionSyntax { Body: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax } member }
            ? member.Name.Identifier.ValueText
            : null;

    private static ReferenceDeclaration ReadReference(
        ReferenceKind kind,
        TypeSyntax targetSyntax,
        InvocationExpressionSyntax call,
        SemanticModel semanticModel,
        OnDeleteBehavior defaultOnDelete,
        CancellationToken cancellationToken)
    {
        var target = semanticModel.GetTypeInfo(targetSyntax, cancellationToken).Type as INamedTypeSymbol;
        var resolved = target is { TypeKind: not TypeKind.Error };

        string? propertyName = null;
        var onDelete = defaultOnDelete;
        var onDeleteExplicit = false;
        var required = true;

        foreach (var argument in call.ArgumentList.Arguments)
        {
            var text = argument.Expression.ToString();

            if (DirectMember(argument.Expression) is { } member)
            {
                propertyName = member;
            }
            else if (Enum.TryParse<OnDeleteBehavior>(text.Substring(text.LastIndexOf('.') + 1), out var behavior))
            {
                onDelete = behavior;
                onDeleteExplicit = true;
            }
            else if (text == "false")
            {
                required = false;
            }
        }

        // Hedef bir Haldus kaynağı değilse anahtar tipi Id özelliğinden okunur.
        var targetKey = resolved
            ? PublicInstanceProperties(target!).FirstOrDefault(property => property.Name == "Id")
            : null;

        return new ReferenceDeclaration(
            kind,
            resolved ? target!.Name : targetSyntax.ToString(),
            resolved ? target!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null,
            targetKey?.Type.ToDisplayString(TypeFormat),
            propertyName,
            onDelete,
            required,
            LocationInfo.From(targetSyntax.GetLocation()),
            onDeleteExplicit);
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
