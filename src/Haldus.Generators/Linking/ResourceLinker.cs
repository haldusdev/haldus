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

        // Silmeyi engelleyen bir referansın hedefi olan kaynaklar, silme ucunda bunu çakışma olarak bildirir.
        var restricted = new HashSet<string>(linked
            .SelectMany(resource => resource.References)
            .Where(reference => reference.OnDelete == OnDeleteBehavior.Restrict)
            .Select(reference => reference.TargetType));

        // Gömülü detaylar sahiplerine iliştirilir; sahibin DTO'ları ve uçları onları taşır.
        return
        [
            .. linked.Select(resource => resource with
            {
                IsReferenced = restricted.Contains(resource.QualifiedName),
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

    /// <summary>Bir bağın, silinmesi reddedilen ucu olan kaynakları işaretler; silme uçları bunu çakışma olarak bildirir.</summary>
    public static List<ResourceModel> MarkRestrictedEnds(IReadOnlyList<ResourceModel> resources, IReadOnlyList<AssociationModel> associations)
    {
        var restricted = new HashSet<string>(associations.SelectMany(association => new[]
        {
            association.LeftOnDelete == OnDeleteBehavior.Restrict ? association.Left.QualifiedName : null,
            association.RightOnDelete == OnDeleteBehavior.Restrict ? association.Right.QualifiedName : null,
        }).OfType<string>());

        return [.. resources.Select(resource =>
            restricted.Contains(resource.QualifiedName) ? resource with { IsReferenced = true } : resource)];
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

        return parent is not null
            && depth < 8
            && FindTarget(parent, resources, byType) is { } target
            && IsTenantScoped(target, resources, byType, depth + 1);
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

        if (target is null && (declaration.TargetType is null || declaration.Kind == ReferenceKind.Parent))
        {
            // Hedef ne bilinen bir tip ne de bir Haldus kaynağı; ebeveyn ise adresi kurulamaz.
            fail("HALDUS009", declaration.Location, [declaration.TargetName, resource.EntityName]);
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

        var targetKeyName = target?.KeyName ?? "Id";
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
            && target is not null
            && IsTenantScoped(resource, resources, byType)
            && !IsTenantScoped(target, resources, byType))
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
            target is null ? string.Empty : Naming.ToKebabCase(Naming.Pluralize(target.EntityName)),
            onDelete);
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

        foreach (var member in TraitCatalog.For(resource.Traits).SelectMany(definition => definition.Members))
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
