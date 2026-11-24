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

        return [.. linked.Select(resource => resource with { IsReferenced = restricted.Contains(resource.QualifiedName) })];
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
            declaration.OnDelete);
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

        foreach (var property in properties)
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
