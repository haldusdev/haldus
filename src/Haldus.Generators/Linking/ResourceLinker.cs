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
        resources = PrepareDetails(MergeReverseAttachments(resources, report), report);

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

            if (resource.ReferenceDeclarations.Any(declaration => declaration.Attached) && AttachmentProblem(resource) is { } unattachable)
            {
                Fail("HALDUS007", resource.Location, $"'{resource.EntityName}' cannot be attached to other resources: {unattachable}");
                continue;
            }

            var properties = resource.Properties.ToList();
            var references = new List<ResolvedReference>();

            foreach (var declaration in resource.ReferenceDeclarations)
            {
                if (Resolve(resource, declaration, resources, byType, properties, Fail) is not { } reference)
                {
                    continue;
                }

                var existing = references.FirstOrDefault(candidate => candidate.PropertyName == reference.PropertyName);

                if (existing is null)
                {
                    references.Add(reference);
                }
                else if (existing.Kind == ReferenceKind.Source || reference.Kind == ReferenceKind.Source)
                {
                    // İki bildirim aynı alanı paylaşamaz: biri alanı istekle yazılabilir yapar, öteki yapmaz.
                    // Sessizce biri seçilseydi üretme ucu hiç oluşmazdı.
                    Fail(
                        "HALDUS007",
                        declaration.Location,
                        $"'{resource.EntityName}' already refers to '{reference.TargetName}' through '{reference.PropertyName}'; it cannot also be created from it");
                }
            }

            var searchFields = ResolveSearchFields(resource, properties, Fail);
            var sortFields = ResolveSortFields(resource, properties, Fail);
            MarkCreateOnly(resource, properties, Fail);
            var naturalKey = ResolveNaturalKey(resource, properties, Fail);
            var effectiveKeys = new List<string>();

            foreach (var request in resource.EffectiveKeyRequests)
            {
                // Dönemler bu alanların değeri başına ayrı tutulur; alan eşitlikle karşılaştırılabilmelidir.
                if (properties.FirstOrDefault(candidate => candidate.Name == request.FieldName) is { IsEmbedded: false } key
                    && key.Name is not ("EffectiveFrom" or "EffectiveTo"))
                {
                    effectiveKeys.Add(key.Name);
                }
                else
                {
                    Fail(
                        "HALDUS010",
                        request.Location,
                        $"'{request.Expression}' cannot separate the periods of '{resource.EntityName}': write it as 'item => item.Property', where the property is a scalar property of the entity");
                }
            }

            var translatable = new List<string>();

            foreach (var request in resource.TranslatableRequests)
            {
                // Çevrilen alan, istemcinin yazdığı bir metindir; çevirisi de aynı kurallarla doğrulanır.
                if (properties.FirstOrDefault(candidate => candidate.Name == request.FieldName)
                    is { IsWritable: true, IsEmbedded: false, IsGenerated: false, NullableType: "string?" } text)
                {
                    translatable.Add(text.Name);
                }
                else
                {
                    Fail(
                        "HALDUS010",
                        request.Location,
                        $"'{request.Expression}' cannot be translated: write it as 'item => item.Property', where the property is a writable string property of '{resource.EntityName}'");
                }
            }


            if (!failed)
            {
                linked.Add(resource with
                {
                    TranslatableFields = translatable.Distinct().ToEquatableArray(),
                    Properties = properties.ToEquatableArray(),
                    References = references.ToEquatableArray(),
                    SearchFields = searchFields.ToEquatableArray(),
                    SortFields = sortFields.ToEquatableArray(),
                    NaturalKey = naturalKey,
                    EffectiveKeys = effectiveKeys.Distinct().ToEquatableArray(),
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
                Rollups = ResolveRollups(resource, linked, resources, byType, report).ToEquatableArray(),
            }),
        ];
    }

    /// <summary>
    /// Toplanan alanların alt kaynağını ve o kaynakta bu kaynağa işaret eden alanı çözer. Alt kaynağın referansları
    /// ancak tüm kaynaklar bağlandıktan sonra bilinir.
    /// </summary>
    private static List<RollupModel> ResolveRollups(
        ResourceModel resource,
        List<ResourceModel> linked,
        IReadOnlyList<ResourceModel> resources,
        Dictionary<string, ResourceModel> byType,
        Action<DiagnosticInfo> report)
    {
        var resolved = new List<RollupModel>();

        foreach (var rollup in resource.Rollups)
        {
            void Fail(string id, params string[] arguments) =>
                report(new DiagnosticInfo(id, rollup.Location, arguments.ToEquatableArray()));

            var declaration = rollup.Field is null ? $"Counts<{rollup.ChildName}>()" : $"Sums<{rollup.ChildName}>()";
            var child = rollup.ChildType is not null
                ? linked.FirstOrDefault(candidate => candidate.QualifiedName == rollup.ChildType)
                : linked.Where(candidate => candidate.EntityName == rollup.ChildName).Take(2).ToList() is { Count: 1 } named ? named[0] : null;

            if (child is null)
            {
                Fail("HALDUS009", rollup.ChildName, resource.EntityName);
            }
            else if (!HasSingleColumnKey(resource))
            {
                Fail("HALDUS007", $"{declaration} needs '{resource.EntityName}' to have a key of its own");
            }
            else if (child.References.FirstOrDefault(reference => reference.TargetType == resource.QualifiedName) is not { } reference)
            {
                Fail(
                    "HALDUS007",
                    $"{declaration} needs '{child.EntityName}' to refer to '{resource.EntityName}'; declare References<{resource.EntityName}>() or Under<{resource.EntityName}>() on it");
            }
            else if (IsTenantScoped(child, resources, byType) && !IsTenantScoped(resource, resources, byType))
            {
                // Herkesin gördüğü bir kaydın alanı, her kiracının kaç kaydı olduğunu ötekilere gösterirdi.
                Fail(
                    "HALDUS007",
                    $"{declaration} would add up the records of every tenant on '{resource.EntityName}', which is not tenant-scoped; make '{resource.EntityName}' MultiTenant() or count a resource that is not");
            }
            else if (!rollup.AcrossOwners && IsOwnerScoped(child, resources, byType) && !IsOwnerScoped(resource, resources, byType))
            {
                // Başkalarının göremediği kayıtların sayısı veya toplamı, üst kaydı gören herkese açılırdı. Beğeni sayısı
                // gibi bilerek istenen durumlar açıkça yazılmalıdır.
                Fail(
                    "HALDUS007",
                    $"{declaration} would show everyone who can see '{resource.EntityName}' a figure made of records that belong to individual users; pass 'acrossOwners: true' if that is intended");
            }
            else
            {
                resolved.Add(rollup with { ChildType = child.QualifiedName, ForeignKey = reference.PropertyName });
            }
        }

        return resolved;
    }

    /// <summary>
    /// Kaynağın kayıtları tek bir kullanıcıya mı ait: ya kendisi sahiplidir ya da sahipli bir ebeveynin altındadır.
    /// </summary>
    private static bool IsOwnerScoped(
        ResourceModel resource,
        IReadOnlyList<ResourceModel> resources,
        Dictionary<string, ResourceModel> byType,
        int depth = 0)
    {
        if (resource.Has(Traits.Owned))
        {
            return true;
        }

        return depth < 8 && resource.ReferenceDeclarations
            .Where(declaration => declaration.Kind == ReferenceKind.Parent)
            .Any(parent => FindTarget(parent, resources, byType) is { } target && IsOwnerScoped(target, resources, byType, depth + 1));
    }

    /// <summary>
    /// Hedef tarafında yazılan takılabilir bildirimlerini (<c>Attachable&lt;T&gt;()</c>, <c>Commentable&lt;T&gt;()</c>)
    /// adı geçen kaynağın kendi <c>.AttachedTo&lt;T&gt;()</c> bildirimine çevirir; iki yazım aynı şeydir.
    /// </summary>
    private static List<ResourceModel> MergeReverseAttachments(IReadOnlyList<ResourceModel> resources, Action<DiagnosticInfo> report)
    {
        var byType = resources.GroupBy(resource => resource.QualifiedName).ToDictionary(group => group.Key, group => group.First());
        var added = new Dictionary<string, List<ReferenceDeclaration>>();

        foreach (var host in resources)
        {
            foreach (var declaration in host.ReverseAttachments)
            {
                void Fail(string id, params string[] arguments) =>
                    report(new DiagnosticInfo(id, declaration.Location, arguments.ToEquatableArray()));

                if (Find(declaration.ChildName, declaration.ChildType, resources, byType) is not { } child)
                {
                    Fail("HALDUS009", declaration.ChildName, host.EntityName);
                }
                else if (declaration.Kind == "Attachable" && child.File is null)
                {
                    Fail("HALDUS007", $"Attachable<{child.EntityName}>() needs a file resource; map '{child.EntityName}' with MapFile, or use Commentable for records without content");
                }
                else if (child.QualifiedName == host.QualifiedName)
                {
                    Fail("HALDUS007", $"'{host.EntityName}' cannot be attached to itself");
                }
                else
                {
                    if (!added.TryGetValue(child.QualifiedName, out var declarations))
                    {
                        added[child.QualifiedName] = declarations = [];
                    }

                    declarations.Add(new ReferenceDeclaration(
                        ReferenceKind.Parent,
                        host.EntityName,
                        host.QualifiedName,
                        TargetKeyType: null,
                        PropertyName: null,
                        OnDeleteBehavior.Cascade,
                        Required: false,
                        declaration.Location)
                    {
                        Attached = true,
                    });
                }
            }
        }

        return
        [
            .. resources.Select(resource => added.TryGetValue(resource.QualifiedName, out var declarations)
                ? resource with
                {
                    // Aynı hedef iki yönden de yazıldıysa bir kez sayılır.
                    ReferenceDeclarations = resource.ReferenceDeclarations
                        .Concat(declarations.Where(declaration => resource.ReferenceDeclarations.All(existing =>
                            !existing.Attached || (existing.TargetType ?? existing.TargetName) != (declaration.TargetType ?? declaration.TargetName))))
                        .GroupBy(declaration => (declaration.Attached, Target: declaration.TargetType ?? declaration.TargetName, declaration.Kind))
                        .SelectMany(group => group.Key.Attached ? group.Take(1) : group)
                        .ToEquatableArray(),
                }
                : resource),
        ];
    }

    /// <summary>
    /// Takılabilir bir kaynak her hedefin adresi altında ayrı ayrı sunulur; tek bir adrese, tek bir ebeveyne veya
    /// kendi builder'ına bağlı bildirimlerle birleşmez.
    /// </summary>
    private static string? AttachmentProblem(ResourceModel resource) =>
        resource.Archetype is not (Archetype.Crud or Archetype.AppendOnly)
            ? "it must be mapped with MapCrud, MapAppendOnly or MapFile"
        : resource.ReferenceDeclarations.Any(declaration => declaration is { Kind: ReferenceKind.Parent, Attached: false })
            ? "it is already placed under a parent with Under<T>()"
        : resource.ReferenceDeclarations.Any(declaration => declaration.Kind == ReferenceKind.Source)
            ? "it is created from another record with CreatedFrom<T>()"
        : resource.Has(Traits.Orderable)
            ? "it is Orderable"
        : resource.Has(Traits.CustomFields)
            ? "it has CustomFields, whose definitions are managed at a single address"
        : resource.Has(Traits.ChangeFeed)
            ? "it has a ChangeFeed, which could not tell whose deleted records it is listing"
        : resource.NaturalKeyRequest is not null
            ? "it has a NaturalKey"
        : resource.Workflow is not null
            ? "it has a state machine, whose transitions are declared for a single address"
        : resource.OwnedUnderMe
            ? "it lives under /me; use OwnedByCurrentUser(underMe: false)"
        : null;

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
                : owner.ReferenceDeclarations.Any(declaration => declaration.Attached)
                    ? $"'{owner.EntityName}' is attached to several resources and has no single address, so it cannot have details"
                : resource.IsSubresource && owner.Has(Traits.Approvable)
                    // Ayrı uçlu satır sahibinden bağımsız yazılır; onaylanmış içerik, onay düşmeden değişirdi.
                    ? $"'{owner.EntityName}' is Approvable, so its details cannot have endpoints of their own: a row written there would change approved content; "
                        + "keep the detail embedded (or AsCommands())"
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
            AxisModel? axis = null;

            if (Find(declaration.OwnerName, declaration.OwnerType, resources, byType) is not { } owner)
            {
                Fail("HALDUS007", $"{(declaration.IsAction ? "MapAction" : "MapTransition")} is declared on '{declaration.OwnerName}', which is not a mapped resource");
                continue;
            }

            if (owner.Attachments.Any())
            {
                // Takılabilir kaynak her hedefin altında ayrı ayrı eşlenir; builder'ı tek bir adres taşımaz.
                Fail("HALDUS007", $"{(declaration.IsAction ? "MapAction" : "MapTransition")} cannot be declared on '{owner.EntityName}', which is attached to several resources and has no single address");
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
                if (owner.Workflow is null && owner.Axes.Length == 0)
                {
                    Fail("HALDUS007", $"MapTransition needs a state machine; declare Workflow<TStatus>() on '{owner.EntityName}'");
                    continue;
                }

                if (!declaration.StatesReadable)
                {
                    Fail("HALDUS010", "The states of a transition must be written as enum members, for example from: OrderStatus.Draft, to: OrderStatus.Placed");
                    continue;
                }

                // Geçişin hangi eksende olduğunu durumlarının tipi söyler: asıl durum ya da ek eksenlerden biri.
                bool Known(StateReference state) =>
                    state.Type == owner.Workflow?.StatusType || owner.Axes.Any(candidate => candidate.StatusType == state.Type);

                if (declaration.From.Append(declaration.To!).FirstOrDefault(state => !Known(state)) is { } unknown)
                {
                    Fail("HALDUS010", $"'{unknown.Name}' is not a state of '{owner.EntityName}'; transitions use a status type declared with Workflow<TStatus>() or Axis<TStatus>()");
                    continue;
                }

                var statusType = declaration.To!.Type;
                axis = owner.Axes.FirstOrDefault(candidate => candidate.StatusType == statusType);

                if (declaration.From.FirstOrDefault(state => state.Type != statusType) is { } foreign)
                {
                    Fail("HALDUS010", $"'{foreign.Name}' and '{declaration.To.Name}' belong to different status types; a transition moves a record along one axis");
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
                Axis = axis,
            });
        }

        var attached = new List<ResourceModel>();

        foreach (var resource in resources)
        {
            var own = transitions[resource.QualifiedName];

            if (resource.Has(Traits.Document))
            {
                // Belgenin gönderilmesi ve iptali kendiliğinden gelir; kullanıcı aynı adla bir geçiş yazdıysa
                // (iş mantığı için) onunki geçerlidir.
                foreach (var (route, from, to) in new[] { ("submit", "Draft", "Submitted"), ("cancel", "Submitted", "Cancelled") })
                {
                    if (own.All(transition => transition.Route != route))
                    {
                        own.Add(new TransitionModel(
                            route,
                            char.ToUpperInvariant(route[0]) + route.Substring(1),
                            route,
                            RequestType: null,
                            ImplementsTransition: false,
                            new EquatableArray<string>([from]),
                            to)
                        {
                            IsImplicit = true,
                        });
                    }
                }
            }

            if (resource.Approval is { } approval
                && approval.RequiredFor.FirstOrDefault(name => own.All(transition => transition.Route != name)) is { } unknown)
            {
                report(new DiagnosticInfo("HALDUS010", resource.Location, new[]
                {
                    $"Approvable names '{unknown}', but '{resource.EntityName}' has no transition or action with that name",
                }.ToEquatableArray()));
            }

            var result = own.Count == 0 ? resource : resource with { Transitions = own.ToEquatableArray() };

            if (result.Numbering is { AssignOn: { } assignOn } && own.All(transition => transition.Axis is not null || transition.To != assignOn))
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
        "read" or "unread" when resource.Has(Traits.ReadTracking) => nameof(Traits.ReadTracking),
        "translations" when resource.Has(Traits.Translatable) => nameof(Traits.Translatable),
        "expire" when resource.Expiry is not null => "Expiring",
        "clone" when resource.Has(Traits.Cloneable) => nameof(Traits.Cloneable),
        "reverse" when resource.Has(Traits.Reversible) => nameof(Traits.Reversible),
        "amend" when resource.Has(Traits.Document) => nameof(Traits.Document),
        "revisions" when resource.Has(Traits.Revisioned) => nameof(Traits.Revisioned),
        "content" when resource.File is not null => "MapFile",
        "publish" or "unpublish" when resource.Has(Traits.Publishable) => nameof(Traits.Publishable),
        "approve" or "reject" or "approvals" when resource.Has(Traits.Approvable) => nameof(Traits.Approvable),
        "rotate" when resource.Secret is { Rotatable: true } => "Rotatable",
        _ when resource.Details.Any(detail => detail.IsCommandWritten && Naming.ToKebabCase(detail.CollectionName) == route) => "MapDetail",
        "supersede" when resource.Has(Traits.Supersedes) => nameof(Traits.Supersedes),
        "move" when resource.Has(Traits.Orderable) => nameof(Traits.Orderable),
        "preview" when resource.Has(Traits.Previewable) => nameof(Traits.Previewable),
        "cancel" when resource.Archetype == Archetype.Job => "MapJob",
        _ => null,
    };

    /// <summary>Çoktan-çoğa bağların iki ucunu da eşlenmiş kaynaklara çözer.</summary>
    public static List<AssociationModel> LinkAssociations(
        IEnumerable<AssociationDeclaration> declarations,
        IReadOnlyList<ResourceModel> resources,
        IReadOnlyList<TransitionDeclaration> transitions,
        Action<DiagnosticInfo> report)
    {
        var byType = resources.ToDictionary(resource => resource.QualifiedName);
        var associations = new List<AssociationModel>();

        foreach (var declaration in declarations.Where(declaration => !declaration.ForCurrentUser))
        {
            void Fail(string id, params string[] arguments) =>
                report(new DiagnosticInfo(id, declaration.Location, arguments.ToEquatableArray()));

            if (declaration.Problems.Length > 0)
            {
                foreach (var problem in declaration.Problems)
                {
                    Fail("HALDUS010", problem);
                }

                continue;
            }

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
            else if (new[] { left, right }.FirstOrDefault(end => end.Attachments.Any()) is { } attached)
            {
                Fail("HALDUS007", $"'{attached.EntityName}' is attached to several resources and has no single address, so it cannot be an end of an association");
            }
            else if (new[] { left, right }.FirstOrDefault(end => end.OwnedUnderMe) is { } personal)
            {
                Fail("HALDUS007", $"'{personal.EntityName}' lives under /me and cannot be an end of an association; use ForCurrentUser() to link the current user to a resource");
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
                    RightOnDelete: !rightScoped && leftScoped ? OnDeleteBehavior.Restrict : OnDeleteBehavior.Cascade)
                {
                    Payload = declaration.Payload,
                    LinkDeclared = declaration.LinkDeclared,
                    Workflow = declaration.Workflow,
                    StatusDeclared = declaration.StatusDeclared,
                });
            }
        }

        return AttachLinkTransitions(associations, transitions, report);
    }

    /// <summary>Bağların builder'ında bildirilen geçişleri bağlarına bağlar ve doğrular.</summary>
    private static List<AssociationModel> AttachLinkTransitions(
        List<AssociationModel> associations,
        IReadOnlyList<TransitionDeclaration> declarations,
        Action<DiagnosticInfo> report)
    {
        var attached = associations.ToDictionary(association => association.LinkName, _ => new List<TransitionModel>());

        foreach (var declaration in declarations.Distinct())
        {
            void Fail(string id, string message) =>
                report(new DiagnosticInfo(id, declaration.Location, new[] { message }.ToEquatableArray()));

            if (associations.FirstOrDefault(candidate => candidate.LinkName == declaration.OwnerName) is not { } association)
            {
                Fail("HALDUS007", $"MapTransition is declared on the association '{declaration.OwnerName}', which is not mapped (links of the current user have no state)");
            }
            else if (association.Workflow is not { } workflow)
            {
                Fail("HALDUS007", $"MapTransition needs a state machine; declare Workflow<TStatus>() on the association '{association.LinkName}'");
            }
            else if (!declaration.StatesReadable)
            {
                Fail("HALDUS010", "The states of a transition must be written as enum members, for example from: MembershipStatus.Invited, to: MembershipStatus.Active");
            }
            else if (declaration.From.Append(declaration.To!).FirstOrDefault(state => state.Type != workflow.StatusType) is { } foreign)
            {
                Fail("HALDUS010", $"'{foreign.Name}' is not a state of '{association.LinkName}'; transitions use the status type declared with Workflow<TStatus>()");
            }
            else if (!declaration.NameIsConstant || declaration.Name is null)
            {
                Fail("HALDUS010", "The name of a transition must be a constant; it becomes part of the generated route");
            }
            else if (!IsValidTransitionName(declaration.Name))
            {
                Fail("HALDUS010", $"'{declaration.Name}' is not a valid transition name; use lowercase words separated by '-', for example \"mark-paid\"");
            }
            else if (attached[association.LinkName].Any(transition => transition.Route == declaration.Name))
            {
                Fail("HALDUS007", $"'{association.LinkName}' already has a transition named '{declaration.Name}'");
            }
            else
            {
                attached[association.LinkName].Add(new TransitionModel(
                    declaration.Name,
                    string.Concat(declaration.Name.Split('-').Select(word => char.ToUpperInvariant(word[0]) + word.Substring(1))),
                    declaration.Name,
                    RequestType: null,
                    ImplementsTransition: false,
                    declaration.From.Select(state => state.Name).Distinct().ToEquatableArray(),
                    declaration.To!.Name));
            }
        }

        return
        [
            .. associations.Select(association => attached[association.LinkName] is { Count: > 0 } own
                ? association with { Transitions = own.ToEquatableArray() }
                : association),
        ];
    }

    /// <summary>
    /// Aktarma işlerini (<c>Exports</c> / <c>Imports</c>) aktardıkları kaynağa bağlar. İş hedefin adını ve
    /// namespace'ini, hedef ise kendisinden okunduğunu veya kendisine yazıldığını öğrenir; böylece hedefin uç
    /// sınıfında işin çağıracağı metot üretilir.
    /// </summary>
    public static List<ResourceModel> AttachTransfers(IReadOnlyList<ResourceModel> resources, Action<DiagnosticInfo> report)
    {
        var byType = resources.ToDictionary(resource => resource.QualifiedName);
        var exported = new HashSet<string>();
        var imported = new HashSet<string>();
        var resolved = new Dictionary<string, TransferModel?>();

        foreach (var job in resources.Where(resource => resource.Transfer is not null))
        {
            var transfer = job.Transfer!;

            void Fail(string id, params string[] arguments) =>
                report(new DiagnosticInfo(id, transfer.Location, arguments.ToEquatableArray()));

            var declaration = transfer.Export ? "Exports" : "Imports";
            resolved[job.QualifiedName] = null;

            if (Find(transfer.TargetName, transfer.TargetType, resources, byType) is not { } target)
            {
                Fail("HALDUS009", transfer.TargetName, job.EntityName);
            }
            else if (target.Archetype is not (Archetype.Crud or Archetype.ReadOnly or Archetype.AppendOnly) || target.Attachments.Any())
            {
                // Hedefin kayıtlarını veren ve oluşturan metotlar, kendi uç sınıfı olan bir koleksiyonda üretilir.
                Fail("HALDUS007", $"{declaration}<{target.EntityName}>() needs a resource mapped with MapCrud, MapReadOnly or MapAppendOnly that is not attached to other resources");
            }
            else if (IsTenantScoped(target, resources, byType) && !job.Has(Traits.MultiTenant))
            {
                // İşin kaydı ve ürettiği dosya, işi görebilen herkese açıktır. Kiracıya ait kayıtları aktaran iş
                // kiracıya ait değilse, bir kiracının verisi (ve hata raporu) ötekilere görünürdü.
                Fail("HALDUS007", $"{declaration}<{target.EntityName}>() moves tenant-owned records, so the job '{job.EntityName}' must be MultiTenant() as well; otherwise its file and its report would be visible across tenants");
            }
            else if (target.Has(Traits.Owned) && !job.Has(Traits.Owned))
            {
                Fail("HALDUS007", $"{declaration}<{target.EntityName}>() moves records that belong to a user, so the job '{job.EntityName}' must be OwnedByCurrentUser() as well; otherwise its file and its report would be visible to other users");
            }
            else if (!transfer.Export && (target.Batch is null || !target.Has(Operations.Create) || target.Parent is not null))
            {
                // İçe aktarma her satırı toplu oluşturmanın öğe yoluyla oluşturur; o yol kaynağın kendi adresindedir.
                Fail("HALDUS007", $"Imports<{target.EntityName}>() needs '{target.EntityName}' to declare Batch() with the Create operation and to live at its own address (not under a parent)");
            }
            else
            {
                (transfer.Export ? exported : imported).Add(target.QualifiedName);

                resolved[job.QualifiedName] = transfer with
                {
                    TargetType = target.QualifiedName,
                    TargetNamespace = target.Namespace,
                    NeedsTime = target.Expiry is { LifetimeTicks: not null },
                };
            }
        }

        return
        [
            .. resources.Select(resource => resource with
            {
                Transfer = resolved.TryGetValue(resource.QualifiedName, out var transfer) ? transfer : resource.Transfer,
                IsExported = exported.Contains(resource.QualifiedName),
                IsImported = imported.Contains(resource.QualifiedName),
            }),
        ];
    }

    /// <summary>
    /// Kayıtlar arası üretme ve türetme bildirimlerini çözer. İkisi de iki kaynağı ilgilendirir: üretilen kaynak
    /// kaynağının alanlarını ve satırlarını, kaynak ise kendisinden üretilen ve türeyen kaynakları bilmelidir. Bu
    /// yüzden geçişler de bağlandıktan sonra, ayrı bir adımda yapılır.
    /// </summary>
    public static List<ResourceModel> AttachOrigins(IReadOnlyList<ResourceModel> resources, Action<DiagnosticInfo> report)
    {
        var byType = resources.ToDictionary(resource => resource.QualifiedName);
        var origins = new Dictionary<string, List<OriginModel>>();
        var spawns = new Dictionary<string, List<SpawnModel>>();
        var derivations = new Dictionary<string, List<string>>();

        void Fail(LocationInfo location, string message) =>
            report(new DiagnosticInfo("HALDUS007", location, new[] { message }.ToEquatableArray()));

        foreach (var resource in resources)
        {
            foreach (var reference in resource.References.Where(reference => reference.Kind == ReferenceKind.Source))
            {
                var source = byType[reference.TargetType];
                var declaration = resource.ReferenceDeclarations.First(candidate =>
                    candidate.Kind == ReferenceKind.Source && candidate.TargetName == reference.TargetName);

                if (resource.Parent?.PropertyName == reference.PropertyName)
                {
                    Fail(declaration.Location, $"'{resource.EntityName}' already lives under '{source.EntityName}'; it cannot also be created from it");
                    continue;
                }

                if (source.Archetype is Archetype.Singleton or Archetype.Detail)
                {
                    Fail(declaration.Location, $"'{resource.EntityName}' cannot be created from '{source.EntityName}': the source has no address of its own");
                    continue;
                }

                var copied = resource.Properties
                    .Where(property => property.IsWritable && !property.IsEmbedded && property.Name != reference.PropertyName)
                    .Where(property => source.Properties.Any(candidate => candidate.Name == property.Name && candidate.Type == property.Type))
                    .Select(property => property.Name);

                if (declaration.Trigger is { } trigger)
                {
                    var problem =
                        source.Transitions.All(transition => transition.Route != trigger)
                            ? $"'{source.EntityName}' has no transition or action named '{trigger}' that could create '{resource.EntityName}'"
                        : resource.Parent is not null
                            ? $"'{resource.EntityName}' lives under a parent, so it cannot be created by a transition of '{source.EntityName}': the parent is not known there"
                        : resource.Numbering is { AssignOn: null } || resource.Has(Traits.Orderable)
                            ? $"'{resource.EntityName}' takes a number or a position when it is created, so it cannot be created by a transition of '{source.EntityName}'; use the from-{Naming.ToKebabCase(source.EntityName)} endpoint instead"
                        : resources.Any(other => other.DerivedFrom.Any(derived => derived.Type == resource.QualifiedName || (derived.Type is null && derived.Name == resource.EntityName)))
                            ? $"Other resources are derived from '{resource.EntityName}', so it cannot be created by a transition of '{source.EntityName}'; use the from-{Naming.ToKebabCase(source.EntityName)} endpoint instead"
                        : null;

                    if (problem is not null)
                    {
                        Fail(declaration.Location, problem);
                        continue;
                    }

                    Add(spawns, source.QualifiedName, new SpawnModel(
                        trigger,
                        resource.QualifiedName,
                        resource.Namespace,
                        resource.EntityName,
                        source.EntityName,
                        NeedsTime: resource.Expiry is { LifetimeTicks: not null }));
                }

                Add(origins, resource.QualifiedName, new OriginModel(
                    source.EntityName,
                    source.QualifiedName,
                    source.KeyName,
                    source.IdType,
                    Emit.ResourceEmitter.RouteConstraint(source),
                    reference.PropertyName,
                    source.Details.Select(detail => detail.CollectionName).ToEquatableArray(),
                    copied.ToEquatableArray(),
                    CopiesMetadata: resource.Has(Traits.Metadata) && source.Has(Traits.Metadata),
                    declaration.Trigger,
                    HasContract: resource.Contracts.Contains($"ICreatedFrom:{source.QualifiedName}")));
            }

            foreach (var operation in resource.Token?.Operations ?? EquatableArray<string>.Empty)
            {
                // Yanlış yazılmış bir ad sessizce yok sayılsaydı, token ile yapılabileceği sanılan işlem yapılamazdı.
                if (resource.Transitions.All(transition => transition.Route != operation))
                {
                    Fail(resource.Location, $"TokenAccess lists '{operation}', but '{resource.EntityName}' has no transition or action with that name");
                }
            }

            foreach (var declaration in resource.DerivedFrom)
            {
                var source = Find(declaration.Name, declaration.Type, resources, byType);

                if (source is null)
                {
                    report(new DiagnosticInfo("HALDUS009", declaration.Location, new[] { declaration.Name, resource.EntityName }.ToEquatableArray()));
                }
                else if (source.Archetype != Archetype.AppendOnly)
                {
                    // İzdüşüm yalnızca kayıt oluşturulurken çağrılır; değişen veya silinen bir kaynağı izleyemez.
                    Fail(declaration.Location, $"'{resource.EntityName}' can only be derived from an append-only resource, but '{source.EntityName}' is mapped with Map{source.Archetype}");
                }
                else
                {
                    Add(derivations, source.QualifiedName, resource.QualifiedName);
                }
            }
        }

        return
        [
            .. resources.Select(resource => resource with
            {
                Origins = Values(origins, resource.QualifiedName).ToEquatableArray(),
                Spawns = Values(spawns, resource.QualifiedName).ToEquatableArray(),
                Derivations = Values(derivations, resource.QualifiedName).Distinct().ToEquatableArray(),
            }),
        ];

        static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
        {
            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = [];
            }

            list.Add(value);
        }

        static List<T> Values<T>(Dictionary<string, List<T>> map, string key) => map.TryGetValue(key, out var list) ? list : [];
    }

    /// <summary>
    /// Geçerli kullanıcıya bağlı bağları çözer. Sol tip argümanı üretilecek bağ entity'sinin adıdır; sağ uç
    /// eşlenmiş bir kaynak olmalıdır.
    /// </summary>
    // Geçerli kullanıcıya bağlı bağın durumu yoktur; bildirim sessizce yok sayılmaz.
    private static bool RejectUserLinkState(AssociationDeclaration declaration, Action<DiagnosticInfo> report)
    {
        if (declaration.Workflow is null && declaration.Problems.Length == 0)
        {
            return false;
        }

        report(new DiagnosticInfo("HALDUS007", declaration.Location, new[]
        {
            "Workflow cannot be combined with ForCurrentUser(): a link of the current user is only a mark and has no state",
        }.ToEquatableArray()));

        return true;
    }

    public static List<UserLinkModel> LinkUserLinks(
        IEnumerable<AssociationDeclaration> declarations,
        IReadOnlyList<ResourceModel> resources,
        Action<DiagnosticInfo> report)
    {
        var byType = resources.ToDictionary(resource => resource.QualifiedName);
        var links = new List<UserLinkModel>();

        foreach (var declaration in declarations.Where(declaration => declaration.ForCurrentUser))
        {
            void Fail(string id, params string[] arguments) =>
                report(new DiagnosticInfo(id, declaration.Location, arguments.ToEquatableArray()));

            if (RejectUserLinkState(declaration, report))
            {
                continue;
            }

            if (Find(declaration.RightName, declaration.RightType, resources, byType) is not { } right)
            {
                Fail("HALDUS009", declaration.RightName, "MapAssociation");
            }
            else if (!HasSingleColumnKey(right))
            {
                Fail("HALDUS007", $"'{right.EntityName}' cannot take part in an association: it has no single-column key (it is a singleton or a tenant-scoped store)");
            }
            else if (resources.Any(resource => resource.EntityName == declaration.LeftName))
            {
                Fail("HALDUS007", $"'{declaration.LeftName}' names the link of a ForCurrentUser association, so it cannot also be mapped as a resource");
            }
            else if (links.All(existing => existing.LinkName != declaration.LeftName))
            {
                // Bağ entity'si yazılmışsa (partial) onun namespace'ine, yazılmamışsa her yazılmamış entity gibi
                // çağrının bulunduğu namespace'e üretilir; böylece çağrıdaki ad derlenirken çözülür.
                var qualified = declaration.LeftType?.Replace("global::", string.Empty);
                var @namespace = qualified is null
                    ? declaration.CallNamespace
                    : qualified.Contains('.') ? qualified.Substring(0, qualified.LastIndexOf('.')) : string.Empty;

                links.Add(new UserLinkModel(declaration.LeftName, @namespace, Declared: qualified is not null, right));
            }
        }

        return links;
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

        if (depth >= 8)
        {
            return false;
        }

        // Takılabilir kaynağın birkaç ebeveyni olabilir; biri kiracıya aitse kayıtlarının bir kısmı da öyledir.
        return resource.ReferenceDeclarations
            .Where(declaration => declaration.Kind == ReferenceKind.Parent)
            .Any(parent => FindTarget(parent, resources, byType) is { } target
                ? IsTenantScoped(target, resources, byType, depth + 1)

                // Başka bir modüldeki ebeveyn hakkında bilinen tek şey, kendisinin kiracıya bağlı olup olmadığıdır.
                : parent is { IsExternal: true, TargetTenantScoped: true });
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

        if (declaration.Kind is ReferenceKind.Parent or ReferenceKind.Source
            && target is not null
            && target.ReferenceDeclarations.Any(candidate => candidate.Attached))
        {
            // Takılabilir kaynağın tek bir adresi yoktur; altına yerleşen veya ondan üretilen kaynağın adresi kurulamaz.
            fail(
                "HALDUS007",
                declaration.Location,
                [$"'{resource.EntityName}' cannot be placed under or created from '{target.EntityName}', which is attached to several resources and has no single address"]);
            return null;
        }

        if (declaration.Kind == ReferenceKind.Source && target is null)
        {
            // Kaynağın alanları ve satırları bilinmeden kayıt ondan kurulamaz.
            fail(
                "HALDUS007",
                declaration.Location,
                [$"'{resource.EntityName}' cannot be created from '{declaration.TargetName}': the source must be mapped with a Map call in the same project"]);
            return null;
        }

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
            // Takılabilir kaynakta her hedefin alanı isteğe bağlıdır; bir kayıtta tam olarak biri doludur.
            ReferenceKind.Parent => !declaration.Attached,
            ReferenceKind.Self or ReferenceKind.Source => false,
            _ => declared?.IsNonNullable ?? declaration.Required,
        };

        // Ebeveyn adresten, kaynak üretme ucundan gelir. Adrese girmeyen ebeveyn ise oluşturma isteğinde verilir.
        var shallow = declaration is { Kind: ReferenceKind.Parent, Shallow: true };
        var settable = shallow || declaration.Kind is not (ReferenceKind.Parent or ReferenceKind.Source);

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
            // Adrese girmeyen ebeveyn kayıt oluşturulurken seçilir ve değişmez: kayıt başka bir ebeveynin altına
            // taşınamaz, tıpkı adresi ebeveyniyle başlayan bir kayıt gibi.
            properties.Add(ForeignKey(propertyName, targetKeyType, required, writable: settable) with { IsCreateOnly = shallow });
        }
        else if (shallow)
        {
            properties[properties.IndexOf(declared)] = declared with { IsCreateOnly = true };
        }
        else if (!settable)
        {
            // Ebeveyn adresten, kaynak üretme ucundan gelir; kullanıcı alanı kendisi yazdıysa bile istek
            // gövdesinden yazılamaz.
            properties[properties.IndexOf(declared)] = declared with { IsWritable = false };
        }

        return new ResolvedReference(
            shallow ? ReferenceKind.Reference : declaration.Attached ? ReferenceKind.Attachment : declaration.Kind,
            propertyName,
            propertyType,
            required,
            target?.QualifiedName ?? declaration.TargetType!,
            declaration.TargetName,
            targetKeyName,
            targetKeyType,
            target is not null
                ? target.CollectionRoute
                : external ? Naming.ToKebabCase(Naming.Pluralize(declaration.TargetName)) : string.Empty,
            onDelete,
            IsExternal: external)
        {
            IsShallowParent = shallow,
        };
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

    /// <summary>
    /// Liste ucunun sıralanabildiği alanlar. Aranabilen her alan sıralanabilir de; ikisi aynı adaylardan seçilir.
    /// </summary>
    private static List<SearchField> ResolveSortFields(
        ResourceModel resource,
        List<PropertyModel> properties,
        Action<string, LocationInfo, string[]> fail)
    {
        var fields = new List<SearchField>();
        if (resource.SortRequests.Length == 0)
        {
            return fields;
        }

        // Filtrenin sorgu parametresi sıralamanınkiyle aynı adı taşırsa biri diğerini gölgeler.
        if (resource.Filter?.Properties.Any(property => property.Name == "Sort") == true)
        {
            fail("HALDUS007", resource.Location, ["Sortable adds the 'sort' query parameter, which the filter of this resource already uses; rename the filter's Sort property"]);
        }

        var candidates = FieldCandidates(resource, properties);

        foreach (var request in resource.SortRequests)
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
                fail(
                    "HALDUS010",
                    request.Location,
                    [$"'{request.Expression}' cannot be sorted: write it as 'item => item.Property', where the property is the Id, a scalar property or a trait field of '{resource.EntityName}'"]);
            }
        }

        return fields;
    }

    /// <summary>
    /// <c>.CreateOnly(...)</c> ile sayılan alanları işaretler. Alan, isteklerde yer alan (yazılabilir) bir alan
    /// olmalıdır; üretilen yabancı anahtarlar da sayılabilir, böylece referans oluşturulduktan sonra değişmez.
    /// </summary>
    private static void MarkCreateOnly(
        ResourceModel resource,
        List<PropertyModel> properties,
        Action<string, LocationInfo, string[]> fail)
    {
        foreach (var request in resource.CreateOnlyRequests)
        {
            var index = request.FieldName is null ? -1 : properties.FindIndex(property => property.Name == request.FieldName);

            if (index < 0 || !properties[index].IsWritable)
            {
                fail(
                    "HALDUS010",
                    request.Location,
                    [$"'{request.Expression}' cannot be create-only: write it as 'item => item.Property', where the property is a writable property of '{resource.EntityName}'"]);
                continue;
            }

            properties[index] = properties[index] with { IsCreateOnly = true };
        }
    }

    /// <summary>Doğal anahtar, null olamayan ve adreste taşınabilen tek bir skaler alandır.</summary>
    private static NaturalKeyModel? ResolveNaturalKey(
        ResourceModel resource,
        List<PropertyModel> properties,
        Action<string, LocationInfo, string[]> fail)
    {
        if (resource.NaturalKeyRequest is not { } request)
        {
            return null;
        }

        var property = request.FieldName is null ? null : properties.FirstOrDefault(candidate => candidate.Name == request.FieldName);

        var constraint = property is { IsNonNullable: true, IsEmbedded: false }
            ? property.Type switch
            {
                "string" => string.Empty,
                "int" => ":int",
                "long" => ":long",
                "global::System.Guid" => ":guid",
                _ => null,
            }
            : null;

        if (property is null || constraint is null)
        {
            fail(
                "HALDUS010",
                request.Location,
                [$"'{request.Expression}' cannot be a natural key: write it as 'item => item.Property', where the property is a non-nullable string, int, long or Guid property of '{resource.EntityName}'"]);
            return null;
        }

        return new NaturalKeyModel(property.Name, property.Type, constraint);
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

        var candidates = FieldCandidates(resource, properties);

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

    /// <summary>Bir sorguda adıyla gösterilebilen alanlar: anahtar, skaler alanlar ve trait'lerin açık alanları.</summary>
    private static Dictionary<string, SearchField> FieldCandidates(ResourceModel resource, List<PropertyModel> properties)
    {
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

        return candidates;
    }
}
