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

        // Dosya, içeriği ayrıca yüklenen bir MapCrud kaynağıdır.
        "MapFile" => Archetype.Crud,
        "MapReadOnly" => Archetype.ReadOnly,
        "MapAppendOnly" => Archetype.AppendOnly,
        "MapStore" => Archetype.Store,
        "MapBalance" => Archetype.Balance,
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

        if (archetype == Archetype.Job && (traits & Traits.Rerunnable) != 0)
        {
            // Yeniden çalıştırılan iş, kopyalandığı işe işaret eder.
            lifecycle.Members.Add(new TraitMember("RerunOfId", GuidType + "?", InResponse: true, Search: null));
        }

        BalanceModel? balance = null;

        if (archetype == Archetype.Balance)
        {
            // Kova bildirilmediyse bakiyenin tek bir miktarı vardır.
            var buckets = (lifecycle.Buckets.Count == 0 ? ["quantity"] : lifecycle.Buckets)
                .Select(bucket => new BucketModel(bucket, string.Concat(bucket.Split('-').Select(word => char.ToUpperInvariant(word[0]) + word.Substring(1)))))
                .ToList();

            if (buckets.FirstOrDefault(bucket => bucket.Property is "Key" or "TenantId" or "CreatedAt" or "UpdatedAt") is { } reserved)
            {
                diagnostics.Add(new DiagnosticInfo(
                    "HALDUS010", location, new EquatableArray<string>([$"The bucket '{reserved.Name}' would collide with the '{reserved.Property}' field of the balance; choose another name"])));
            }

            balance = new BalanceModel(buckets.ToEquatableArray(), lifecycle.AllowNegative);

            // Bakiyenin son ne zaman değiştiği bilinmelidir.
            traits |= Traits.Auditable;

            foreach (var bucket in buckets)
            {
                lifecycle.Members.Add(new TraitMember(
                    bucket.Property, "decimal", InResponse: true, new SearchField(bucket.Property, SearchKind.Range, "decimal", IsNullable: false)));
            }
        }
        else if (lifecycle.Buckets.Count > 0 || lifecycle.AllowNegative)
        {
            diagnostics.Add(new DiagnosticInfo(
                "HALDUS007", location, new EquatableArray<string>(["Buckets and AllowNegative describe a balance; declare them on a MapBalance call"])));
        }

        FileModel? file = null;

        if (name.Identifier.ValueText == "MapFile")
        {
            file = LifecycleReader.ReadFile(
                context.SemanticModel,
                invocation,
                (node, message) => diagnostics.Add(new DiagnosticInfo("HALDUS010", LocationInfo.From(node.GetLocation()), new EquatableArray<string>([message]))),
                cancellationToken);

            // Dosyanın durumu ve içeriği kendi sabit modelidir; kopyalanan veya başka bir kayıttan doğan kayıt içeriksiz kalırdı.
            foreach (var other in new[] { "Workflow", "Document", "SoftDeletable", "Cloneable", "Publishable", "Reversible", "Supersedes", "CreatedFrom" })
            {
                if (chain.Any(call => call.Name == other))
                {
                    diagnostics.Add(new DiagnosticInfo(
                        "HALDUS007", location, new EquatableArray<string>([$"MapFile cannot be combined with {other}: the content of a file is stored once, under its own fixed states"])));
                }
            }

            if (chain.Any(call => call.Name == "Retention"))
            {
                // Bakım kayıtları topluca siler; içerik depoda sahipsiz kalırdı.
                diagnostics.Add(new DiagnosticInfo(
                    "HALDUS007", location, new EquatableArray<string>(["MapFile cannot be combined with Retention: records are purged in bulk, which would leave their content behind in the storage"])));
            }

            // Yükleme bekleyen kayıtların ne zaman oluşturulduğu bilinmelidir.
            traits |= Traits.Auditable;
            lifecycle.Members.AddRange(LifecycleReader.FileMembers());
        }

        var properties = new List<PropertyModel>();
        var declaredTraitMembers = new List<string>();
        var traitMemberNames = new HashSet<string>(
            TraitCatalog.For(traits).SelectMany(definition => definition.Members)
                .Concat(lifecycle.Members)
                .Select(member => member.Name));

        // Zincir alanlarının tipi anahtarın tipine bağlıdır; adları şimdiden ayrılır, kendileri anahtar belli
        // olunca eklenir.
        var pairings = ChainOperation.All.Where(pairing => (traits & pairing.Trait) != 0).ToArray();
        foreach (var pairing in pairings)
        {
            traitMemberNames.Add(pairing.Forward);
            traitMemberNames.Add(pairing.Back);
        }
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

        var (keyGeneration, keyPrefix) = ReadKeyStrategy(chain, location, diagnostics);

        // Önekli anahtar metindir; kullanıcı Id'yi kendisi yazdıysa string olmalıdır.
        var prefixed = keyGeneration == KeyGeneration.Prefixed;
        var declaredTextKey = prefixed && id?.Type.SpecialType == SpecialType.System_String;

        // Anahtarı istemcinin verdiği archetype'larda varsayılan tip metindir; diğerlerinde sunucu Guid üretir.
        var idKind = archetype.HasClientKey() || prefixed ? IdKind.String : IdKind.Guid;
        if (declaredTextKey)
        {
            idKind = IdKind.String;
        }
        else if (id is not null && !TryGetIdKind(id.Type, archetype, out idKind))
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

        var idType = id?.Type.ToDisplayString(TypeFormat) ?? (archetype.HasClientKey() || prefixed ? "string" : GuidType);

        foreach (var pairing in pairings)
        {
            // Yeni kayıt eskisine, eskisi yenisine işaret eder; ikisi de yanıtta görünür, istekle yazılamaz.
            lifecycle.Members.Add(new TraitMember(pairing.Forward, idType + "?", InResponse: true, Search: null));
            lifecycle.Members.Add(new TraitMember(pairing.Back, idType + "?", InResponse: true, Search: null));
        }

        if (pairings.Length > 0 && idKind is IdKind.Int32 or IdKind.Int64)
        {
            // İki kayıt tek kaydetmede birbirine bağlanır; bunun için yeni kaydın anahtarı önceden bilinmelidir.
            diagnostics.Add(new DiagnosticInfo(
                "HALDUS007",
                location,
                new EquatableArray<string>(
                [
                    $"{pairings[0].Trait} needs a key that Haldus assigns before saving, but '{entityName}.{keyName}' is generated by the database; use a Guid or a prefixed key",
                ])));
        }

        if ((traits & Traits.Publishable) != 0)
        {
            // Yayındaki kopya aynı tabloda, taslağına işaret eden ayrı bir satırdır.
            lifecycle.Members.Add(new TraitMember("PublishedOfId", idType + "?", InResponse: false, Search: null));
            lifecycle.Members.Add(new TraitMember(
                "PublishedAt",
                "global::System.DateTimeOffset?",
                InResponse: true,
                new SearchField("PublishedAt", SearchKind.Range, "global::System.DateTimeOffset", IsNullable: true)));
        }

        foreach (var (trait, declaration) in new[] { (Traits.Revisioned, "Revisioned"), (Traits.ChangeFeed, "ChangeFeed"), (Traits.Publishable, "Publishable") })
        {
            // Görüntü, günlük satırı ve yayın kopyası kayıtla aynı kaydetmede yazılır; anahtar o sırada bilinmelidir.
            if ((traits & trait) != 0 && idKind is IdKind.Int32 or IdKind.Int64)
            {
                diagnostics.Add(new DiagnosticInfo(
                    "HALDUS007",
                    location,
                    new EquatableArray<string>(
                    [
                        $"{declaration} needs a key that Haldus assigns before saving, but '{entityName}.{keyName}' is generated by the database; use a Guid or a prefixed key",
                    ])));
            }
        }

        if (id is not null && keyGeneration != KeyGeneration.Sequential && idKind != (prefixed ? IdKind.String : IdKind.Guid))
        {
            diagnostics.Add(new DiagnosticInfo(
                "HALDUS007",
                location,
                new EquatableArray<string>(
                [
                    $"KeyStrategy.{keyGeneration} produces a {(prefixed ? "string" : "Guid")} key, but '{entityName}.{keyName}' is declared as '{id.Type.ToDisplayString()}'",
                ])));
        }

        if (archetype == Archetype.Lookup && properties.All(property => property.Name != "Name"))
        {
            properties.Insert(0, LookupName());
        }

        if (lifecycle.Expiry is { LifetimeTicks: null } && properties.All(property => property.Name != "ExpiresAt"))
        {
            // Son kullanma zamanını istemci veriyor: sıradan, yazılabilir bir alandır.
            properties.Add(LifecycleReader.ClientExpiry());
        }

        if (file is not null && properties.All(property => property.Name != "FileName"))
        {
            properties.Insert(0, LifecycleReader.FileName());
        }


        if ((traits & Traits.EffectiveDated) != 0)
        {
            // Dönemi istemci verir: iki alan da sıradan, yazılabilir alanlardır.
            properties.AddRange(LifecycleReader.EffectivePeriod().Where(period => properties.All(property => property.Name != period.Name)));
        }

        foreach (var message in Conflicts(archetype, traits, operations, unsupportedOperations, chain))
        {
            diagnostics.Add(new DiagnosticInfo("HALDUS007", location, new EquatableArray<string>([message])));
        }

        FilterModel? filter = null;
        var searchRequests = new List<SearchFieldRequest>();
        var sortRequests = new List<SearchFieldRequest>();
        var createOnlyRequests = new List<SearchFieldRequest>();
        var translatableRequests = new List<SearchFieldRequest>();
        var sensitiveRequests = new List<SearchFieldRequest>();
        SearchFieldRequest? naturalKeyRequest = null;
        var effectiveKeyRequests = new List<SearchFieldRequest>();
        TransferModel? transfer = null;
        var rollups = new List<RollupModel>();
        var reverseAttachments = new List<ReverseDeclaration>();
        var tagDeclarations = new List<ReverseDeclaration>();
        var derivedFrom = new List<SourceDeclaration>();
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
                case { Identifier.ValueText: "Sortable" }:
                    ReadFieldRequests(callName, call, sortRequests, diagnostics);
                    break;
                case { Identifier.ValueText: "CreateOnly" }:
                    ReadFieldRequests(callName, call, createOnlyRequests, diagnostics);
                    break;
                case { Identifier.ValueText: "Translatable" }:
                    ReadFieldRequests(callName, call, translatableRequests, diagnostics);
                    break;
                case { Identifier.ValueText: "Sensitive" }:
                    ReadFieldRequests(callName, call, sensitiveRequests, diagnostics);
                    break;
                // Alan saymak isteğe bağlıdır: sayılmazsa kapsamın tamamında tek bir dönem dizisi tutulur.
                case { Identifier.ValueText: "EffectiveDated" } when call.ArgumentList.Arguments.Count > 0:
                    ReadFieldRequests(callName, call, effectiveKeyRequests, diagnostics);
                    break;
                case { Identifier.ValueText: "NaturalKey" }:
                {
                    var keys = new List<SearchFieldRequest>();
                    ReadFieldRequests(callName, call, keys, diagnostics);

                    if (naturalKeyRequest is not null || keys.Count > 1)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            "HALDUS007",
                            LocationInfo.From(callName.GetLocation()),
                            new EquatableArray<string>([$"'{entityName}' can have only one natural key, made of a single field"])));
                    }

                    naturalKeyRequest ??= keys.FirstOrDefault();
                    break;
                }
                case GenericNameSyntax { Identifier.ValueText: "References", TypeArgumentList.Arguments: { Count: 1 } targets }:
                    references.Add(ReadReference(
                        ReferenceKind.Reference, targets[0], call, context.SemanticModel, OnDeleteBehavior.Restrict, cancellationToken));
                    break;
                case GenericNameSyntax { Identifier.ValueText: "Counts" or "Sums", TypeArgumentList.Arguments: { Count: 1 } counted } rollupCall:
                {
                    var child = context.SemanticModel.GetTypeInfo(counted[0], cancellationToken).Type as INamedTypeSymbol;
                    var known = child is { TypeKind: not TypeKind.Error };
                    var childName = known ? child!.Name : counted[0].ToString();
                    var sum = rollupCall.Identifier.ValueText == "Sums";

                    string? field = null;
                    string? into = null;
                    var acrossOwners = false;
                    ITypeSymbol? fieldType = null;

                    foreach (var argument in call.ArgumentList.Arguments)
                    {
                        if (DirectMember(argument.Expression) is { } member)
                        {
                            field = member;
                            fieldType = known ? PublicInstanceProperties(child!).FirstOrDefault(property => property.Name == member)?.Type : null;
                        }
                        else if (context.SemanticModel.GetConstantValue(argument.Expression, cancellationToken) is { HasValue: true } constant)
                        {
                            switch (constant.Value)
                            {
                                case string text:
                                    into = text;
                                    break;
                                case bool allowed:
                                    acrossOwners = allowed;
                                    break;
                            }
                        }
                        else
                        {
                            // Okunamayan bir argüman sessizce yok sayılsaydı, alanın adı veya izin yanlış anlaşılırdı.
                            diagnostics.Add(new DiagnosticInfo(
                                "HALDUS010",
                                LocationInfo.From(argument.GetLocation()),
                                new EquatableArray<string>([$"The arguments of {rollupCall.Identifier.ValueText}<{childName}>() must be readable at compile time"])));
                        }
                    }

                    var numeric = fieldType?.SpecialType is SpecialType.System_Decimal or SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Double;
                    into ??= sum ? childName + field + "Sum" : childName + "Count";

                    if ((sum && !numeric) || !SyntaxFacts.IsValidIdentifier(into) || rollups.Any(existing => existing.Into == into))
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            "HALDUS010",
                            LocationInfo.From(callName.GetLocation()),
                            new EquatableArray<string>(
                            [
                                sum && !numeric
                                    ? $"Sums<{childName}>() needs a non-nullable decimal, int, long or double property of '{childName}', written as 'item => item.Amount'"
                                    : $"'{into}' cannot name the field of {rollupCall.Identifier.ValueText}<{childName}>(): it must be a valid, unused identifier",
                            ])));
                        break;
                    }

                    rollups.Add(new RollupModel(
                        childName,
                        known ? child!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null,
                        sum ? field : null,
                        into,
                        LocationInfo.From(counted[0].GetLocation()))
                    {
                        AcrossOwners = acrossOwners,
                    });

                    // Alan yalnızca yanıtta görünür; değeri bağlı kayıtlar kaydedildikçe hesaplanır.
                    lifecycle.Members.Add(new TraitMember(
                        into,
                        sum ? fieldType!.ToDisplayString(TypeFormat) : "int",
                        InResponse: true,
                        new SearchField(into, SearchKind.Range, sum ? fieldType!.ToDisplayString(TypeFormat) : "int", IsNullable: false)));
                    break;
                }
                case GenericNameSyntax { Identifier.ValueText: "Exports" or "Imports", TypeArgumentList.Arguments: { Count: 1 } moved } transferCall:
                {
                    var target = context.SemanticModel.GetTypeInfo(moved[0], cancellationToken).Type as INamedTypeSymbol;
                    var known = target is { TypeKind: not TypeKind.Error };

                    if (archetype != Archetype.Job || transfer is not null)
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            "HALDUS007",
                            LocationInfo.From(callName.GetLocation()),
                            new EquatableArray<string>(
                            [
                                archetype != Archetype.Job
                                    ? $"{transferCall.Identifier.ValueText} turns a job into an export or an import; declare it on a MapJob call"
                                    : $"'{entityName}' can export or import one resource, not both",
                            ])));
                    }

                    transfer ??= new TransferModel(
                        transferCall.Identifier.ValueText == "Exports",
                        known ? target!.Name : moved[0].ToString(),
                        known ? target!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null,
                        LocationInfo.From(moved[0].GetLocation()));
                    break;
                }
                case GenericNameSyntax { Identifier.ValueText: "AttachedTo", TypeArgumentList.Arguments: { Count: 1 } hosts }:
                    references.Add(ReadReference(
                        ReferenceKind.Parent, hosts[0], call, context.SemanticModel, OnDeleteBehavior.Cascade, cancellationToken) with
                    {
                        Attached = true,
                        Required = false,
                        Shallow = false,
                    });
                    break;
                case GenericNameSyntax { Identifier.ValueText: "Attachable" or "Commentable" or "Taggable", TypeArgumentList.Arguments: { Count: 1 } children } reverse:
                {
                    // Hedef tarafında yazılan bildirim: adı geçen kaynak, tüm kaynaklar bilindikten sonra çözülür.
                    var child = context.SemanticModel.GetTypeInfo(children[0], cancellationToken).Type as INamedTypeSymbol;
                    var declaration = new ReverseDeclaration(
                        reverse.Identifier.ValueText,
                        child is { TypeKind: not TypeKind.Error } ? child.Name : children[0].ToString(),
                        child is { TypeKind: not TypeKind.Error } ? child.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null,
                        LocationInfo.From(children[0].GetLocation()));

                    (reverse.Identifier.ValueText == "Taggable" ? tagDeclarations : reverseAttachments).Add(declaration);
                    break;
                }
                case GenericNameSyntax { Identifier.ValueText: "Under", TypeArgumentList.Arguments: { Count: 1 } parents }:
                    references.Add(ReadReference(
                        ReferenceKind.Parent, parents[0], call, context.SemanticModel, OnDeleteBehavior.Cascade, cancellationToken));
                    break;
                case GenericNameSyntax { Identifier.ValueText: "CreatedFrom", TypeArgumentList.Arguments: { Count: 1 } origins }:
                {
                    // Geçişin adı derleme zamanında okunur; değişkenden gelen bir ad görülemez.
                    var trigger = call.ArgumentList.Arguments
                        .Select(argument => argument.Expression.ToString())
                        .FirstOrDefault(text => text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"');

                    if (trigger is null && call.ArgumentList.Arguments.Any(argument =>
                        argument.NameColon?.Name.Identifier.ValueText == "on" && argument.Expression.ToString() != "null"))
                    {
                        diagnostics.Add(new DiagnosticInfo(
                            "HALDUS010",
                            LocationInfo.From(callName.GetLocation()),
                            new EquatableArray<string>(["The transition that creates the record must be written as a string literal, for example CreatedFrom<Cart>(on: \"complete\")"])));
                    }

                    references.Add(ReadReference(
                        ReferenceKind.Source, origins[0], call, context.SemanticModel, OnDeleteBehavior.Restrict, cancellationToken) with
                    {
                        Required = false,
                        Trigger = trigger?.Substring(1, trigger.Length - 2),
                    });
                    break;
                }

                case GenericNameSyntax { Identifier.ValueText: "DerivedFrom", TypeArgumentList.Arguments: { Count: 1 } sources }:
                {
                    var source = context.SemanticModel.GetTypeInfo(sources[0], cancellationToken).Type as INamedTypeSymbol;

                    derivedFrom.Add(source is { TypeKind: not TypeKind.Error }
                        ? new SourceDeclaration(source.Name, source.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), LocationInfo.From(sources[0].GetLocation()))
                        : new SourceDeclaration(sources[0].ToString(), null, LocationInfo.From(sources[0].GetLocation())));
                    break;
                }

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

        if (transfer is not null && archetype == Archetype.Job)
        {
            // Aktarma işinin girdileri ve çıktıları Haldus'a aittir; mantığı da üretilir.
            properties.AddRange(LifecycleReader.TransferInputs(transfer.Export).Where(input => properties.All(property => property.Name != input.Name)));
            lifecycle.Members.AddRange(transfer.Export ? LifecycleReader.ExportMembers() : LifecycleReader.ImportMembers());
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
            IsCommandWritten = chain.Any(call => call.Name == "AsCommands"),
            SortRequests = sortRequests.ToEquatableArray(),
            CreateOnlyRequests = createOnlyRequests.ToEquatableArray(),
            TranslatableRequests = translatableRequests.ToEquatableArray(),
            SensitiveRequests = sensitiveRequests.ToEquatableArray(),
            NaturalKeyRequest = naturalKeyRequest,
            EffectiveKeyRequests = effectiveKeyRequests.ToEquatableArray(),
            ReverseAttachments = reverseAttachments.ToEquatableArray(),
            TagDeclarations = tagDeclarations.ToEquatableArray(),
            DerivedFrom = derivedFrom.ToEquatableArray(),
            KeyGeneration = keyGeneration,
            KeyPrefix = keyPrefix,
            Workflow = lifecycle.Workflow,
            Numbering = lifecycle.Numbering,
            Expiry = lifecycle.Expiry,
            Secret = lifecycle.Secret,
            Token = lifecycle.Token,
            Approval = lifecycle.Approval,
            File = file,
            Transfer = archetype == Archetype.Job ? transfer : null,
            RetentionTicks = lifecycle.RetentionTicks,
            Balance = balance,
            ChangeSets = lifecycle.ChangeSets,
            Axes = lifecycle.Axes.ToEquatableArray(),
            Rollups = rollups.ToEquatableArray(),
            ReversibleIn = lifecycle.ReversibleIn,
            SupersedableIn = lifecycle.SupersedableIn,
            Batch = lifecycle.Batch,
            ExtraMembers = lifecycle.Members.ToEquatableArray(),
            IsComputed = entity?.AllInterfaces.Any(candidate => candidate.ToDisplayString() == "Haldus.IComputed") == true,
            Contracts = (entity?.AllInterfaces ?? [])
                .Where(candidate => candidate is
                {
                    Name: "IReversal" or "ISupersession" or "IAmendment" or "ICreatedFrom",
                    TypeArguments.Length: 1,
                    ContainingNamespace: { Name: "Haldus", ContainingNamespace.IsGlobalNamespace: true },
                })
                .Select(candidate => candidate.Name == "ICreatedFrom"
                    ? $"ICreatedFrom:{candidate.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}"
                    : candidate.Name)
                .OrderBy(contract => contract, StringComparer.Ordinal)
                .ToEquatableArray(),
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
                return archetype is not (Archetype.Lookup or Archetype.Balance);
            case SpecialType.System_Int64:
                kind = IdKind.Int64;
                return archetype is not (Archetype.Lookup or Archetype.Balance);
            default:
                kind = IdKind.Guid;
                return archetype is not (Archetype.Lookup or Archetype.Balance) && type.ToDisplayString() == "System.Guid";
        }
    }

    private static string SupportedKeyTypes(Archetype archetype) => archetype switch
    {
        Archetype.Lookup or Archetype.Balance => "string",
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

        if (archetype == Archetype.Balance)
        {
            // Bakiye yalnızca kendi komutlarıyla değişir; kaydı başka yoldan yazan veya gizleyen bildirimler onu bozardı.
            foreach (var declaration in chain.Select(call => call.Name).Distinct())
            {
                if (declaration is not ("MultiTenant" or "Idempotent" or "Sortable" or "Searchable" or "Filterable" or "Buckets" or "AllowNegative" or "Without" or "Only"))
                {
                    yield return $"{declaration} cannot be combined with MapBalance: a balance changes only through its own commands";
                }
            }
        }

        // Ayrı uçlu detay, sahibinin adresi altında yaşayan sıradan bir kaynaktır.
        var crudLike = archetype == Archetype.Crud
            || (archetype == Archetype.Detail && chain.Any(call => call.Name == "AsSubresource"));

        if (chain.Any(call => call.Name == "CreateOnly")
            && crudLike
            && ((operations & Operations.Create) == 0 || (operations & (Operations.Update | Operations.Patch)) == 0))
        {
            yield return "CreateOnly has no effect: the resource needs the Create operation and at least one of Update and Patch";
        }

        foreach (var declaration in new[] { "SecretOnce", "TokenAccess" })
        {
            if (chain.All(call => call.Name != declaration) || !(crudLike || archetype == Archetype.AppendOnly))
            {
                continue;
            }

            if ((operations & Operations.Create) == 0)
            {
                yield return $"{declaration} requires the Create operation: the value is generated and shown when the record is created";
            }

            // Değer yalnızca tekil oluşturma yanıtında gösterilebilir; başka bir yoldan doğan kayıt onsuz doğardı.
            foreach (var other in new[] { "Batch", "Cloneable", "Previewable", "Reversible", "Supersedes", "Document", "CreatedFrom" })
            {
                if (chain.Any(call => call.Name == other))
                {
                    yield return $"{declaration} cannot be combined with {other}: a record created that way would have no value to show";
                }
            }
        }

        if (chain.Any(call => call.Name == "Revisioned") && chain.Any(call => call.Name is "SecretOnce" or "TokenAccess"))
        {
            yield return "Revisioned cannot be combined with SecretOnce or TokenAccess: the snapshots would keep copies of the stored secret";
        }

        if (chain.Any(call => call.Name == "ChangeFeed")
            && chain.Any(call => call.Name == "Under" && call.Arguments.Any(argument => argument.EndsWith("true", StringComparison.Ordinal))))
        {
            // Kısa adreste ebeveyn adreste yoktur; silinen kayıtların kimin ebeveynine ait olduğu süzülemezdi.
            yield return "ChangeFeed cannot be combined with Under<T>(shallow: true): the feed could not tell whose deleted records it is listing";
        }

        if ((traits & Traits.Purgeable) != 0)
        {
            // Ölçüt kaynağın filtresidir; her kayıt toplu silmenin öğe yolundan, yani tekil silmenin kurallarıyla silinir.
            if (chain.All(call => call.Name != "Filterable") || chain.All(call => call.Name != "Batch"))
            {
                yield return "Purgeable needs Filterable<TFilter>() for its criteria and Batch() for the rules of deleting each record";
            }

            if ((operations & Operations.Delete) == 0)
            {
                yield return "Purgeable requires the Delete operation, which has been removed from this resource";
            }

            if ((traits & Traits.ConcurrencyChecked) != 0)
            {
                yield return "Purgeable cannot be combined with ConcurrencyChecked: records deleted by a criterion carry no version";
            }
        }

        if ((traits & Traits.Rerunnable) != 0 && chain.Any(call => call.Name == "Imports"))
        {
            // İçe aktarmanın içeriği iş bitince boşaltılır; yeniden çalıştırılacak bir girdi kalmaz.
            yield return "Rerunnable cannot be combined with Imports: the content of an import is cleared when the job ends";
        }

        if (chain.Any(call => call.Name == "ChangeSets") && crudLike && (operations & Operations.Patch) == 0)
        {
            // Küme, kaydın kısmi güncellemesinin birikmiş halidir.
            yield return "ChangeSets requires the Patch operation, which has been removed from this resource";
        }

        if ((traits & Traits.Translatable) != 0 && (operations & (Operations.Update | Operations.Patch)) == 0)
        {
            // Çeviri yazmak kaydı güncellemektir.
            yield return "Translatable requires the Update or Patch operation, which have been removed from this resource";
        }

        if ((traits & Traits.ReadTracking) != 0 && (traits & Traits.Owned) == 0)
        {
            // Okundu bilgisi kaydın kendisinde durur; kayıt tek bir kullanıcıya ait olmalıdır.
            yield return "ReadTracking needs OwnedByCurrentUser: whether a record has been read is kept on the record, so it must belong to one user";
        }

        if (chain.Any(call => call.Name == "CustomFields") && chain.Any(call => call.Name == "Under"))
        {
            // Tanımlar kaynağın kendi adresinde yönetilir ve bütün kayıtları için geçerlidir; ebeveyn başına değil.
            yield return "CustomFields cannot be combined with Under<T>(): field definitions belong to the resource, not to one parent";
        }

        if (chain.Any(call => call.Name == "Publishable"))
        {
            // Yayın kendi durum modelidir ve kopya, taslağın yalnızca içeriğini taşır; Haldus'un yönettiği alanları
            // (durum, numara, ömür, sır, onay, dönem) taşıyan bildirimlerle birlikte anlamı belirsizleşirdi.
            foreach (var other in new[]
            {
                "Workflow", "Document", "Numbered", "Expiring", "SecretOnce", "TokenAccess", "Reversible", "Supersedes", "Approvable", "EffectiveDated",
                "Retention", "ReadTracking", "Counts", "Sums", "Axis",
            })
            {
                if (chain.Any(call => call.Name == other))
                {
                    yield return $"Publishable cannot be combined with {other}: the published copy carries only the content of the draft";
                }
            }

            if ((operations & Operations.Create) == 0)
            {
                yield return "Publishable requires the Create operation, which has been removed from this resource";
            }
        }

        if (chain.Any(call => call.Name == "Rotatable") && chain.All(call => call.Name != "SecretOnce"))
        {
            yield return "Rotatable rotates the secret of the resource; declare SecretOnce(prefix: ...) as well";
        }

        if (chain.Any(call => call.Name == "TokenAccess"))
        {
            // Token ile gelen kişinin kiracısı ve kimliği yoktur; kaydın görünürlüğü onlara bağlıysa kayıt bulunamaz.
            if (chain.Any(call => call.Name == "Under"))
            {
                yield return "TokenAccess cannot be combined with Under<T>(): the visibility of the record would depend on a parent the token holder cannot see";
            }

            if ((traits & Traits.Owned) != 0)
            {
                yield return "TokenAccess cannot be combined with OwnedByCurrentUser: the token holder is not the owner";
            }
        }

        // Geçişle doğan kayıt için açık bir uç üretilmez; üretilen uç ise bir oluşturmadır.
        if (chain.Any(call => call.Name == "CreatedFrom" && !call.Arguments.Any(argument => argument.StartsWith("\"", StringComparison.Ordinal)))
            && (crudLike || archetype == Archetype.AppendOnly)
            && (operations & Operations.Create) == 0)
        {
            yield return "CreatedFrom adds an endpoint that creates records, but the Create operation has been removed from this resource; "
                + "keep Create, or pass on: \"...\" so that the record is created only by a transition of its source";
        }

        foreach (var declaration in new[] { "Reversible", "Supersedes", "Document" })
        {
            if (chain.All(call => call.Name != declaration)
                || !(crudLike || (declaration == "Reversible" && archetype == Archetype.AppendOnly)))
            {
                continue;
            }

            if ((operations & Operations.Create) == 0)
            {
                yield return $"{declaration} requires the Create operation, which has been removed from this resource";
            }
        }

        foreach (var declaration in new[] { "Cloneable", "Previewable" })
        {
            // İkisi de bir oluşturma biçimidir; oluşturması kaldırılmış bir kaynakta arka kapı olurlardı.
            // Archetype'ın kendisi uygun değilse yalnızca o bildirilir.
            if (chain.Any(call => call.Name == declaration)
                && (crudLike || (declaration == "Previewable" && archetype == Archetype.AppendOnly))
                && (operations & Operations.Create) == 0)
            {
                yield return $"{declaration} requires the Create operation, which has been removed from this resource";
            }
        }

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

                // Ebeveyn altında yaşamak, sunucunun anahtar ürettiği koleksiyonlar ve ebeveyn başına tek kayıt içindir.
                "Under" => archetype is Archetype.Crud or Archetype.ReadOnly or Archetype.AppendOnly or Archetype.Singleton,
                "Hierarchical" => archetype is Archetype.Crud or Archetype.ReadOnly or Archetype.AppendOnly,
                _ => true,
            };

            if (!allowed)
            {
                yield return $"{relation} cannot be combined with {name}";
            }
        }

        if (chain.Any(call => call.Name == "AsCommands") && chain.Any(call => call.Name == "AsSubresource"))
        {
            yield return "AsCommands and AsSubresource are two different ways to write the rows of a detail; choose one";
        }

        if (archetype == Archetype.Singleton && chain.Any(call => call.Name == "Under" && call.Arguments.Any(argument => argument == "true")))
        {
            yield return "A singleton under a parent is addressed through that parent; shallow: true does not apply to it";
        }

        if (archetype == Archetype.Singleton && chain.Any(call => call.Name == "Under") && (traits & Traits.MultiTenant) != 0)
        {
            // Kapsam tektir: ya kiracı başına ya ebeveyn başına bir kayıt. Ebeveyn kiracıya aitse kayıt da öyledir.
            yield return "A singleton under a parent is scoped by that parent; remove MultiTenant (the record follows the tenancy of its parent)";
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
                "Workflow" or "Axis" => archetype is Archetype.Crud or Archetype.ReadOnly or Archetype.AppendOnly,

                // Numara ve ömür kayıt oluşturulurken verilir.
                "Numbered" or "Expiring" => archetype is Archetype.Crud or Archetype.AppendOnly,

                // Anahtarla ekle-veya-güncelle zaten tekrara dayanıklıdır; tek kaydın oluşturma ucu yoktur.
                "Idempotent" => archetype is Archetype.Crud or Archetype.ReadOnly or Archetype.AppendOnly or Archetype.Job or Archetype.Balance,

                // Toplu uçlar, sunucunun anahtar ürettiği koleksiyonların işlemlerini izler.
                "Batch" => archetype is Archetype.Crud or Archetype.ReadOnly or Archetype.AppendOnly,

                // Anahtarın biçimi yalnızca sunucunun anahtar ürettiği koleksiyonlarda seçilir; işin kimliği
                // kuyruklarda taşındığı için sabittir.
                "KeyStrategy" => archetype is Archetype.Crud or Archetype.ReadOnly or Archetype.AppendOnly,

                // Doğal anahtar, zaten istemcinin anahtar verdiği archetype'larda anlamsızdır.
                "NaturalKey" => crudLike || archetype is Archetype.ReadOnly or Archetype.AppendOnly,
                "Metadata" => archetype is Archetype.Crud or Archetype.ReadOnly or Archetype.AppendOnly,

                // Oluşturma ve güncelleme isteklerinin ayrı olduğu tek şekil budur.
                "CreateOnly" => crudLike,

                // Sıra ve kopya, istemcinin oluşturup silebildiği koleksiyonlar içindir.
                "Orderable" or "Cloneable" => crudLike,

                // Sır ve token kayıt oluşturulurken üretilir ve yalnızca o yanıtta gösterilir.
                "SecretOnce" or "Rotatable" or "TokenAccess" => crudLike || archetype == Archetype.AppendOnly,

                // Başka bir kayıttan üretilen kayıt, sunucunun anahtar ürettiği bir koleksiyona eklenir.
                "CreatedFrom" => crudLike || archetype == Archetype.AppendOnly,

                // Türeyen kaynağı istemci yazmaz; yalnızca izdüşüm yazar.
                "DerivedFrom" => archetype == Archetype.ReadOnly,

                // Ters kayıt, yalnızca eklenen kayıtların düzeltilme biçimidir; değiştirilebilen kayıtta da olur.
                "Reversible" => crudLike || archetype == Archetype.AppendOnly,
                "Supersedes" => crudLike,

                // Belge, taslağı düzenlenen ve düzeltmesi yeni bir kayıt olan bir koleksiyondur.
                "Document" => crudLike,

                // Saklama süresi, okundu takibi ve toplanan alanlar, anahtarıyla adreslenen koleksiyonlar içindir.
                "Retention" or "ReadTracking" or "Counts" or "Sums" => crudLike || archetype is Archetype.AppendOnly or Archetype.ReadOnly,

                // Ölçüte göre silme, istemcinin sildiği koleksiyonlar içindir; yeniden çalıştırma yalnızca işler için.
                "Purgeable" => crudLike,
                "Rerunnable" => archetype == Archetype.Job,

                // Çeviriler ve değişiklik kümeleri, istemcinin yazıp değiştirdiği kayıtlar içindir.
                "Translatable" or "ChangeSets" => crudLike,

                // Özel alanlar, istemcinin yazıp değiştirdiği kayıtlar içindir.
                "CustomFields" => crudLike,

                // Taslak, istemcinin yazdığı kayıttır.
                "Publishable" => crudLike,

                // Dönem, istemcinin yazıp değiştirdiği bir alandır.
                "EffectiveDated" => crudLike,

                // Sürümler ve geri yükleme, istemcinin değiştirebildiği kayıtlar içindir.
                "Revisioned" => crudLike,

                // Akış, sunucunun anahtar ürettiği koleksiyonları izler.
                "ChangeFeed" => crudLike || archetype is Archetype.AppendOnly or Archetype.ReadOnly,

                // Karar, anahtarıyla adreslenen bir kayda verilir.
                "Approvable" => crudLike || archetype is Archetype.AppendOnly or Archetype.ReadOnly,
                "Previewable" => crudLike || archetype == Archetype.AppendOnly,
                _ => true,
            };

            if (!allowed)
            {
                yield return $"{declaration} cannot be combined with {name}";
            }
        }

        if ((traits & Traits.Owned) != 0)
        {
            // Sahiplik, sunucunun anahtar ürettiği koleksiyonlar ve işler içindir.
            if (archetype is not (Archetype.Crud or Archetype.ReadOnly or Archetype.AppendOnly or Archetype.Job))
            {
                yield return $"OwnedByCurrentUser cannot be combined with {name}";
            }

            if ((traits & Traits.OwnedPlainRoute) == 0 && chain.Any(call => call.Name == "Under"))
            {
                yield return "OwnedByCurrentUser places the resource under /me, which cannot be combined with Under<T>(); "
                    + "pass underMe: false to keep the parent's address";
            }
        }

        if (chain.Any(call => call.Name == "Batch")
            && (traits & Traits.ConcurrencyChecked) != 0
            && (operations & (Operations.Update | Operations.Delete)) != 0)
        {
            // Toplu güncelleme ve silme öğe başına sürüm taşımaz; sürüm kontrolünü sessizce atlamak olurdu.
            yield return "Batch cannot be combined with ConcurrencyChecked while the resource can be updated or deleted: "
                + "a batch item cannot carry the version it expects. Remove those operations with Without, or drop one of the two";
        }

        foreach (var declaration in new[] { "Filterable", "Searchable", "Sortable" })
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

    internal static PropertyModel ToModel(IPropertySymbol property, SearchKind searchKind, string valueType)
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

    /// <summary>Alanları lambda ile sayan bir bildirimi (<c>Sortable</c>, <c>CreateOnly</c>, <c>NaturalKey</c>) okur.</summary>
    private static void ReadFieldRequests(
        SimpleNameSyntax callName,
        InvocationExpressionSyntax call,
        List<SearchFieldRequest> requests,
        List<DiagnosticInfo> diagnostics)
    {
        if (call.ArgumentList.Arguments.Count == 0)
        {
            var declaration = callName.Identifier.ValueText;

            diagnostics.Add(new DiagnosticInfo(
                "HALDUS010",
                LocationInfo.From(callName.GetLocation()),
                new EquatableArray<string>([$"{declaration} needs at least one field, for example {declaration}(item => item.Name)"])));
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

    /// <summary>
    /// <c>.KeyStrategy(...)</c> bildirimini okur. Biçim ve önek derleme zamanında bilinmelidir; önek ayrıca
    /// kimliğin her yerde (adres, günlük, dosya adı) güvenle kullanılabilmesi için dar tutulur.
    /// </summary>
    private static (KeyGeneration Generation, string? Prefix) ReadKeyStrategy(
        EquatableArray<ChainCall> chain,
        LocationInfo location,
        List<DiagnosticInfo> diagnostics)
    {
        if (chain.FirstOrDefault(call => call.Name == "KeyStrategy") is not { } call)
        {
            return (KeyGeneration.Sequential, null);
        }

        void Fail(string id, string message) =>
            diagnostics.Add(new DiagnosticInfo(id, location, new EquatableArray<string>([message])));

        var strategy = call.Arguments.Length == 0 ? string.Empty : call.Arguments[0];

        if (!strategy.Contains("KeyStrategy.")
            || !Enum.TryParse<KeyGeneration>(strategy.Substring(strategy.LastIndexOf('.') + 1), out var generation))
        {
            Fail("HALDUS010", "KeyStrategy must be written as a constant, for example KeyStrategy(KeyStrategy.Prefixed, \"ord\")");
            return (KeyGeneration.Sequential, null);
        }

        var prefixArgument = call.Arguments.Length > 1 ? call.Arguments[1] : null;

        if (generation != KeyGeneration.Prefixed)
        {
            if (prefixArgument is not null)
            {
                Fail("HALDUS007", $"A key prefix has no effect with KeyStrategy.{generation}; it belongs to KeyStrategy.Prefixed");
            }

            return (generation, null);
        }

        var prefix = prefixArgument is { Length: >= 2 } literal && literal[0] == '"' && literal[literal.Length - 1] == '"'
            ? literal.Substring(1, literal.Length - 2)
            : null;

        if (prefix is null || !IsKeyPrefix(prefix))
        {
            Fail(
                "HALDUS010",
                "KeyStrategy.Prefixed needs a prefix written as a string literal: it starts with a lowercase letter, "
                + "contains only lowercase letters and digits, and is at most 12 characters long, for example \"ord\"");
            return (KeyGeneration.Sequential, null);
        }

        return (generation, prefix);
    }

    internal static bool IsKeyPrefix(string prefix) =>
        prefix.Length is > 0 and <= 12
        && prefix[0] is >= 'a' and <= 'z'
        && prefix.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9'));

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
        var shallow = false;

        foreach (var argument in call.ArgumentList.Arguments)
        {
            var text = argument.Expression.ToString();

            if (kind == ReferenceKind.Parent && text == "true")
            {
                shallow = true;
                continue;
            }

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

        // Hedef bu derlemede eşlenmiş bir kaynak değilse anahtarı tipinden okunur: başka bir modülde derlenmiş
        // bir Haldus entity'sinde anahtar archetype'a göre Id, Code veya Key'dir.
        var properties = resolved ? PublicInstanceProperties(target!).ToList() : [];
        var targetKey = new[] { "Id", "Code", "Key" }
            .Select(name => properties.FirstOrDefault(property => property.Name == name))
            .FirstOrDefault(property => property is not null);

        var external = resolved
            && !SymbolEqualityComparer.Default.Equals(target!.ContainingAssembly, semanticModel.Compilation.Assembly);

        return new ReferenceDeclaration(
            kind,
            resolved ? target!.Name : targetSyntax.ToString(),
            resolved ? target!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null,
            targetKey?.Type.ToDisplayString(TypeFormat),
            propertyName,
            onDelete,
            required,
            LocationInfo.From(targetSyntax.GetLocation()),
            onDeleteExplicit)
        {
            TargetKeyName = targetKey?.Name,
            IsExternal = external,
            TargetTenantScoped = resolved && target!.AllInterfaces.Any(candidate => candidate.ToDisplayString() == "Haldus.IMultiTenant"),
            Shallow = shallow,
        };
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
