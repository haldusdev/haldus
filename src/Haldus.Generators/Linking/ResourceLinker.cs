using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;

namespace Haldus.Generators.Linking;

/// <summary>
/// Kaynakları birbirine bağlar. Her <c>Map*</c> çağrısı tek başına ayrıştırılır; bir referansın hedefinin
/// anahtar tipi, adı ve adresi ise ancak tüm kaynaklar bilindiğinde çözülebilir. Bu adım referansları
/// çözer, yabancı anahtar alanlarını ekler ve aranabilir alanları doğrular.
/// </summary>
internal static class ResourceLinker
{
    private const string GuidType = "global::System.Guid";
    private const string RequiredAttribute = "global::System.ComponentModel.DataAnnotations.RequiredAttribute";

    public static List<ResourceModel> Link(IReadOnlyList<ResourceModel> resources, Action<DiagnosticInfo> report)
    {
        resources = PrepareDetails(resources, report);

        var byType = resources.ToDictionary(resource => resource.QualifiedName);
        var linked = new List<ResourceModel>();

        foreach (var resource in resources)
        {
            var failed = false;

            void Fail(string id, LocationInfo location, params string[] arguments)
            {
                failed = true;
                report(new DiagnosticInfo(id, location, arguments.ToEquatableArray()));
            }

            var properties = resource.Properties.ToList();
            var references = new List<ResolvedReference>();

            foreach (var declaration in resource.ReferenceDeclarations)
            {
                if (Resolve(resource, declaration, resources, byType, properties, Fail) is { } reference
                    && references.All(existing => existing.PropertyName != reference.PropertyName))
                {
                    references.Add(reference);
                }
            }

            var searchFields = ResolveSearchFields(resource, properties, Fail);

            if (!failed)
            {
                linked.Add(resource with
                {
                    Properties = properties.ToEquatableArray(),
                    References = references.ToEquatableArray(),
                    SearchFields = searchFields.ToEquatableArray(),
                });
            }
        }


        // Gömülü detaylar sahiplerine iliştirilir; sahibin DTO'ları ve uçları onları taşır.
        return
        [
            .. linked.Select(resource => resource with
            {

                Details = linked
                    .Where(detail => detail.Archetype == Archetype.Detail && detail.Parent?.TargetType == resource.QualifiedName)
                    .ToEquatableArray(),
            }),
        ];
    }

    /// <summary>
    /// <c>MapDetail</c> ile bildirilen kaynakları sahiplerine bağlar. Detay, sahibine zorunlu ve zincirleme
    /// silinen bir ebeveyn referansı kazanır; böylece görünürlüğünü de ondan devralır. Ayrı uçlu bir detay,
    /// bundan sonra sahibinin adresi altında yaşayan sıradan bir kaynaktır.
    /// </summary>
    private static List<ResourceModel> PrepareDetails(IReadOnlyList<ResourceModel> resources, Action<DiagnosticInfo> report)
    {
        var byType = resources.ToDictionary(resource => resource.QualifiedName);
        var prepared = new List<ResourceModel>();

        foreach (var resource in resources)
        {
            if (resource.Archetype != Archetype.Detail)
            {
                prepared.Add(resource);
                continue;
            }

            var owner = Find(resource.DetailOfName!, resource.DetailOfType, resources, byType);

            if (owner is null)
            {
                report(new DiagnosticInfo(
                    "HALDUS009", resource.Location, new[] { resource.DetailOfName!, resource.EntityName }.ToEquatableArray()));
                continue;
            }

            var supported = resource.IsSubresource
                ? owner.Archetype is Archetype.Crud or Archetype.ReadOnly or Archetype.AppendOnly
                : owner.Archetype is Archetype.Crud or Archetype.AppendOnly;

            var problem =
                !supported
                    ? $"'{resource.EntityName}' cannot be a detail of '{owner.EntityName}': Map{owner.Archetype} does not support "
                        + (resource.IsSubresource ? "sub-resource details" : "embedded details")
                : !resource.IsSubresource && resource.IdKind != IdKind.Guid
                    ? $"'{resource.EntityName}' is an embedded detail, so its Id must be a Guid; Haldus assigns it when the row is added"
                : null;

            if (problem is not null)
            {
                report(new DiagnosticInfo("HALDUS007", resource.Location, new[] { problem }.ToEquatableArray()));
                continue;
            }

            // Gömülü satırlar istekte geldikleri sırayla saklanır; sırayı tutan alan DTO'lara girmez.
            var position = resource.IsSubresource ? null : resource.Properties.FirstOrDefault(property => property.Name == "Position");

            prepared.Add(resource with
            {
                Archetype = resource.IsSubresource ? Archetype.Crud : Archetype.Detail,
                RouteRelativeToParent = resource.IsSubresource,
                PositionDeclared = position is not null,

                // Detay sahibinin parçasıdır: sahibi düzenlenemezken ayrı uçlarından da yazılamaz.
                OwnerGuard = resource.IsSubresource && owner.Workflow is { EditableIn: { } editable } ownerWorkflow
                    ? new OwnerGuardModel(ownerWorkflow.StatusType, editable)
                    : null,
                Properties = resource.Properties.Where(property => property != position).ToEquatableArray(),
                ReferenceDeclarations = resource.ReferenceDeclarations
                    .Append(new ReferenceDeclaration(
                        ReferenceKind.Parent,
                        owner.EntityName,
                        owner.QualifiedName,
                        TargetKeyType: null,
                        PropertyName: null,

                        // Satır sahibine aittir; sahibi silinince her zaman onunla gider.
                        OnDeleteBehavior.Cascade,
                        Required: true,
                        resource.Location,
                        OnDeleteExplicit: true))
                    .ToEquatableArray(),
            });
        }

        return prepared;
    }

    /// <summary>
    /// <c>MapTransition</c> çağrılarını sahiplerine bağlar ve doğrular: sahip bir durum makinesi taşımalı,
    /// durumlar onun durum tipinden olmalı, adlar derleme zamanında okunabilmeli ve çakışmamalıdır.
    /// </summary>
    public static List<ResourceModel> AttachTransitions(
        IReadOnlyList<ResourceModel> resources,
        IEnumerable<TransitionDeclaration> declarations,
        Action<DiagnosticInfo> report)
    {
        var byType = resources.ToDictionary(resource => resource.QualifiedName);
        var transitions = resources.ToDictionary(resource => resource.QualifiedName, _ => new List<TransitionModel>());

        foreach (var declaration in declarations.Distinct())
        {
            void Fail(string id, string message) =>
                report(new DiagnosticInfo(id, declaration.Location, new[] { message }.ToEquatableArray()));

            var kind = declaration.IsAction ? "action" : "transition";

            if (Find(declaration.OwnerName, declaration.OwnerType, resources, byType) is not { } owner)
            {
                Fail("HALDUS007", $"{(declaration.IsAction ? "MapAction" : "MapTransition")} is declared on '{declaration.OwnerName}', which is not a mapped resource");
                continue;
            }

            if (declaration.IsAction)
            {
                // Aksiyon tek bir kayıt üzerinde çalışır; kaydın adreste bir anahtarı olmalıdır.
                if (owner.Archetype is Archetype.Singleton or Archetype.Detail)
                {
                    Fail("HALDUS007", $"MapAction needs a resource that is addressed by its key; '{owner.EntityName}' is mapped with Map{owner.Archetype}");
                    continue;
                }
            }
            else
            {
                if (owner.Workflow is not { } workflow)
                {
                    Fail("HALDUS007", $"MapTransition needs a state machine; declare Workflow<TStatus>() on '{owner.EntityName}'");
                    continue;
                }

                if (!declaration.StatesReadable)
                {
                    Fail("HALDUS010", "The states of a transition must be written as enum members, for example from: OrderStatus.Draft, to: OrderStatus.Placed");
                    continue;
                }

                if (declaration.From.Append(declaration.To!).FirstOrDefault(state => state.Type != workflow.StatusType) is { } foreign)
                {
                    Fail("HALDUS010", $"'{foreign.Name}' is not a state of '{owner.EntityName}'; transitions use the status type declared with Workflow<TStatus>()");
                    continue;
                }
            }

            if (!declaration.NameIsConstant)
            {
                Fail("HALDUS010", $"The name of {(declaration.IsAction ? "an action" : "a transition")} must be a constant; it becomes part of the generated route");
                continue;
            }

            var route = declaration.Name ?? TransitionRoute(declaration.RequestName!, owner.EntityName);

            if (!IsValidTransitionName(route))
            {
                Fail("HALDUS010", $"'{route}' is not a valid {kind} name; use lowercase words separated by '-', for example \"mark-paid\"");
                continue;
            }

            if (ReservedRoute(owner, route) is { } trait)
            {
                Fail("HALDUS007", $"The {kind} '{route}' collides with the '{route}' endpoint that {trait} generates on '{owner.EntityName}'");
                continue;
            }

            var existing = transitions[owner.QualifiedName];

            if (existing.Any(transition => transition.Route == route))
            {
                Fail("HALDUS007", $"'{owner.EntityName}' already has a transition or an action named '{route}'");
                continue;
            }

            existing.Add(new TransitionModel(
                route,
                string.Concat(route.Split('-').Select(word => char.ToUpperInvariant(word[0]) + word.Substring(1))),
                declaration.Name,
                declaration.RequestType,
                declaration.ImplementsTransition,
                declaration.From.Select(state => state.Name).Distinct().ToEquatableArray(),
                declaration.To?.Name)
            {
                IsAction = declaration.IsAction,
                ResponseType = declaration.ResponseType,
            });
        }

        var attached = new List<ResourceModel>();

        foreach (var resource in resources)
        {
            var own = transitions[resource.QualifiedName];
            var result = own.Count == 0 ? resource : resource with { Transitions = own.ToEquatableArray() };

            if (result.Numbering is { AssignOn: { } assignOn } && own.All(transition => transition.To != assignOn))
            {
                report(new DiagnosticInfo("HALDUS007", resource.Location, new[]
                {
                    $"Numbered(assignOn: {assignOn}) never takes effect on '{resource.EntityName}': no transition leads to '{assignOn}'",
                }.ToEquatableArray()));
            }

            if (result.Has(Traits.Idempotent) && !result.HasWrites)
            {
                report(new DiagnosticInfo("HALDUS007", resource.Location, new[]
                {
                    $"Idempotent has no effect on '{resource.EntityName}': it has neither a create operation nor transitions",
                }.ToEquatableArray()));
            }

            attached.Add(result);
        }

        return attached;
    }

    /// <summary><c>CancelOrder</c> → <c>cancel</c>; <c>OrderApprovalRequest</c> → <c>approval</c>.</summary>
    private static string TransitionRoute(string requestName, string entityName)
    {
        var name = requestName;

        foreach (var suffix in new[] { "Request", "Command" })
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - suffix.Length);
                break;
            }
        }

        if (name.Length > entityName.Length && name.EndsWith(entityName, StringComparison.Ordinal))
        {
            name = name.Substring(0, name.Length - entityName.Length);
        }
        else if (name.Length > entityName.Length && name.StartsWith(entityName, StringComparison.Ordinal))
        {
            name = name.Substring(entityName.Length);
        }

        return Naming.ToKebabCase(name);
    }

    private static bool IsValidTransitionName(string name) =>
        name.Length > 0
        && name[0] is >= 'a' and <= 'z'
        && name[name.Length - 1] != '-'
        && name.IndexOf("--", StringComparison.Ordinal) < 0
        && name.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>Ad, kaynağın bir trait'inin ürettiği uçla çakışıyorsa o trait'in adını verir.</summary>
    private static string? ReservedRoute(ResourceModel resource, string route) => route switch
    {
        "restore" or "purge" when resource.Has(Traits.SoftDeletable) => nameof(Traits.SoftDeletable),
        "archive" or "unarchive" when resource.Has(Traits.Archivable) => nameof(Traits.Archivable),
        "expire" when resource.Expiry is not null => "Expiring",
        "cancel" when resource.Archetype == Archetype.Job => "MapJob",
        _ => null,
    };

    /// <summary>Çoktan-çoğa bağların iki ucunu da eşlenmiş kaynaklara çözer.</summary>
    public static List<AssociationModel> LinkAssociations(
        IEnumerable<AssociationDeclaration> declarations,
        IReadOnlyList<ResourceModel> resources,
        Action<DiagnosticInfo> report)
    {
        var byType = resources.ToDictionary(resource => resource.QualifiedName);
        var associations = new List<AssociationModel>();

        foreach (var declaration in declarations)
        {
            void Fail(string id, params string[] arguments) =>
                report(new DiagnosticInfo(id, declaration.Location, arguments.ToEquatableArray()));

            var left = Find(declaration.LeftName, declaration.LeftType, resources, byType);
            var right = Find(declaration.RightName, declaration.RightType, resources, byType);

            if (left is null || right is null)
            {
                // Bağın iki ucu da Map çağrısıyla eşlenmiş olmalı; yanıt tipleri ve anahtarlar oradan gelir.
                Fail("HALDUS009", left is null ? declaration.LeftName : declaration.RightName, "MapAssociation");
            }
            else if (left.QualifiedName == right.QualifiedName)
            {
                Fail("HALDUS007", $"'{left.EntityName}' cannot be associated with itself");
            }
            else if (new[] { left, right }.FirstOrDefault(end => !HasSingleColumnKey(end)) is { } unsupported)
            {
                Fail("HALDUS007", $"'{unsupported.EntityName}' cannot take part in an association: it has no single-column key (it is a singleton or a tenant-scoped store)");
            }
            else if (associations.All(existing => existing.LinkName != left.EntityName + right.EntityName))
            {
                var leftScoped = IsTenantScoped(left, resources, byType);
                var rightScoped = IsTenantScoped(right, resources, byType);

                // Bir uç silindiğinde bağları da silinir; tek istisna, genel bir ucun kiracıya ait bir uca
                // bağlı olmasıdır. O durumda genel ucu silen kişi başka kiracıların bağ kayıtlarını da
                // silmiş olurdu, bu yüzden silme reddedilir.
                associations.Add(new AssociationModel(
                    left,
                    right,
                    LeftOnDelete: !leftScoped && rightScoped ? OnDeleteBehavior.Restrict : OnDeleteBehavior.Cascade,
                    RightOnDelete: !rightScoped && leftScoped ? OnDeleteBehavior.Restrict : OnDeleteBehavior.Cascade));
            }
        }

        return associations;
    }

    /// <summary>
    /// Kaynağın kayıtları bir kiracıya mı ait: ya kendisi kiracıya bağlıdır ya da kiracıya ait bir ebeveynin
    /// altındadır.
    /// </summary>
    private static bool IsTenantScoped(
        ResourceModel resource,
        IReadOnlyList<ResourceModel> resources,
        Dictionary<string, ResourceModel> byType,
        int depth = 0)
    {
        if (resource.Has(Traits.MultiTenant))
        {
            return true;
        }

        var parent = resource.ReferenceDeclarations.FirstOrDefault(declaration => declaration.Kind == ReferenceKind.Parent);

        if (parent is null || depth >= 8)
        {
            return false;
        }

        // Başka bir modüldeki ebeveyn hakkında bilinen tek şey, kendisinin kiracıya bağlı olup olmadığıdır.
        return FindTarget(parent, resources, byType) is { } target
            ? IsTenantScoped(target, resources, byType, depth + 1)
            : parent is { IsExternal: true, TargetTenantScoped: true };
    }

    private static bool HasSingleColumnKey(ResourceModel resource) =>
        resource.Archetype != Archetype.Singleton && !(resource.Archetype.HasClientKey() && resource.Has(Traits.MultiTenant));

    private static ResourceModel? Find(
        string name,
        string? type,
        IReadOnlyList<ResourceModel> resources,
        Dictionary<string, ResourceModel> byType)
    {
        if (type is not null)
        {
            return byType.TryGetValue(type, out var exact) ? exact : null;
        }

        var named = resources.Where(resource => resource.EntityName == name).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    private static ResolvedReference? Resolve(
        ResourceModel resource,
        ReferenceDeclaration declaration,
        IReadOnlyList<ResourceModel> resources,
        Dictionary<string, ResourceModel> byType,
        List<PropertyModel> properties,
        Action<string, LocationInfo, string[]> fail)
    {
        var target = declaration.Kind == ReferenceKind.Self
            ? resource
            : FindTarget(declaration, resources, byType);

        // Başka bir modülde derlenmiş entity bu derlemede eşlenmez; anahtarı derlenmiş tipinden okunur.
        var external = target is null && declaration.IsExternal;

        if (target is null && (declaration.TargetType is null || (declaration.Kind == ReferenceKind.Parent && !external)))
        {
            // Hedef ne bilinen bir tip ne de bir Haldus kaynağı; ebeveyn ise adresi kurulamaz.
            fail("HALDUS009", declaration.Location, [declaration.TargetName, resource.EntityName]);
            return null;
        }

        if (external && (declaration.TargetKeyName is null || (declaration.TargetTenantScoped && declaration.TargetKeyName != "Id")))
        {
            fail(
                "HALDUS007",
                declaration.Location,
                [$"'{declaration.TargetName}' cannot be referenced: it has no single-column key (it is a singleton or a tenant-scoped store)"]);
            return null;
        }

        if (target is { Archetype: Archetype.Singleton }
            || (target is not null && target.Archetype.HasClientKey() && target.Has(Traits.MultiTenant)))
        {
            fail(
                "HALDUS007",
                declaration.Location,
                [$"'{target.EntityName}' cannot be referenced: it has no single-column key (it is a singleton or a tenant-scoped store)"]);
            return null;
        }

        var targetKeyName = target?.KeyName ?? declaration.TargetKeyName ?? "Id";
        var targetKeyType = target?.IdType ?? declaration.TargetKeyType ?? GuidType;
        var propertyName = declaration.PropertyName
            ?? (declaration.Kind == ReferenceKind.Self ? "Parent" + targetKeyName : declaration.TargetName + targetKeyName);

        var declared = properties.FirstOrDefault(property => property.Name == propertyName);
        var required = declaration.Kind switch
        {
            ReferenceKind.Parent => true,
            ReferenceKind.Self => false,
            _ => declared?.IsNonNullable ?? declaration.Required,
        };

        // Ebeveyn silinince altındakiler de silinir; ama kiracıya ait kayıtlar genel bir ebeveynin
        // altındaysa bu, ebeveyni silen kişinin başka kiracıların verisini silmesi demektir. Geliştirici
        // açıkça seçmediyse o durumda silme reddedilir.
        var onDelete = declaration.OnDelete;
        if (declaration is { Kind: ReferenceKind.Parent, OnDeleteExplicit: false }
            && (target is not null || external)
            && IsTenantScoped(resource, resources, byType)
            && !(target is null ? declaration.TargetTenantScoped : IsTenantScoped(target, resources, byType)))
        {
            onDelete = OnDeleteBehavior.Restrict;
        }

        if (required && declaration.OnDelete == OnDeleteBehavior.SetNull)
        {
            fail(
                "HALDUS007",
                declaration.Location,
                [$"'{propertyName}' is required, so it cannot be set to null when '{declaration.TargetName}' is deleted; make the reference optional or choose another delete behavior"]);
            return null;
        }

        var propertyType = declared?.Type ?? (required ? targetKeyType : targetKeyType + "?");

        if (declared is null)
        {
            properties.Add(ForeignKey(propertyName, targetKeyType, required, writable: declaration.Kind != ReferenceKind.Parent));
        }
        else if (declaration.Kind == ReferenceKind.Parent)
        {
            // Ebeveyn adresten gelir; kullanıcı alanı kendisi yazdıysa bile istek gövdesinden yazılamaz.
            properties[properties.IndexOf(declared)] = declared with { IsWritable = false };
        }

        return new ResolvedReference(
            declaration.Kind,
            propertyName,
            propertyType,
            required,
            target?.QualifiedName ?? declaration.TargetType!,
            declaration.TargetName,
            targetKeyName,
            targetKeyType,
            target is null && !external ? string.Empty : Naming.ToKebabCase(Naming.Pluralize(declaration.TargetName)),
            onDelete,
            IsExternal: external);
    }

    private static ResourceModel? FindTarget(
        ReferenceDeclaration declaration,
        IReadOnlyList<ResourceModel> resources,
        Dictionary<string, ResourceModel> byType)
    {
        if (declaration.TargetType is not null)
        {
            return byType.TryGetValue(declaration.TargetType, out var exact) ? exact : null;
        }

        // Hedef henüz üretilmemiş bir entity: derleyici tipini çözemez, bu yüzden adıyla aranır.
        var named = resources.Where(resource => resource.EntityName == declaration.TargetName).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    private static PropertyModel ForeignKey(string name, string keyType, bool required, bool writable)
    {
        var isString = keyType == "string";
        var kind = keyType switch
        {
            "string" => SearchKind.String,
            GuidType => SearchKind.Equality,
            _ => SearchKind.Range,
        };

        return new PropertyModel(
            name,
            required ? keyType : keyType + "?",
            NullableType: keyType + "?",
            IsWritable: writable,
            IsNonNullableReference: required && isString,
            IsNonNullableValue: required && !isString,
            kind,
            keyType,
            required && isString
                ? new EquatableArray<AttributeModel>([new AttributeModel(RequiredAttribute, IsRequired: true)])
                : EquatableArray<AttributeModel>.Empty,
            IsGenerated: true);
    }

    /// <summary>Aranabilecek alanlar: anahtar, skaler alanlar (üretilen yabancı anahtarlar dahil) ve trait'lerin açık alanları.</summary>
    private static List<SearchField> ResolveSearchFields(
        ResourceModel resource,
        List<PropertyModel> properties,
        Action<string, LocationInfo, string[]> fail)
    {
        var fields = new List<SearchField>();
        if (resource.SearchRequests.Length == 0)
        {
            return fields;
        }

        var keyKind = resource.IdKind switch
        {
            IdKind.Guid => SearchKind.Equality,
            IdKind.String => SearchKind.String,
            _ => SearchKind.Range,
        };

        var candidates = new Dictionary<string, SearchField>();
        if (resource.KeyName.Length > 0)
        {
            candidates[resource.KeyName] = new SearchField(resource.KeyName, keyKind, resource.IdType, IsNullable: false);
        }

        // Gömülü değer nesneleri tek bir koşul tipine oturmaz; aranamazlar.
        foreach (var property in properties.Where(property => !property.IsEmbedded))
        {
            candidates[property.Name] = new SearchField(
                property.Name, property.SearchKind, property.ValueType, IsNullable: !property.IsNonNullable);
        }

        foreach (var member in TraitCatalog.For(resource.Traits).SelectMany(definition => definition.Members).Concat(resource.ExtraMembers))
        {
            if (member.Search is not null)
            {
                candidates[member.Name] = member.Search;
            }
        }

        foreach (var request in resource.SearchRequests)
        {
            if (request.FieldName is not null && candidates.TryGetValue(request.FieldName, out var field))
            {
                if (fields.All(existing => existing.Name != field.Name))
                {
                    fields.Add(field);
                }
            }
            else
            {
                fail("HALDUS005", request.Location, [request.Expression, resource.EntityName]);
            }
        }

        return fields;
    }
}
