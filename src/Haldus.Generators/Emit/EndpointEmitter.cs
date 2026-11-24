using System.Text;
using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;

namespace Haldus.Generators.Emit;

/// <summary>Bir kaynağın Minimal API endpoint'lerini archetype'a, seçilen işlemlere ve trait'lere göre üretir.</summary>
internal sealed partial class EndpointEmitter
{
    private const string Task = "global::System.Threading.Tasks.Task";
    private const string Http = "global::Microsoft.AspNetCore.Http";
    private const string Results = "global::Microsoft.AspNetCore.Http.HttpResults";
    private const string Problem = "global::Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult";
    private const string Problems = "global::Haldus.HaldusProblems";

    private const string ContextsParameter = "        global::Haldus.IHaldusDbContextProvider contexts,\n";
    private const string MessagesParameter = "        global::Haldus.IHaldusMessages messages,\n";
    private const string HttpContextParameter = "        global::Microsoft.AspNetCore.Http.HttpContext httpContext,\n";
    private const string TenantsParameter = "        global::Haldus.ITenantProvider tenants,\n";
    private const string TimeProviderParameter = "        global::System.TimeProvider timeProvider,\n";
    private const string Now = "global::Haldus.HaldusTime.UtcNow(timeProvider)";
    private const string CancellationParameter = "        global::System.Threading.CancellationToken cancellationToken)\n";

    private readonly ResourceModel _resource;
    private readonly string _entity;
    private readonly string _plural;
    private readonly string _key;
    private readonly string _keyParameter;
    private readonly string _keyRoute;
    private readonly bool _concurrency;
    private readonly bool _tenant;
    private readonly PropertyModel[] _writable;
    private readonly PropertyModel[] _updatable;
    private readonly StringBuilder _map = new();
    private readonly StringBuilder _handlers = new();
    private readonly StringBuilder _transitionMaps = new();

    private EndpointEmitter(ResourceModel resource)
    {
        _resource = resource;
        _entity = resource.EntityName;
        _plural = Naming.Pluralize(resource.EntityName);
        _key = resource.KeyName;
        _keyParameter = _key.Length == 0 ? string.Empty : ResourceEmitter.CamelCase(_key);
        _keyRoute = resource.IdKind switch
        {
            IdKind.Int32 => $"/{{{_keyParameter}:int}}",
            IdKind.Int64 => $"/{{{_keyParameter}:long}}",
            IdKind.String => $"/{{{_keyParameter}}}",
            _ => $"/{{{_keyParameter}:guid}}",
        };
        _concurrency = resource.Has(Traits.ConcurrencyChecked);
        _tenant = resource.Has(Traits.MultiTenant);
        _writable = [.. resource.Properties.Where(property => property.IsWritable)];
        _updatable = [.. _writable.Where(property => !property.IsCreateOnly)];
    }

    private NaturalKeyModel? NaturalKey => _resource.NaturalKey;

    private bool HasMetadata => _resource.Has(Traits.Metadata);

    /// <summary>Oluşturma bir hata yanıtıyla dönebilir mi: referans bulunamayabilir veya doğal anahtar çakışabilir.</summary>
    private bool CreateCanFail => ChecksReferences || NaturalKey is not null || EffectiveDated;

    private bool EffectiveDated => _resource.Has(Traits.EffectiveDated);

    private string OkOrProblem => $"{Task}<{Results}.Results<{Results}.Ok<{_entity}Response>, {Problem}>>";

    private string NoContentOrProblem => $"{Task}<{Results}.Results<{Results}.NoContent, {Problem}>>";

    private string KeyParameter => $"        {_resource.IdType} {_keyParameter},\n";

    private string ResponseAssignments => ResourceEmitter.ResponseAssignments(_resource);

    /// <summary>Tek bir kayıt yüklenirken gömülü detayları da getirir; yanıt ve eşitleme onlara ihtiyaç duyar.</summary>
    private string Includes => string.Concat(_resource.Details.Select(detail => $".Include(item => item.{detail.CollectionName})"));


    private string NotFound => $"{Problems}.NotFound(messages, \"{_entity}\", {_keyParameter})";

    /// <summary>Ömrü sunucu veriyor mu; veriyorsa son kullanma zamanı oluşturulurken hesaplanır.</summary>
    private bool HasLifetime => _resource.Expiry is { LifetimeTicks: not null };

    /// <summary>Her başarılı yazmada ömür yeniden başlar mı.</summary>
    private bool Sliding => _resource.Expiry is { LifetimeTicks: not null, Sliding: true };

    private bool Idempotent => _resource.Has(Traits.Idempotent);

    private ResolvedReference? Parent => _resource.Parent;

    private string ParentParameter => Parent is null ? string.Empty : ResourceEmitter.CamelCase(Parent.PropertyName);

    /// <summary>Kaynak bir ebeveynin altındaysa her handler'ın ilk parametresi ebeveynin anahtarıdır.</summary>
    private string ScopeParameter => Parent is null ? string.Empty : $"        {Parent.TargetKeyType} {ParentParameter},\n";

    /// <summary>Kaydı anahtarıyla ve, varsa, ebeveyniyle eşleştirir; başka bir ebeveynin kaydı bulunamaz sayılır.</summary>
    private string Match => $"item.{_key} == {_keyParameter}{ScopeCondition("item")}";

    private string ScopeFilter => Parent is null ? string.Empty : $".Where(item => {ScopeCondition("item").Substring(4)})";

    private bool ChecksReferences => _resource.References.Length > 0;

    /// <summary>
    /// Kaydın adresteki ebeveyne ait olduğunu şart koşar. Ebeveynin isteği yapana görünür olması ayrıca
    /// burada kontrol edilmez: onu, kaynağın modeldeki sorgu filtresi (<c>HaldusFilters.Parent</c>) her
    /// sorguda zaten sağlar.
    /// </summary>
    private string ScopeCondition(string variable) =>
        Parent is null ? string.Empty : $" && {variable}.{Parent.PropertyName} == {ParentParameter}";

    private static string RouteConstraint(string keyType) => keyType switch
    {
        "int" => ":int",
        "long" => ":long",
        "string" => string.Empty,
        _ => ":guid",
    };

    public static string Emit(ResourceModel resource, string entityNamespace) =>
        new EndpointEmitter(resource).Build(entityNamespace);

    private string Build(string entityNamespace)
    {
        string route;

        if (_resource.Archetype == Archetype.Singleton)
        {
            // Tek kayıt koleksiyon değildir; adresi de tekildir.
            route = Naming.ToKebabCase(_entity);
            EmitSingleton();
        }
        else
        {
            route = Naming.ToKebabCase(_plural);
            EmitCollection();
        }

        if (Parent is not null)
        {
            // Ebeveynin altında yaşayan kaynağın adresi ebeveyninkiyle başlar: /projects/{projectId}/tasks
            // Ayrı uçlu bir detay ise zaten sahibinin grubuna eşlenir; sahibin adresi yeniden yazılmaz.
            var parentKey = $"{{{ParentParameter}{RouteConstraint(Parent.TargetKeyType)}}}/{route}";
            route = _resource.RouteRelativeToParent ? parentKey : $"{Parent.TargetRoute}/{parentKey}";
        }
        else if (_resource.OwnedUnderMe)
        {
            route = "me/" + route;
        }

        var tenantFilter = _tenant
            ? "        group.AddEndpointFilter<global::Haldus.HaldusTenantRequiredFilter>();\n\n"
            : string.Empty;

        // Kullanıcıya ait kaynakta (ve /me altındaki her adreste) kullanıcısı çözülemeyen istek hiçbir kayda
        // ulaşamaz; boş bir liste yerine nedenini söyleyen 401 döner.
        if (_resource.Has(Traits.Owned) || route.StartsWith("me/", StringComparison.Ordinal))
        {
            tenantFilter += "        group.AddEndpointFilter<global::Haldus.HaldusUserRequiredFilter>();\n\n";
        }

        return $$"""
            {{ResourceEmitter.Header}}
            using System.Linq;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Routing;
            using Microsoft.EntityFrameworkCore;

            {{ResourceEmitter.NamespaceLine(entityNamespace)}}
            public static partial class {{_entity}}Endpoints
            {
            {{Constants(entityNamespace)}}    public static global::Microsoft.AspNetCore.Routing.RouteGroupBuilder Map(
                    global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
                {
                    var group = endpoints.MapGroup("/{{route}}").WithTags("{{(_resource.Archetype == Archetype.Singleton ? _entity : _plural)}}");

            {{tenantFilter}}{{_map}}        return group;
                }
            {{_transitionMaps}}{{_handlers}}
                private static {{_entity}}Response ToResponse({{_entity}} item) => new() { {{ResponseAssignments}} };
            }

            """;
    }

    private void EmitCollection()
    {
        if (_resource.Has(Operations.List))
        {
            if (_resource.Archetype == Archetype.Lookup)
            {
                EmitLookupList();
            }
            else
            {
                EmitList();
            }
        }

        if (_resource.SearchFields.Length > 0)
        {
            EmitSearch();
        }

        if (_resource.Has(Operations.Get))
        {
            EmitGet();

            if (NaturalKey is not null)
            {
                EmitGetByNaturalKey();
            }
        }

        if (_resource.Has(Operations.Create))
        {
            EmitCreate();
        }

        if (_resource.Has(Operations.Update))
        {
            if (_resource.Archetype.HasClientKey())
            {
                EmitPut();
            }
            else
            {
                EmitWrite("Update", "MapPut", UpdateAssignments());
            }
        }

        if (_resource.Has(Operations.Patch))
        {
            EmitWrite("Patch", "MapPatch", PatchAssignments());
        }

        if (_resource.Has(Operations.Delete))
        {
            EmitDelete();
        }

        // Geri yükleme ve kalıcı silme, silmenin kendisi gibi istemci yazmalarıdır; yalnızca silebilen
        // archetype'ta anlamlıdır.
        if (_resource.Has(Traits.SoftDeletable) && _resource.Archetype == Archetype.Crud)
        {
            EmitRestore();
            EmitPurge();
        }

        if (_resource.Has(Traits.Archivable) && _resource.Archetype != Archetype.ReadOnly)
        {
            EmitArchive("Archive", "archive", "entity.ArchivedAt ??= global::Haldus.HaldusTime.UtcNow(timeProvider);");
            EmitArchive("Unarchive", "unarchive", "entity.ArchivedAt = null;");
        }

        if (_resource.Expiry is not null)
        {
            EmitExpire();
        }

        if (_resource.Archetype == Archetype.Job)
        {
            EmitJobCancel();
        }

        if (_resource.Has(Traits.Previewable))
        {
            EmitPreview();
        }

        if (_resource.Has(Traits.Cloneable))
        {
            EmitClone();
        }

        if (Orderable)
        {
            EmitMove();
        }

        foreach (var chain in ChainOperation.For(_resource))
        {
            EmitChain(chain);
        }

        foreach (var origin in _resource.Origins)
        {
            EmitOrigin(origin);
        }

        if (_resource.Secret is { Rotatable: true })
        {
            EmitRotate();
        }

        if (_resource.Token is not null)
        {
            EmitTokenRead();
        }

        if (_resource.Batch is not null)
        {
            EmitBatch();
        }

        foreach (var transition in _resource.Transitions)
        {
            EmitTransition(transition);

            if (transition.IsImplicit)
            {
                // Kullanıcının yazmadığı geçiş (belgenin gönderilmesi, iptali) standart uçlarla birlikte eşlenir.
                _map.Append($"        {transition.MapperName}(group, apply: null);\n\n");
            }
        }

        if (_resource.Approval is not null)
        {
            EmitApproval(approve: true);
            EmitApproval(approve: false);
            EmitApprovalList();
        }

        if (_resource.Has(Traits.Revisioned))
        {
            EmitRevisions();
        }

        if (_resource.Has(Traits.ChangeFeed))
        {
            EmitChanges();
        }

        if (_resource.Has(Traits.Publishable))
        {
            EmitPublishing();
        }

        if (_resource.Has(Operations.Update) || _resource.Has(Operations.Patch))
        {
            EmitCycleHelper();
            EmitSyncHelpers();
        }

        foreach (var detail in _resource.Details.Where(detail => detail.IsCommandWritten))
        {
            EmitDetailCommands(detail);
        }
    }

    /// <summary>Numara deseni ve ömür gibi, uçların paylaştığı sabitler.</summary>
    private string Constants(string entityNamespace)
    {
        var builder = new StringBuilder();

        if (_resource.Numbering is { } numbering)
        {
            // Sayaç adı tam tip adıdır; farklı namespace'lerdeki aynı adlı entity'ler sayaç paylaşmaz.
            var counter = entityNamespace.Length == 0 ? _entity : $"{entityNamespace}.{_entity}";
            var sequence = numbering.Gapless ? string.Empty : $", sequence: {ResourceEmitter.Literal(ResourceEmitter.NumberSequence(_resource))}";

            builder.Append(
                $"    private static readonly global::Haldus.NumberFormat Numbering = new({ResourceEmitter.Literal(counter)}, "
                + $"{ResourceEmitter.Literal(numbering.Pattern)}, gapless: {(numbering.Gapless ? "true" : "false")}, "
                + $"perTenant: {(_tenant ? "true" : "false")}{sequence});\n\n");
        }

        if (_resource.Expiry is { LifetimeTicks: { } ticks })
        {
            builder.Append(
                $"    private static readonly global::System.TimeSpan Lifetime = global::System.TimeSpan.FromTicks({ticks}L);\n\n");
        }

        return builder.ToString();
    }

    private void EmitList()
    {
        var softDelete = _resource.Has(Traits.SoftDeletable);
        var archive = _resource.Has(Traits.Archivable);

        // Sıralanabilir liste, sayılmayan bir alan istendiğinde hata yanıtıyla dönebilir.
        var sortable = _resource.SortFields.Length > 0;

        _map.Append($$"""
                    group.MapGet("/", ListAsync)
                        .WithName("List{{_plural}}"){{(sortable ? "\n            .ProducesValidationProblem()" : string.Empty)}};


            """);

        var self = _resource.SelfReference;
        var treeParameters = self is null
            ? string.Empty
            : $"        {self.TargetKeyType}? {ResourceEmitter.CamelCase(self.PropertyName)},\n        bool? roots,\n";

        // Adrese girmeyen ebeveyn: liste ebeveynler arasında döner ve ebeveyne göre süzülebilir.
        var shallow = _resource.ShallowParent;
        var shallowParameter = shallow is null ? string.Empty : ResourceEmitter.CamelCase(shallow.PropertyName);
        if (shallow is not null)
        {
            treeParameters += $"        {shallow.TargetKeyType}? {shallowParameter},\n";
        }

        // Durum makinesi olan kaynak duruma göre süzülebilir; kullanıcının filtresi aynı adı taşıyorsa o geçerlidir.
        var status = _resource.Filter?.Properties.Any(property => property.Name == "Status") != true
            ? _resource.StatusType
            : null;
        var statusParameter = (status is null ? string.Empty : $"        {status}? status,\n")
            + (EffectiveDated ? "        global::System.DateOnly? asOf,\n" : string.Empty);

        var paged = $"{Results}.Ok<global::Haldus.PagedResult<{_entity}Response>>";
        var listResult = sortable ? $"{Results}.Results<{paged}, {Problem}>" : paged;

        _handlers.Append($$"""

                private static async {{Task}}<{{listResult}}> ListAsync(
            {{ScopeParameter}}        [{{Http}}.AsParameters] global::Haldus.PageRequest page,
            {{(sortable ? "        string? sort,\n" : string.Empty)}}{{(softDelete ? "        bool? includeDeleted,\n" : string.Empty)}}{{(archive ? "        bool? includeArchived,\n" : string.Empty)}}{{treeParameters}}{{statusParameter}}{{FilterParameters()}}{{ContextsParameter}}{{(sortable ? MessagesParameter : string.Empty)}}{{CancellationParameter}}    {
                    var query = contexts.For<{{_entity}}>().Set<{{_entity}}>().AsNoTracking(){{ScopeFilter}};

            """);

        if (softDelete)
        {
            _handlers.Append("""

                        if (includeDeleted == true)
                        {
                            query = query.IgnoreQueryFilters(new[] { global::Haldus.HaldusFilters.SoftDelete });
                        }

                """);
        }

        if (archive)
        {
            _handlers.Append("""

                        if (includeArchived != true)
                        {
                            query = query.Where(item => item.ArchivedAt == null);
                        }

                """);
        }

        if (self is not null)
        {
            // Ağaç düzey düzey gezilebilsin diye: belirli bir düğümün çocukları veya yalnızca kökler.
            var parentFilter = ResourceEmitter.CamelCase(self.PropertyName);

            _handlers.Append($$"""

                        if ({{parentFilter}} is { } parentValue)
                        {
                            query = query.Where(item => item.{{self.PropertyName}} == parentValue);
                        }
                        else if (roots == true)
                        {
                            query = query.Where(item => item.{{self.PropertyName}} == null);
                        }

                """);
        }

        if (shallow is not null)
        {
            _handlers.Append($$"""

                        if ({{shallowParameter}} is { } underValue)
                        {
                            query = query.Where(item => item.{{shallow.PropertyName}} == underValue);
                        }

                """);
        }

        if (status is not null)
        {
            _handlers.Append("""

                        if (status is { } statusValue)
                        {
                            query = query.Where(item => item.Status == statusValue);
                        }

                """);
        }

        if (EffectiveDated)
        {
            // Dönem yarı açıktır: başlangıç günü dahil, bitiş günü hariç.
            _handlers.Append("""

                        if (asOf is { } day)
                        {
                            query = query.Where(item => item.EffectiveFrom <= day && (item.EffectiveTo == null || item.EffectiveTo > day));
                        }

                """);
        }

        _handlers.Append(ApplyFilter());

        if (sortable)
        {
            _handlers.Append($$"""

                        if (!TrySort(query, sort, out var sorted, out var unknownSortField))
                        {
                            return {{Problems}}.SortInvalid(messages, unknownSortField, SortableFields);
                        }

                """);
        }

        // Sıralı kaynakta liste, başka bir sıra istenmedikçe kayıtların kendi sırasıyla döner.
        var source = sortable
            ? "sorted"
            : Orderable
                ? $"query\n            .OrderBy(item => item.Position)\n            .ThenBy(item => item.{_key})"
                : $"query\n            .OrderBy(item => item.{_key})";

        _handlers.Append($$"""

                    var totalCount = await query.LongCountAsync(cancellationToken);
                    var items = await {{source}}
                        .Skip(page.Skip)
                        .Take(page.PageSizeOrDefault)
                        .Select(item => new {{_entity}}Response { {{ResponseAssignments}} })
                        .ToListAsync(cancellationToken);

                    return {{Http}}.TypedResults.Ok(
                        new global::Haldus.PagedResult<{{_entity}}Response>(items, page.PageOrDefault, page.PageSizeOrDefault, totalCount));
                }

            """);

        if (sortable)
        {
            EmitSortHelper();
        }
    }

    /// <summary>
    /// <c>?sort=-createdAt,name</c> ifadesini sorguya uygular. Yalnızca <c>.Sortable(...)</c> ile sayılan alanlar
    /// tanınır; tanınmayan bir alan sessizce yok sayılmaz, çünkü istemci sonucun sıralı olduğunu sanardı.
    /// </summary>
    private void EmitSortHelper()
    {
        var fields = _resource.SortFields;
        var names = string.Join(", ", fields.Select(field => ResourceEmitter.CamelCase(field.Name)));
        var cases = string.Concat(fields.Select(field => $$"""
                            case "{{ResourceEmitter.CamelCase(field.Name)}}":
                                ordered = global::Haldus.HaldusSearch.OrderBy(query, ordered, item => item.{{field.Name}}, descending);
                                break;

            """));

        _handlers.Append($$"""

                private const string SortableFields = "{{names}}";

                private static bool TrySort(
                    global::System.Linq.IQueryable<{{_entity}}> query,
                    string? sort,
                    out global::System.Linq.IOrderedQueryable<{{_entity}}> sorted,
                    out string unknown)
                {
                    global::System.Linq.IOrderedQueryable<{{_entity}}>? ordered = null;
                    var terms = (sort ?? string.Empty).Split(
                        ',', global::System.StringSplitOptions.RemoveEmptyEntries | global::System.StringSplitOptions.TrimEntries);
                    sorted = null!;
                    unknown = string.Empty;

                    // Bir alanı yinelemenin anlamı yoktur; terim sayısını sınırlamak sorgunun büyümesini de sınırlar.
                    if (terms.Length > {{fields.Length}})
                    {
                        unknown = terms[{{fields.Length}}].TrimStart('-');
                        return false;
                    }

                    foreach (var term in terms)
                    {
                        var descending = term[0] == '-';
                        var name = descending ? term.Substring(1) : term;

                        switch (name)
                        {
            {{cases}}                default:
                                unknown = name;
                                return false;
                        }
                    }

            {{(Orderable ? "        // Sıra istenmediyse kayıtların kendi sırası geçerlidir.\n        ordered ??= query.OrderBy(item => item.Position);\n\n" : string.Empty)}}        // Sayfalama kararlı olsun diye sıralama her zaman anahtarla tamamlanır.
                    sorted = global::Haldus.HaldusSearch.OrderBy(query, ordered, item => item.{{_key}}, descending: false);
                    return true;
                }

            """);
    }

    /// <summary>Kaydı doğal anahtarıyla okur: <c>GET /coupons/by-code/{code}</c>.</summary>
    private void EmitGetByNaturalKey()
    {
        var key = NaturalKey!;
        var parameter = ResourceEmitter.CamelCase(key.PropertyName);
        var segment = Naming.ToKebabCase(key.PropertyName);

        _map.Append($$"""
                    group.MapGet("/by-{{segment}}/{{{parameter}}{{key.RouteConstraint}}}", GetBy{{key.PropertyName}}Async)
                        .WithName("Get{{_entity}}By{{key.PropertyName}}")
                        .ProducesProblem(404);


            """);

        // Değer ebeveyn içinde benzersizdir; ebeveyn adreste değilse hangisinin kastedildiği sorguda söylenir.
        var under = _resource.ShallowParent;
        var underParameter = under is null ? string.Empty : $"        {under.TargetKeyType} {ResourceEmitter.CamelCase(under.PropertyName)},\n";
        var underCondition = under is null ? string.Empty : $" && item.{under.PropertyName} == {ResourceEmitter.CamelCase(under.PropertyName)}";

        _handlers.Append($$"""

                private static async {{OkOrProblem}} GetBy{{key.PropertyName}}Async(
            {{ScopeParameter}}        {{key.Type}} {{parameter}},
            {{underParameter}}{{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var entity = await contexts.For<{{_entity}}>().Set<{{_entity}}>()
                        .AsNoTracking(){{Includes}}
                        .FirstOrDefaultAsync(item => item.{{key.PropertyName}} == {{parameter}}{{ScopeCondition("item")}}{{underCondition}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{Problems}}.NotFound(messages, "{{_entity}}", {{parameter}});
                    }

            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """);
    }

    /// <summary>Lookup küçük ve kararlı bir listedir; sayfalamadan, koda göre sıralı ve tam döner.</summary>
    private void EmitLookupList()
    {
        var archive = _resource.Has(Traits.Archivable);

        _map.Append($$"""
                    group.MapGet("/", ListAsync)
                        .WithName("List{{_plural}}");


            """);

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Ok<global::System.Collections.Generic.List<{{_entity}}Response>>> ListAsync(
            {{(archive ? "        bool? includeArchived,\n" : string.Empty)}}{{ContextsParameter}}{{CancellationParameter}}    {
                    var query = contexts.For<{{_entity}}>().Set<{{_entity}}>().AsNoTracking(){{ScopeFilter}};

            """);

        if (archive)
        {
            _handlers.Append("""

                        if (includeArchived != true)
                        {
                            query = query.Where(item => item.ArchivedAt == null);
                        }

                """);
        }

        _handlers.Append($$"""

                    var items = await query
                        .OrderBy(item => item.{{_key}})
                        .Select(item => new {{_entity}}Response { {{ResponseAssignments}} })
                        .ToListAsync(cancellationToken);

                    return {{Http}}.TypedResults.Ok(items);
                }

            """);
    }

    private void EmitGet()
    {
        _map.Append($$"""
                    group.MapGet("{{_keyRoute}}", GetAsync)
                        .WithName("Get{{_entity}}")
                        .ProducesProblem(404);


            """);

        _handlers.Append($$"""

                private static async {{OkOrProblem}} GetAsync(
            {{ScopeParameter}}{{KeyParameter}}{{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var entity = await contexts.For<{{_entity}}>().Set<{{_entity}}>()
                        .AsNoTracking(){{Includes}}
                        .FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """);
    }

    /// <summary>
    /// Yeni kaydın başlangıç değerleri: Haldus'un atadıkları (kimlik, ebeveyn, durum, ömür) ve istekten
    /// gelen alanlar. Tekil ve toplu oluşturma aynı ifadeyi kullanır.
    /// </summary>
    /// <param name="source">Alanların okunduğu nesne: oluşturmada istek, kopyalamada kaynak kayıt.</param>
    /// <param name="preview">Kayıt kaydedilmeyecek mi; öyleyse kimlik üretilmez.</param>
    /// <param name="only">Yalnızca bu adlı alanlar okunur; kaynağı başka tipte bir kayıt olan yeni kayıtlar için.</param>
    /// <param name="metadata">Metadata çantası da okunsun mu.</param>
    private string CreateInitializer(string source = "request", bool preview = false, IEnumerable<string>? only = null, bool metadata = true)
    {
        var keys = new List<string>();
        if (preview)
        {
            // Önizlenen kayıt var olmayacak; üretilmiş bir kimlik göstermek yanıltıcı olurdu.
        }
        else if (_resource.KeyGeneration == KeyGeneration.Prefixed)
        {
            keys.Add($"{_key} = global::Haldus.HaldusKeys.NewPrefixed({ResourceEmitter.Literal(_resource.KeyPrefix!)})");
        }
        else if (_resource.IdKind == IdKind.Guid)
        {
            keys.Add(_resource.KeyGeneration == KeyGeneration.Random
                ? $"{_key} = global::System.Guid.NewGuid()"
                : $"{_key} = global::System.Guid.CreateVersion7()");
        }

        if (Parent is not null)
        {
            // Ebeveyn istek gövdesinden değil adresten gelir.
            keys.Add($"{Parent.PropertyName} = {ParentParameter}");
        }

        if (_resource.Workflow is { } workflow)
        {
            // Durum istek gövdesinden gelmez; kayıt her zaman başlangıç durumunda doğar.
            keys.Add($"Status = {workflow.StatusType}.{workflow.Initial}");
        }

        if (HasLifetime)
        {
            keys.Add($"ExpiresAt = {Now} + Lifetime");
        }

        if (_resource.Archetype == Archetype.Job)
        {
            keys.Add($"CreatedAt = {Now}");

            // İş, isteğin diliyle çalışır; arka planda istek olmadığı için dil kayda yazılır.
            keys.Add("Culture = global::System.Globalization.CultureInfo.CurrentUICulture.Name");
        }

        if (HasMetadata && metadata)
        {
            keys.Add($"Metadata = global::Haldus.HaldusMetadata.Create({source}.Metadata)");
        }

        if (!preview && source == "request")
        {
            // Sır ve token oluşturma ucunda üretilir (SecretVariables); kayda yalnızca özetleri yazılır.
            keys.AddRange(SecretAssignments(prefix: string.Empty));

            if (_resource.Token is not null)
            {
                keys.Add("TokenHash = global::Haldus.HaldusSecrets.Hash(token)");
            }
        }

        var fields = only is null ? _writable.Select(property => property.Name) : only;

        return string.Join(", ", keys.Concat(fields.Select(field => $"{field} = {source}.{field}")));
    }

    /// <summary>
    /// Yeni kayıttan türeyen kaynakların izdüşümlerini çağırır. Kayıt kaydedilmeden önce, onunla aynı bağlamda
    /// çalışır; böylece kaynak ve ondan türeyenler birlikte kalıcı olur.
    /// </summary>
    private string Derive() => _resource.Derivations.Length == 0
        ? string.Empty
        : $"        await using var derived = await global::Haldus.HaldusDerivations.BeginAsync(contexts.For<{_entity}>(), \"{_plural}\", cancellationToken);\n"
            + string.Concat(_resource.Derivations.Select(target =>
                $"        await global::Haldus.HaldusDerivations.ApplyAsync<{_entity}, {target}>(contexts.For<{_entity}>(), entity, contexts, cancellationToken);\n"));

    /// <summary>Türetmenin sınırını, kaynak kayıt kaydedildikten sonra kapatır.</summary>
    private string DeriveCommit() => _resource.Derivations.Length == 0
        ? string.Empty
        : "        await derived.CommitAsync(cancellationToken);\n";

    /// <summary>
    /// Yeni bir kaydı (oluşturulan veya kopyalanan) ekler ve kaydeder; sonunda satır sonu yoktur. Numara ve sıra,
    /// kayıtla aynı transaction'da ayrılır ve onunla birlikte kalıcı olur.
    /// </summary>
    /// <param name="catches">Kaydetmenin, çakışma olarak bildirilecek ek hataları (yakalama blokları).</param>
    /// <param name="derive">Kayıttan türeyen kaynaklar da güncellensin mi.</param>
    private string SaveCreated(string catches = "", bool derive = true)
    {
        var numbered = _resource.Numbering is { AssignOn: null };
        var save = numbered
            ? $$"""
                    await using var numbering = await global::Haldus.HaldusNumbering.AllocateAsync(context, Numbering, cancellationToken);
                    entity.Number = numbering.Number;
                    context.Set<{{_entity}}>().Add(entity);
                    await context.SaveChangesAsync(cancellationToken);
                    await numbering.CommitAsync(cancellationToken);
            """
            : $$"""
                    context.Set<{{_entity}}>().Add(entity);
                    await context.SaveChangesAsync(cancellationToken);
            """;

        if (NaturalKey is not null || catches.Length > 0)
        {
            // Ön kontrolü geçen iki eşzamanlı istekten ikincisi indekse takılır; o da çakışma olarak bildirilir.
            save = save.Replace("        await context.SaveChangesAsync(cancellationToken);", SaveNew(catches).TrimEnd('\n'));
        }

        if (Orderable)
        {
            save = OrderAppend() + save + "\n" + OrderCommit().TrimEnd('\n');
        }

        save = Effective(save);

        return derive && _resource.Derivations.Length > 0
            ? Derive() + save + "\n" + DeriveCommit().TrimEnd('\n')
            : save;
    }

    /// <summary>
    /// Dönemli kayıtta kaydetmeyi dönem kuralıyla sarar: kapsam kilitlenir, dönem kapsamdaki öteki kayıtlarla
    /// sınanır (gerekirse açık uçlu önceki dönem kapatılır) ve hepsi birlikte kalıcı olur. Kural kaydetmenin
    /// kendisine bağlıdır; böylece kaydı yazan her yol (güncelleme, toplu yollar, kopya, geri yükleme, aksiyonlar)
    /// ondan geçer.
    /// </summary>
    private string Effective(string save)
    {
        if (!EffectiveDated)
        {
            return save;
        }

        var siblings = SiblingsOf("entity")
            + string.Concat(_resource.EffectiveKeys.Select(key => $".Where(item => item.{key} == entity.{key})"));

        return $$"""
                    // Dönemler kapsam içinde çakışamaz; aynı kapsama aynı anda yazan istekler sıraya girer.
                    await using var period = {{OrderLock()}};

                    switch (await global::Haldus.HaldusEffectiveDating.ApplyAsync({{siblings}}, entity, cancellationToken))
                    {
                        case global::Haldus.EffectiveOutcome.Invalid:
                            return {{Problems}}.EffectivePeriodInvalid(messages);
                        case global::Haldus.EffectiveOutcome.Overlap:
                            return {{Problems}}.EffectivePeriodOverlap(messages, "{{_entity}}");
                    }

            {{save.TrimEnd('\n')}}
                    await period.CommitAsync(cancellationToken);
            """ + (save.EndsWith("\n", StringComparison.Ordinal) ? "\n" : string.Empty);
    }

    private void EmitCreate()
    {
        var assignments = CreateInitializer();
        var save = SaveCreated();

        // İş oluşturulur ve hemen dönülür: kayıt, altyapının kuyruğuyla birlikte kaydedilir, sonra teslim edilir.
        var job = _resource.Archetype == Archetype.Job;
        var result = job ? "Accepted" : "Created";

        if (job)
        {
            save = $$"""
                    var ticket = new global::Haldus.HaldusJobTicket(global::Haldus.HaldusJobRegistry.Name<{{_entity}}>(), entity.{{_key}});
                    jobs.Prepare(context, ticket);
                    context.Set<{{_entity}}>().Add(entity);
                    await context.SaveChangesAsync(cancellationToken);
                    await jobs.DispatchAsync(ticket, cancellationToken);
            """;
        }

        // Referans veya ebeveyn bulunamazsa oluşturma reddedilir; dönüş tipi buna göre genişler.
        var returnType = CreateCanFail
            ? $"{Task}<{Results}.Results<{Results}.{result}<{_entity}Response>, {Problem}>>"
            : $"{Task}<{Results}.{result}<{_entity}Response>>";

        _map.Append($$"""
                    group.MapPost("/", CreateAsync)
                        .WithName("Create{{_entity}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<Create{{_entity}}Request>>()
                        .ProducesValidationProblem(){{IdempotencyFilter($"Create{_entity}Request")}}{{(Idempotent ? "\n            .ProducesProblem(422)" : string.Empty)}}{{(Parent is null ? string.Empty : "\n            .ProducesProblem(404)")}}{{(_resource.OwnerGuard is null ? string.Empty : "\n            .ProducesProblem(409)")}};


            """);

        _handlers.Append($$"""

                private static async {{returnType}} CreateAsync(
            {{ScopeParameter}}        Create{{_entity}}Request request,
            {{HttpContextParameter}}{{(HasLifetime || job ? TimeProviderParameter : string.Empty)}}{{(job ? "        global::Haldus.IHaldusJobDispatcher jobs,\n" : string.Empty)}}{{ContextsParameter}}{{(CreateCanFail ? MessagesParameter : string.Empty)}}{{CancellationParameter}}    {
            {{(_resource.OwnerGuard is null ? ParentCheck() : OwnerGuard("create"))}}{{WriteChecks(patch: false, creating: true)}}{{SecretVariables()}}        var entity = new {{_entity}} { {{assignments}} };
            {{CreateDetails()}}{{Compute()}}
                    var context = contexts.For<{{_entity}}>();
            {{save}}

            {{WriteETag()}}        var location = $"{httpContext.Request.PathBase}{httpContext.Request.Path.Value?.TrimEnd('/')}/{entity.{{_key}}}";
                    return {{Http}}.TypedResults.{{result}}(location, {{CreatedBody()}});
                }

            """);
    }

    /// <summary>
    /// İşi iptal eder. Sırada bekleyen iş hemen iptal olur; çalışan iş için iptal istenir ve yanıt işin o
    /// anki halini döner (iş, mantığı iptali gördüğünde <c>Cancelled</c> olur).
    /// </summary>
    private void EmitJobCancel()
    {
        _map.Append($$"""
                    group.MapPost("{{_keyRoute}}/cancel", CancelAsync)
                        .WithName("Cancel{{_entity}}")
                        .ProducesProblem(404)
                        .ProducesProblem(409);


            """);

        _handlers.Append($$"""

                private static async {{OkOrProblem}} CancelAsync(
            {{KeyParameter}}{{TimeProviderParameter}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>().FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

                    if (!await global::Haldus.HaldusJobs.CancelAsync(context, entity, timeProvider, cancellationToken))
                    {
                        return {{Problems}}.StateConflict(messages, "cancel", entity.Status);
                    }

                    return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """);
    }

    /// <summary>PUT ve PATCH aynı iskeleti paylaşır; yalnızca alanların nasıl yazıldığı değişir.</summary>
    private void EmitWrite(string operation, string mapMethod, string assignments)
    {
        _map.Append($$"""
                    group.{{mapMethod}}("{{_keyRoute}}", {{operation}}Async)
                        .WithName("{{operation}}{{_entity}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{{operation}}{{_entity}}Request>>()
                        .ProducesValidationProblem()
                        .ProducesProblem(404){{StateMetadata(_resource.Workflow?.EditableIn)}}{{ConcurrencyMetadata()}};


            """);

        _handlers.Append($$"""

                private static async {{OkOrProblem}} {{operation}}Async(
            {{ScopeParameter}}{{KeyParameter}}        {{operation}}{{_entity}}Request request,
            {{(_concurrency ? HttpContextParameter : string.Empty)}}{{(Sliding ? TimeProviderParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}{{OwnerGuard("update")}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ChainGuard()}}{{StateGuard(_resource.Workflow?.EditableIn, "update")}}{{WriteChecks(patch: operation == "Patch", creating: false)}}{{CycleCheck(patch: operation == "Patch")}}{{ExpectVersion()}}{{OrderRemember()}}{{assignments}}
            {{SyncDetails(patch: operation == "Patch")}}{{Slide()}}{{Compute()}}{{OrderReparent()}}{{Save()}}{{OrderReparentCommit()}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """);
    }

    /// <summary>
    /// Anahtarı istemcinin verdiği archetype'larda PUT, kayıt yoksa oluşturur, varsa tam değiştirir.
    /// İki istek aynı yeni anahtarı aynı anda yazarsa ikincisi çakışır; o durumda bir kez yeniden denenir
    /// ve güncelleme olarak tamamlanır.
    /// </summary>
    private void EmitPut()
    {
        var keys = new List<string> { $"{_key} = {_keyParameter}" };
        if (_tenant)
        {
            // Kiracı bileşik anahtarın parçasıdır; kayıt izlenmeye başlamadan önce atanmalıdır.
            keys.Add("TenantId = tenants.TenantId.GetValueOrDefault()");
        }

        var creation = string.Join(
            ", ", keys.Concat(_writable.Select(property => $"{property.Name} = request.{property.Name}")));

        _map.Append($$"""
                    group.MapPut("{{_keyRoute}}", PutAsync)
                        .WithName("Put{{_entity}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<Put{{_entity}}Request>>()
                        .ProducesValidationProblem();


            """);

        var returnType = ChecksReferences
            ? $"{Results}.Results<{Results}.Ok<{_entity}Response>, {Results}.Created<{_entity}Response>, {Problem}>"
            : $"{Results}.Results<{Results}.Ok<{_entity}Response>, {Results}.Created<{_entity}Response>>";

        _handlers.Append($$"""

                private static async {{Task}}<{{returnType}}> PutAsync(
            {{ScopeParameter}}{{KeyParameter}}        Put{{_entity}}Request request,
            {{HttpContextParameter}}{{(_tenant ? TenantsParameter : string.Empty)}}{{ContextsParameter}}{{(ChecksReferences ? MessagesParameter : string.Empty)}}{{CancellationParameter}}    {
            {{ReferenceChecks(patch: false)}}        var context = contexts.For<{{_entity}}>();

                    for (var attempt = 0; ; attempt++)
                    {
                        var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                        if (entity is not null)
                        {
            {{Indent(UpdateAssignments() + Compute(), 8)}}                await context.SaveChangesAsync(cancellationToken);
                            return {{Http}}.TypedResults.Ok(ToResponse(entity));
                        }

                        entity = new {{_entity}} { {{creation}} };
            {{Indent(Compute(), 4)}}            context.Set<{{_entity}}>().Add(entity);

                        try
                        {
                            await context.SaveChangesAsync(cancellationToken);
                        }
                        catch (global::Microsoft.EntityFrameworkCore.DbUpdateException) when (attempt == 0)
                        {
                            context.Entry(entity).State = global::Microsoft.EntityFrameworkCore.EntityState.Detached;
                            continue;
                        }

                        var location = $"{httpContext.Request.PathBase}{httpContext.Request.Path}";
                        return {{Http}}.TypedResults.Created(location, ToResponse(entity));
                    }
                }

            """);
    }

    private void EmitDelete()
    {
        _map.Append($$"""
                    group.MapDelete("{{_keyRoute}}", DeleteAsync)
                        .WithName("Delete{{_entity}}")
                        .ProducesProblem(404){{StateMetadata(_resource.Workflow?.DeletableIn)}}{{ConcurrencyMetadata()}};


            """);

        _handlers.Append($$"""

                private static async {{NoContentOrProblem}} DeleteAsync(
            {{ScopeParameter}}{{KeyParameter}}{{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}{{OwnerGuard("delete")}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ChainDeleteGuard()}}{{StateGuard(_resource.Workflow?.DeletableIn, "delete")}}{{ExpectVersion()}}{{OrderBegin()}}        context.Set<{{_entity}}>().Remove(entity);
            {{SaveDelete()}}{{OrderCloseGap()}}{{OrderCommit()}}
            {{(_resource.Has(Traits.SoftDeletable) ? WriteETag() : string.Empty)}}        return {{Http}}.TypedResults.NoContent();
                }

            """);
    }

    private void EmitRestore()
    {
        _map.Append($$"""
                    group.MapPost("{{_keyRoute}}/restore", RestoreAsync)
                        .WithName("Restore{{_entity}}")
                        .ProducesProblem(404){{ConcurrencyMetadata()}};


            """);

        _handlers.Append($$"""

                private static async {{OkOrProblem}} RestoreAsync(
            {{ScopeParameter}}{{KeyParameter}}{{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>()
                        .IgnoreQueryFilters(new[] { global::Haldus.HaldusFilters.SoftDelete })
                        .FirstOrDefaultAsync(item => {{Match}} && item.DeletedAt != null, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ExpectVersion()}}{{OrderAppend()}}        entity.DeletedAt = null;
            {{Save()}}{{OrderCommit()}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """);
    }

    private void EmitPurge()
    {
        _map.Append($$"""
                    group.MapPost("{{_keyRoute}}/purge", PurgeAsync)
                        .WithName("Purge{{_entity}}")
                        .ProducesProblem(404){{ConcurrencyMetadata()}};


            """);

        var delete = _concurrency
            ? """
                    var deleted = ifMatch.Version is { } expectedVersion
                        ? await query.Where(item => item.Version == expectedVersion).ExecuteDeleteAsync(cancellationToken)
                        : await query.ExecuteDeleteAsync(cancellationToken);

            """
            : "        var deleted = await query.ExecuteDeleteAsync(cancellationToken);\n";

        // ExecuteDelete veritabanının reddini sarmalamadan fırlatır; o da çakışma olarak bildirilir. Kayda kimin
        // referans verdiği burada bilinemez: referans başka bir modülden veya elle yazılmış bir ilişkiden gelebilir.
        delete = $$"""
                    int deleted;
                    try
                    {
            {{Indent(delete.Replace("var deleted = ", "deleted = "), 4)}}        }
                    catch (global::System.Data.Common.DbException)
                    {
                        return {{Problems}}.InUse(messages, "{{_entity}}", {{_keyParameter}});
                    }

            """;

        // Sıfır satır silindiyse ya kayıt yoktur ya da sürümü değişmiştir; ikisi ayrı yanıtlanır.
        var staleCheck = _concurrency
            ? $$"""
                        if (await query.AnyAsync(cancellationToken))
                        {
                            return {{Problems}}.PreconditionFailed(messages);
                        }


            """
            : string.Empty;

        // Yalnızca önceden silinmiş kayıt kalıcı silinebilir; aksi halde bu uç, silmenin geri alınabilir
        // olmasını ve sürüm kontrolünü atlamanın yolu olurdu. ExecuteDelete değişiklik izleyicisini
        // atladığı için silmeyi güncellemeye çeviren interceptor devreye girmez.
        _handlers.Append($$"""

                private static async {{NoContentOrProblem}} PurgeAsync(
            {{ScopeParameter}}{{KeyParameter}}{{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}        var query = contexts.For<{{_entity}}>().Set<{{_entity}}>()
                        .IgnoreQueryFilters(new[] { global::Haldus.HaldusFilters.SoftDelete })
                        .Where(item => {{Match}} && item.DeletedAt != null);

            {{delete}}
                    if (deleted == 0)
                    {
            {{staleCheck}}            return {{NotFound}};
                    }

                    return {{Http}}.TypedResults.NoContent();
                }

            """);
    }

    private void EmitArchive(string operation, string route, string statement)
    {
        _map.Append($$"""
                    group.MapPost("{{_keyRoute}}/{{route}}", {{operation}}Async)
                        .WithName("{{operation}}{{_entity}}")
                        .ProducesProblem(404){{ConcurrencyMetadata()}};


            """);

        _handlers.Append($$"""

                private static async {{OkOrProblem}} {{operation}}Async(
            {{ScopeParameter}}{{KeyParameter}}        global::System.TimeProvider timeProvider,
            {{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ExpectVersion()}}        {{statement}}
            {{Save()}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """);
    }

    /// <summary>
    /// Tek kayıt örtük olarak vardır: hiç yazılmadıysa okuma varsayılan değerleri döner, ilk yazma kaydı
    /// oluşturur. Anahtar entity'de görünmez; kiracıya bağlıysa kiracının kendisidir, değilse sabit bir
    /// gölge alandır. Böylece aynı kapsamda ikinci bir kayıt oluşamaz.
    /// </summary>
    private void EmitSingleton()
    {
        if (_resource.Has(Operations.Get))
        {
            _map.Append($$"""
                        group.MapGet("/", GetAsync)
                            .WithName("Get{{_entity}}");


                """);

            // Ebeveyn altındaki tek kayıt ebeveyn başına birdir; ebeveyn görünmüyorsa kayıt da yoktur.
            _handlers.Append(Parent is null
                ? $$"""

                    private static async {{Task}}<{{Results}}.Ok<{{_entity}}Response>> GetAsync(
                {{ContextsParameter}}{{CancellationParameter}}    {
                        var entity = await contexts.For<{{_entity}}>().Set<{{_entity}}>()
                            .AsNoTracking()
                            .FirstOrDefaultAsync(cancellationToken);

                        return {{Http}}.TypedResults.Ok(ToResponse(entity ?? new {{_entity}}()));
                    }

                """
                : $$"""

                    private static async {{OkOrProblem}} GetAsync(
                {{ScopeParameter}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                {{ParentCheck()}}        var entity = await contexts.For<{{_entity}}>().Set<{{_entity}}>()
                            .AsNoTracking()
                            .FirstOrDefaultAsync(item => item.{{Parent.PropertyName}} == {{ParentParameter}}, cancellationToken);

                        // Hiç yazılmadıysa varsayılan değerler döner.
                        return {{Http}}.TypedResults.Ok(ToResponse(entity ?? new {{_entity}} { {{Parent.PropertyName}} = {{ParentParameter}} }));
                    }

                """);
        }

        if (_resource.Has(Operations.Update))
        {
            EmitSingletonWrite("Update", "MapPut", UpdateAssignments());
        }

        if (_resource.Has(Operations.Patch))
        {
            EmitSingletonWrite("Patch", "MapPatch", PatchAssignments());
        }
    }

    private void EmitSingletonWrite(string operation, string mapMethod, string assignments)
    {
        var key = _tenant
            ? "entity.TenantId = tenants.TenantId.GetValueOrDefault();"
            : Parent is not null
                ? $"entity.{Parent.PropertyName} = {ParentParameter};"
                : "context.Entry(entity).Property(global::Haldus.HaldusModel.SingletonKey).CurrentValue = 1;";

        var scoped = Parent is not null;
        var returnType = scoped ? OkOrProblem : $"{Task}<{Results}.Ok<{_entity}Response>>";
        var match = scoped ? $"item => item.{Parent!.PropertyName} == {ParentParameter}, " : string.Empty;

        _map.Append($$"""
                    group.{{mapMethod}}("/", {{operation}}Async)
                        .WithName("{{operation}}{{_entity}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{{operation}}{{_entity}}Request>>()
                        .ProducesValidationProblem(){{(scoped ? "\n            .ProducesProblem(404)" : string.Empty)}};


            """);

        _handlers.Append($$"""

                private static async {{returnType}} {{operation}}Async(
            {{ScopeParameter}}        {{operation}}{{_entity}}Request request,
            {{(_tenant ? TenantsParameter : string.Empty)}}{{ContextsParameter}}{{(scoped ? MessagesParameter : string.Empty)}}{{CancellationParameter}}    {
            {{ParentCheck()}}        var context = contexts.For<{{_entity}}>();

                    for (var attempt = 0; ; attempt++)
                    {
                        var entity = await context.Set<{{_entity}}>().FirstOrDefaultAsync({{match}}cancellationToken);
                        var isNew = entity is null;
                        entity ??= new {{_entity}}();

            {{Indent(assignments + Compute(), 4).TrimEnd('\n')}}

                        if (isNew)
                        {
                            {{key}}
                            context.Set<{{_entity}}>().Add(entity);
                        }

                        try
                        {
                            await context.SaveChangesAsync(cancellationToken);
                        }
                        catch (global::Microsoft.EntityFrameworkCore.DbUpdateException) when (isNew && attempt == 0)
                        {
                            // Başka bir istek kaydı aynı anda oluşturdu; ikinci turda güncelleme olarak tamamlanır.
                            context.Entry(entity).State = global::Microsoft.EntityFrameworkCore.EntityState.Detached;
                            continue;
                        }

                        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                    }
                }

            """);
    }

    /// <summary>
    /// Filtrenin her özelliği ayrı bir sorgu parametresi olarak bağlanır. Böylece parametre adları
    /// camelCase olur ve hepsi isteğe bağlıdır; gönderilmeyen özellik filtredeki varsayılan değerini korur.
    /// </summary>
    private string FilterParameters() => _resource.Filter is null
        ? string.Empty
        : string.Concat(_resource.Filter.Properties.Select(property =>
            $"        [global::Microsoft.AspNetCore.Mvc.FromQuery(Name = \"{ResourceEmitter.CamelCase(property.Name)}\")] "
            + $"{property.NullableType} filter{property.Name},\n"));

    private string ApplyFilter()
    {
        if (_resource.Filter is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append($"\n        var filter = new {_resource.Filter.Type}();\n");

        foreach (var property in _resource.Filter.Properties)
        {
            // Gönderilmeyen dizi parametresi boş dizi olarak bağlanır; o da "gönderilmedi" sayılır.
            var pattern = property.IsArray ? "{ Length: > 0 }" : "{ }";

            builder.Append($$"""
                        if (filter{{property.Name}} is {{pattern}} {{ResourceEmitter.CamelCase(property.Name)}}Value)
                        {
                            filter.{{property.Name}} = {{ResourceEmitter.CamelCase(property.Name)}}Value;
                        }


                """);
        }

        builder.Append("        query = filter.Apply(query);\n");
        return builder.ToString();
    }

    private void EmitSearch()
    {
        var softDelete = _resource.Has(Traits.SoftDeletable);
        var archive = _resource.Has(Traits.Archivable);

        _map.Append($$"""
                    group.MapPost("/search", SearchAsync)
                        .WithName("Search{{_plural}}")
                        .ProducesProblem(400);


            """);

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Results<{{Results}}.Ok<global::Haldus.PagedResult<{{_entity}}Response>>, {{Problem}}>> SearchAsync(
            {{ScopeParameter}}        {{_entity}}SearchRequest request,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var budget = new global::Haldus.SearchBudget();
                    budget.CountSortFields(request.Sort?.Count ?? 0);
                    var predicate = BuildPredicate(request.Where, budget);

                    if (budget.Exceeded)
                    {
                        return {{Problems}}.SearchTooComplex(messages);
                    }

                    var query = contexts.For<{{_entity}}>().Set<{{_entity}}>().AsNoTracking(){{ScopeFilter}};

            """);

        if (softDelete)
        {
            _handlers.Append("""

                        if (request.IncludeDeleted == true)
                        {
                            query = query.IgnoreQueryFilters(new[] { global::Haldus.HaldusFilters.SoftDelete });
                        }

                """);
        }

        if (archive)
        {
            _handlers.Append("""

                        if (request.IncludeArchived != true)
                        {
                            query = query.Where(item => item.ArchivedAt == null);
                        }

                """);
        }

        var conditions = string.Concat(_resource.SearchFields.Select(field =>
        {
            var method = (field.Kind, field.IsNullable) switch
            {
                (SearchKind.String, _) => $"String<{_entity}>",
                (SearchKind.Range, false) => $"Range<{_entity}, {field.ValueType}>",
                (SearchKind.Range, true) => $"NullableRange<{_entity}, {field.ValueType}>",
                (_, false) => $"Equality<{_entity}, {field.ValueType}>",
                (_, true) => $"NullableEquality<{_entity}, {field.ValueType}>",
            };

            return $"            global::Haldus.HaldusSearch.{method}(item => item.{field.Name}, where.{field.Name}, budget),\n";
        }));

        var sortCases = string.Concat(_resource.SearchFields.Select(field =>
            $"                {_entity}SearchField.{field.Name} => "
            + $"global::Haldus.HaldusSearch.OrderBy(query, ordered, item => item.{field.Name}, entry.Descending),\n"));

        _handlers.Append($$"""

                    if (predicate is not null)
                    {
                        query = query.Where(predicate);
                    }

                    var page = new global::Haldus.PageRequest(request.Page, request.PageSize);
                    var totalCount = await query.LongCountAsync(cancellationToken);
                    var items = await ApplySort(query, request.Sort)
                        .Skip(page.Skip)
                        .Take(page.PageSizeOrDefault)
                        .Select(item => new {{_entity}}Response { {{ResponseAssignments}} })
                        .ToListAsync(cancellationToken);

                    return {{Http}}.TypedResults.Ok(
                        new global::Haldus.PagedResult<{{_entity}}Response>(items, page.PageOrDefault, page.PageSizeOrDefault, totalCount));
                }

                private static global::System.Linq.Expressions.Expression<global::System.Func<{{_entity}}, bool>>? BuildPredicate(
                    {{_entity}}Where? where,
                    global::Haldus.SearchBudget budget)
                {
                    if (where is null || !budget.TryEnter())
                    {
                        return null;
                    }

                    var predicate = global::Haldus.HaldusSearch.All(new[]
                    {
            {{conditions}}            global::Haldus.HaldusSearch.All(where.And?.Select(child => BuildPredicate(child, budget)).ToList()),
                        global::Haldus.HaldusSearch.Any(where.Or?.Select(child => BuildPredicate(child, budget)).ToList()),
                        global::Haldus.HaldusSearch.Not(BuildPredicate(where.Not, budget)),
                    });

                    budget.Exit();
                    return predicate;
                }

                private static global::System.Linq.IOrderedQueryable<{{_entity}}> ApplySort(
                    global::System.Linq.IQueryable<{{_entity}}> query,
                    global::System.Collections.Generic.IReadOnlyList<{{_entity}}Sort>? sort)
                {
                    global::System.Linq.IOrderedQueryable<{{_entity}}>? ordered = null;

                    foreach (var entry in sort ?? global::System.Array.Empty<{{_entity}}Sort>())
                    {
                        ordered = entry.Field switch
                        {
            {{sortCases}}                _ => ordered,
                        };
                    }

                    // Sayfalama kararlı olsun diye sıralama her zaman anahtarla tamamlanır.
                    return global::Haldus.HaldusSearch.OrderBy(query, ordered, item => item.{{_key}}, descending: false);
                }

            """);
    }

    /// <summary>Süresi dolmamış bir kaydı hemen sonlandırır. Kayıt silinmez, ama artık okunamaz ve yazılamaz.</summary>
    private void EmitExpire()
    {
        _map.Append($$"""
                    group.MapPost("{{_keyRoute}}/expire", ExpireAsync)
                        .WithName("Expire{{_entity}}")
                        .ProducesProblem(404){{ConcurrencyMetadata()}};


            """);

        _handlers.Append($$"""

                private static async {{NoContentOrProblem}} ExpireAsync(
            {{ScopeParameter}}{{KeyParameter}}{{TimeProviderParameter}}{{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>().FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ExpectVersion()}}        entity.ExpiresAt = {{Now}};
            {{Save()}}
                    return {{Http}}.TypedResults.NoContent();
                }

            """);
    }

    /// <summary>
    /// Bir geçişin veya entity aksiyonunun ucunu üretir. Uç, <c>MapTransition</c> / <c>MapAction</c> çağrısı
    /// çalıştığında bağlanır; çağrıya verilen lambda (varsa) eşleyiciye parametre olarak gelir. Kaydı bulma,
    /// sürüm kontrolü ve kaydetme burada üretilir; iş mantığı istek tipinden, lambda'dan veya bir handler
    /// sınıfından gelir. Aksiyon, durum kontrolü ve durum ataması olmayan bir geçiştir; isterse kaydın kendisi
    /// yerine kendi sonucunu döndürür.
    /// </summary>
    private void EmitTransition(TransitionModel transition)
    {
        const string Services = "global::System.IServiceProvider";
        const string Cancellation = "global::System.Threading.CancellationToken";
        const string Injection = "global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions";

        var request = transition.RequestType;
        var response = transition.ResponseType;
        var method = $"{(transition.IsAction ? "Action" : "Transition")}{transition.PascalName}Async";
        var outcome = response is null ? Task : $"{Task}<{response}>";
        var apply = request is null
            ? $"global::System.Func<{_entity}, {Services}, {Cancellation}, {outcome}>?"
            : $"global::System.Func<{_entity}, {request}, {Services}, {Cancellation}, {outcome}>?";

        // İş mantığının sözleşmeleri geçiş ve aksiyon için ayrıdır; üretilen iskelet aynıdır.
        var (handler, require, selfApplying) = (transition.IsAction, response) switch
        {
            (false, _) => ($"global::Haldus.ITransitionHandler<{_entity}, {request}>", $"RequireHandler<{_entity}, {request}>", "ITransition"),
            (true, null) => ($"global::Haldus.IEntityActionHandler<{_entity}, {request}>", $"RequireActionHandler<{_entity}, {request}>", "IEntityAction"),
            _ => ($"global::Haldus.IEntityActionHandler<{_entity}, {request}, {response}>", $"RequireActionHandler<{_entity}, {request}, {response}>", "IEntityAction"),
        };

        // Uç bir lambda ile eşlenir ki çağrıya verilen iş mantığını yakalayabilsin; asıl iş statik metottadır.
        var parameters = new List<(string Type, string Name)>();
        if (Parent is not null)
        {
            parameters.Add((Parent.TargetKeyType, ParentParameter));
        }

        parameters.Add((_resource.IdType, _keyParameter));
        if (request is not null)
        {
            parameters.Add((request, "request"));
        }

        parameters.Add(("global::Microsoft.AspNetCore.Http.HttpContext", "httpContext"));
        if (Sliding)
        {
            parameters.Add(("global::System.TimeProvider", "timeProvider"));
        }

        parameters.Add(("global::Haldus.IHaldusDbContextProvider", "contexts"));
        parameters.Add(("global::Haldus.IHaldusMessages", "messages"));
        parameters.Add((Cancellation, "cancellationToken"));

        var lambdaParameters = string.Join(",\n", parameters.Select(parameter => $"                {parameter.Type} {parameter.Name}"));
        var arguments = string.Join(", ", parameters.Select(parameter => parameter.Name == "httpContext" ? "apply, httpContext" : parameter.Name));

        // İş mantığı ne istek tipinde ne de lambda'da ise bir handler sınıfı kayıtlı olmalıdır.
        var requireHandler = request is not null && !transition.ImplementsTransition
            ? $$"""
                    if (apply is null)
                    {
                        global::Haldus.HaldusTransitionRegistry.{{require}}(group);
                    }


            """
            : string.Empty;

        var validation = request is null
            ? string.Empty
            : $"\n            .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{request}>>()\n            .ProducesValidationProblem()";

        _transitionMaps.Append($$"""

                public static global::Microsoft.AspNetCore.Builder.RouteHandlerBuilder {{transition.MapperName}}(
                    global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder group,
                    {{apply}} apply)
                {
            {{requireHandler}}{{TokenTransitionMap(transition, method, parameters, validation)}}        return group.MapPost("{{_keyRoute}}/{{transition.Route}}", (
            {{lambdaParameters}}) =>
                            {{method}}({{arguments}}))
                        .WithName("{{transition.PascalName}}{{_entity}}"){{validation}}{{IdempotencyFilter(request)}}
                        .ProducesProblem(404)
                        .ProducesProblem(409)
                        .ProducesProblem(422){{ConcurrencyMetadata()}};
                }

            """);

        var logic = new StringBuilder();

        if (response is not null)
        {
            // Sonuç döndüren aksiyonda mantık zorunludur: lambda verilmediyse handler sınıfı çağrılır.
            logic.Append($$"""
                            result = apply is not null
                                ? await apply(entity, request, httpContext.RequestServices, cancellationToken)
                                : await {{Injection}}.GetRequiredService<{{handler}}>(httpContext.RequestServices)
                                    .HandleAsync(entity, request, cancellationToken);

                """);
        }
        else
        {
            if (transition.ImplementsTransition)
            {
                logic.Append($"            ((global::Haldus.{selfApplying}<{_entity}>)request).Apply(entity);\n\n");
            }

            logic.Append($$"""
                            if (apply is not null)
                            {
                                await apply(entity, {{(request is null ? string.Empty : "request, ")}}httpContext.RequestServices, cancellationToken);
                            }

                """);

            if (request is not null)
            {
                logic.Append($$"""
                                else if ({{Injection}}
                                    .GetService<{{handler}}>(httpContext.RequestServices) is { } handler)
                                {
                                    await handler.HandleAsync(entity, request, cancellationToken);
                                }

                    """);
            }
        }

        // Numara bu duruma geçerken atanıyorsa, kayıtla aynı transaction'da ayrılır; daha önce atanmışsa korunur.
        var numbered = transition.To is not null && _resource.Numbering is { AssignOn: { } assignOn } && assignOn == transition.To;
        var allocate = numbered
            ? """
                    await using var numbering = entity.Number is null
                        ? await global::Haldus.HaldusNumbering.AllocateAsync(context, Numbering, cancellationToken)
                        : null;

                    if (numbering is not null)
                    {
                        entity.Number = numbering.Number;
                    }


            """
            : string.Empty;
        var commit = numbered
            ? """

                    if (numbering is not null)
                    {
                        await numbering.CommitAsync(cancellationToken);
                    }

            """
            : string.Empty;

        var returnType = response is null
            ? OkOrProblem
            : $"{Task}<{Results}.Results<{Results}.Ok<{response}>, {Problem}>>";
        var declareResult = response is null ? string.Empty : $"        {response} result;\n";
        var setStatus = transition.To is null
            ? string.Empty
            : $"        entity.Status = {_resource.Workflow!.StatusType}.{transition.To};\n";

        _handlers.Append($$"""

                private static async {{returnType}} {{method}}(
            {{ScopeParameter}}{{KeyParameter}}{{(request is null ? string.Empty : $"        {request} request,\n")}}        {{apply}} apply,
            {{HttpContextParameter}}{{(Sliding ? TimeProviderParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}{{OwnerGuard(transition.Route)}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ChainClosedGuard()}}{{StateGuard(transition.IsAction ? null : transition.From, transition.Route)}}{{ApprovalGate(transition.Route)}}{{ExpectVersion()}}{{declareResult}}        try
                    {
            {{logic}}        }
                    catch (global::Haldus.TransitionRejectedException rejection)
                    {
                        return {{Problems}}.TransitionRejected(messages, rejection.Message);
                    }

            {{setStatus}}{{Slide()}}{{Compute()}}{{Spawn(transition)}}{{allocate}}{{Save()}}{{commit}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok({{(response is null ? "ToResponse(entity)" : "result")}});
                }

            """);

        EmitTokenTransition(transition, method, request, apply, returnType);
    }
    /// <summary>Kayıt sayılan durumlardan birinde değilse işlemi çakışma olarak reddeder.</summary>
    private string StateGuard(EquatableArray<string>? allowed, string operation)
    {
        if (_resource.Workflow is not { } workflow || allowed is not { } states)
        {
            return string.Empty;
        }

        return $$"""
                    if (entity.Status is not ({{States(workflow.StatusType, states)}}))
                    {
                        return {{Problems}}.StateConflict(messages, "{{operation}}", entity.Status);
                    }


            """;
    }

    private static string States(string statusType, IEnumerable<string> states) =>
        string.Join(" or ", states.Select(state => $"{statusType}.{state}"));

    private string StateMetadata(EquatableArray<string>? allowed) =>
        allowed is null && _resource.OwnerGuard is null ? string.Empty : "\n            .ProducesProblem(409)";

    /// <summary>
    /// Ayrı uçlu bir detay sahibinin parçasıdır: sahibi düzenlenemeyen bir durumdaysa detayı da yazılamaz.
    /// Sahibin görünür olduğu da burada doğrulanır.
    /// </summary>
    private string OwnerGuard(string operation)
    {
        if (_resource.OwnerGuard is not { } guard || Parent is null)
        {
            return string.Empty;
        }

        return $$"""
                    var ownerStatus = await contexts.For<{{Parent.TargetType}}>().Set<{{Parent.TargetType}}>()
                        .Where(parent => parent.{{Parent.TargetKeyName}} == {{ParentParameter}})
                        .Select(parent => ({{guard.StatusType}}?)parent.Status)
                        .FirstOrDefaultAsync(cancellationToken);

                    if (ownerStatus is null)
                    {
                        return {{Problems}}.NotFound(messages, "{{Parent.TargetName}}", {{ParentParameter}});
                    }

                    if (ownerStatus is not ({{States(guard.StatusType, guard.EditableIn)}}))
                    {
                        return {{Problems}}.StateConflict(messages, "{{operation}}", ownerStatus);
                    }


            """;
    }

    /// <summary>Kayan ömürde her başarılı yazma süreyi yeniden başlatır.</summary>
    private string Slide() => Sliding ? $"        entity.ExpiresAt = {Now} + Lifetime;\n" : string.Empty;

    /// <summary>Ucu <c>Idempotency-Key</c> ile tekrara karşı korur; doğrulamadan sonra çalışır ki geçersiz istek veritabanına dokunmasın.</summary>
    private string IdempotencyFilter(string? requestType) => Idempotent
        ? $"\n            .AddEndpointFilter(new global::Haldus.HaldusIdempotencyFilter<{_entity}>("
            + $"{(requestType is null ? "null" : $"typeof({requestType})")}, required: {(_resource.Has(Traits.IdempotencyRequired) ? "true" : "false")}))"
        : string.Empty;

    /// <summary>Entity hesaplanan alanlar taşıyorsa, istek uygulandıktan sonra ve kaydetmeden önce hesaplatır.</summary>
    private string Compute() => _resource.IsComputed
        ? "        ((global::Haldus.IComputed)entity).Compute();\n"
        : string.Empty;

    /// <summary>Oluşturma isteğindeki gömülü satırları yeni kayda ekler.</summary>
    private string CreateDetails()
    {
        var builder = new StringBuilder();

        foreach (var detail in _resource.Details)
        {
            var position = ResourceEmitter.CamelCase(detail.CollectionName) + "Position";
            var assignments = string.Join(
                ", ",
                new[] { $"{detail.KeyName} = global::System.Guid.CreateVersion7()", $"Position = {position}++" }
                    .Concat(detail.Properties.Where(property => property.IsWritable).Select(property => $"{property.Name} = row.{property.Name}")));

            builder.Append($$"""

                        var {{position}} = 0;
                        foreach (var row in request.{{detail.CollectionName}} ?? global::System.Array.Empty<{{detail.Namespace.Prefix()}}Create{{detail.EntityName}}Request>())
                        {
                            entity.{{detail.CollectionName}}.Add(new {{detail.QualifiedName}} { {{assignments}} });
                        }

                """);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Gömülü satırları istektekilerle eşitler. Satır değişikliği üst kaydı da değişmiş sayar; böylece üst
    /// kaydın sürümü ilerler ve değiştirilme zamanı güncellenir.
    /// </summary>
    private string SyncDetails(bool patch)
    {
        var builder = new StringBuilder();

        foreach (var detail in _resource.Details.Where(detail => !detail.IsCommandWritten))
        {
            var collection = detail.CollectionName;
            var problem = ResourceEmitter.CamelCase(collection) + "Problem";
            var sync = $$"""
                if (Sync{{collection}}(entity, request.{{collection}}{{(patch ? "!" : string.Empty)}}, context, messages) is { } {{problem}})
                {
                    return {{problem}};
                }

                context.Entry(entity).State = global::Microsoft.EntityFrameworkCore.EntityState.Modified;

                """;

            builder.Append(patch
                ? $"        if (((global::Haldus.IHaldusPatch)request).IsSet(\"{collection}\"))\n        {{\n{Indent(sync, 12)}        }}\n\n"
                : Indent(sync, 8) + "\n");
        }

        return builder.ToString();
    }

    /// <summary>Her gömülü detay için bir eşitleme metodu üretir.</summary>
    private void EmitSyncHelpers()
    {
        foreach (var detail in _resource.Details.Where(detail => !detail.IsCommandWritten))
        {
            var collection = detail.CollectionName;
            var writable = detail.Properties.Where(property => property.IsWritable).ToArray();
            var parent = detail.Parent!;

            var updates = string.Concat(
                new[] { "                existing.Position = position++;\n" }
                    .Concat(writable.Select(property => $"                existing.{property.Name} = row.{property.Name};\n")));
            var creation = string.Join(
                ", ",
                new[]
                {
                    $"{detail.KeyName} = global::System.Guid.CreateVersion7()",
                    $"{parent.PropertyName} = entity.{_key}",
                    "Position = position++",
                }.Concat(writable.Select(property => $"{property.Name} = row.{property.Name}")));

            _handlers.Append($$"""

                    /// <summary>
                    /// İstekteki satırları kayıttakilerle eşitler: kimliği verilen satır güncellenir, verilmeyen
                    /// eklenir, istekte bulunmayan silinir. Kimlik bu kayda ait değilse hiçbir şey kaydedilmez.
                    /// </summary>
                    private static {{Problem}}? Sync{{collection}}(
                        {{_entity}} entity,
                        global::System.Collections.Generic.IReadOnlyList<{{detail.Namespace.Prefix()}}Update{{detail.EntityName}}Request> rows,
                        global::Microsoft.EntityFrameworkCore.DbContext context,
                        global::Haldus.IHaldusMessages messages)
                    {
                        var current = entity.{{collection}}.ToDictionary(row => row.{{detail.KeyName}});
                        var kept = new global::System.Collections.Generic.HashSet<{{detail.IdType}}>();
                        var position = 0;

                        foreach (var row in rows)
                        {
                            if (row.{{detail.KeyName}} is { } rowId)
                            {
                                if (!current.TryGetValue(rowId, out var existing) || !kept.Add(rowId))
                                {
                                    return {{Problems}}.DetailNotFound(messages, "{{ResourceEmitter.CamelCase(collection)}}", "{{detail.EntityName}}", rowId);
                                }

                {{updates}}            }
                            else
                            {
                                // Yeni satır açıkça eklenir; aksi halde anahtarı dolu olduğu için var olan bir
                                // satır sanılıp güncellenmeye çalışılırdı.
                                var added = new {{detail.QualifiedName}} { {{creation}} };
                                entity.{{collection}}.Add(added);
                                context.Add(added);
                            }
                        }

                        foreach (var removed in current.Values.Where(row => !kept.Contains(row.{{detail.KeyName}})).ToList())
                        {
                            entity.{{collection}}.Remove(removed);
                            context.Remove(removed);
                        }

                        return null;
                    }

                """);
        }
    }

    /// <summary>
    /// Satırları tek tek yazan komutlar: ekle, değiştir, kaldır. Her biri üst kaydın bir yazmasıdır; güncelleme
    /// ucunun korumalarından (sürüm, durum, kapanmış kayıt) geçer, hesaplanan alanları yeniler ve üst kaydı,
    /// satırlarıyla birlikte döndürür.
    /// </summary>
    private void EmitDetailCommands(ResourceModel detail)
    {
        var collection = detail.CollectionName;
        var route = Naming.ToKebabCase(collection);
        var name = detail.EntityName;
        var prefix = detail.Namespace.Prefix();
        var writable = detail.Properties.Where(property => property.IsWritable).ToArray();
        var rowRoute = $"{_keyRoute}/{route}/{{rowId:guid}}";
        var metadata = $"\n            .ProducesProblem(404){StateMetadata(_resource.Workflow?.EditableIn)}{ConcurrencyMetadata()}";

        _map.Append($$"""
                    group.MapPost("{{_keyRoute}}/{{route}}", Add{{name}}Async)
                        .WithName("Add{{_entity}}{{name}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{{prefix}}Create{{name}}Request>>()
                        .ProducesValidationProblem(){{metadata}};

                    group.MapPut("{{rowRoute}}", Change{{name}}Async)
                        .WithName("Change{{_entity}}{{name}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{{prefix}}Create{{name}}Request>>()
                        .ProducesValidationProblem(){{metadata}};

                    group.MapDelete("{{rowRoute}}", Remove{{name}}Async)
                        .WithName("Remove{{_entity}}{{name}}"){{metadata}};


            """);

        // Üç komut aynı iskeleti paylaşır: üst kaydı satırlarıyla yükle, korumalardan geç, satırı değiştir, kaydet.
        string Command(string method, string parameters, string change) => $$"""

                private static async {{OkOrProblem}} {{method}}{{name}}Async(
            {{ScopeParameter}}{{KeyParameter}}{{parameters}}{{(_concurrency ? HttpContextParameter : string.Empty)}}{{(Sliding ? TimeProviderParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}{{OwnerGuard("update")}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ChainGuard()}}{{StateGuard(_resource.Workflow?.EditableIn, "update")}}{{ExpectVersion()}}{{change}}
                    // Satır değişikliği üst kaydın değişikliğidir: sürümü ilerler ve hesaplanan alanları yenilenir.
                    context.Entry(entity).State = global::Microsoft.EntityFrameworkCore.EntityState.Modified;
            {{Slide()}}{{Compute()}}{{Save()}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """;

        var find = $$"""
                    var row = entity.{{collection}}.FirstOrDefault(candidate => candidate.{{detail.KeyName}} == rowId);

                    if (row is null)
                    {
                        return {{Problems}}.NotFound(messages, "{{name}}", rowId);
                    }


            """;

        var creation = string.Join(
            ", ",
            new[] { $"{detail.KeyName} = global::System.Guid.CreateVersion7()", $"{detail.Parent!.PropertyName} = entity.{_key}", "Position = position" }
                .Concat(writable.Select(property => $"{property.Name} = request.{property.Name}")));

        _handlers.Append(Command(
            "Add",
            $"        {prefix}Create{name}Request request,\n",
            $$"""
                    // Yeni satır sona eklenir.
                    var position = entity.{{collection}}.Count == 0 ? 0 : entity.{{collection}}.Max(candidate => candidate.Position) + 1;
                    var added = new {{detail.QualifiedName}} { {{creation}} };
                    entity.{{collection}}.Add(added);
                    context.Add(added);

            """));

        _handlers.Append(Command(
            "Change",
            $"        global::System.Guid rowId,\n        {prefix}Create{name}Request request,\n",
            find + string.Concat(writable.Select(property => $"        row.{property.Name} = request.{property.Name};\n"))));

        _handlers.Append(Command(
            "Remove",
            "        global::System.Guid rowId,\n",
            find + $"        entity.{collection}.Remove(row);\n        context.Remove(row);\n"));
    }

    /// <summary>Kaynak bir ebeveynin altındaysa, oluşturmadan önce ebeveynin var (ve görünür) olduğu doğrulanır.</summary>
    private string ParentCheck() => Parent is null
        ? string.Empty
        : $$"""
                    if (!await contexts.For<{{Parent.TargetType}}>().Set<{{Parent.TargetType}}>()
                        .AnyAsync(parent => parent.{{Parent.TargetKeyName}} == {{ParentParameter}}, cancellationToken))
                    {
                        return {{Problems}}.NotFound(messages, "{{Parent.TargetName}}", {{ParentParameter}});
                    }


            """;

    /// <summary>
    /// Yazılan her referansın hedefinin var olduğunu doğrular. Sorgu hedefin kendi filtrelerinden geçer;
    /// böylece başka bir kiracının veya silinmiş bir kaydın kimliği verilerek referans kurulamaz.
    /// </summary>
    /// <param name="creating">
    /// Kayıt oluşturuluyor mu. Güncellemede, yalnızca oluştururken yazılan referanslar istekte yer almaz ve
    /// yeniden doğrulanmaz.
    /// </param>
    /// <param name="source">
    /// Referansların okunduğu nesne. İstek yerine kaydın kendisi verilirse (kopya gibi, alanları başka bir kayıttan
    /// gelen yeni kayıtlarda) kayıttaki her referans doğrulanır; yalnızca istekte gönderilenler değil.
    /// </param>
    private string ReferenceChecks(bool patch, bool creating = true, string source = "request")
    {
        var builder = new StringBuilder();

        // Ebeveyn adresten, kaydın üretildiği kaynak üretme ucundan gelir; ikisi de orada doğrulanır.
        foreach (var reference in _resource.References.Where(reference => reference.Kind is not (ReferenceKind.Parent or ReferenceKind.Source)))
        {
            if (!creating && IsCreateOnly(reference.PropertyName))
            {
                continue;
            }

            var property = reference.PropertyName;
            var field = ResourceEmitter.CamelCase(property);

            // Ağaçta ebeveyn, aynı üst kaynağın altındaki bir düğüm olmalıdır.
            var scope = reference.Kind == ReferenceKind.Self ? ScopeCondition("target") : string.Empty;

            string condition;
            string value;

            if (patch)
            {
                value = $"{field}Reference";
                condition = $"((global::Haldus.IHaldusPatch)request).IsSet(\"{property}\") && request.{property} is {{ }} {value}\n            && ";
            }
            else if (reference.IsRequired)
            {
                value = $"{source}.{property}";
                condition = string.Empty;
            }
            else
            {
                value = $"{field}Reference";
                condition = $"{source}.{property} is {{ }} {value}\n            && ";
            }

            builder.Append($$"""
                        if ({{condition}}!await contexts.For<{{reference.TargetType}}>().Set<{{reference.TargetType}}>()
                            .AnyAsync(target => target.{{reference.TargetKeyName}} == {{value}}{{scope}}, cancellationToken))
                        {
                            return {{Problems}}.ReferenceNotFound(messages, "{{field}}", "{{reference.TargetName}}", {{value}});
                        }


                """);
        }

        return builder.ToString();
    }

    /// <summary>Ağaçta bir düğüm kendi altındaki bir düğümün çocuğu yapılamaz.</summary>
    private string CycleCheck(bool patch)
    {
        // Ebeveyn yalnızca oluştururken yazılıyorsa düğüm sonradan taşınamaz; döngü de oluşamaz.
        if (_resource.SelfReference is not { } self || IsCreateOnly(self.PropertyName))
        {
            return string.Empty;
        }

        var isSet = patch ? $"((global::Haldus.IHaldusPatch)request).IsSet(\"{self.PropertyName}\") && " : string.Empty;

        return $$"""
                    if ({{isSet}}await CreatesCycleAsync(contexts, {{_keyParameter}}, request.{{self.PropertyName}}, cancellationToken))
                    {
                        return {{Problems}}.HierarchyCycle(messages, "{{ResourceEmitter.CamelCase(self.PropertyName)}}");
                    }


            """;
    }

    private void EmitCycleHelper()
    {
        if (_resource.SelfReference is not { } self)
        {
            return;
        }

        _handlers.Append($$"""

                /// <summary>Yeni ebeveynden köke doğru çıkar; yolda kaydın kendisine rastlanırsa döngü oluşur.</summary>
                private static async {{Task}}<bool> CreatesCycleAsync(
                    global::Haldus.IHaldusDbContextProvider contexts,
                    {{_resource.IdType}} {{_keyParameter}},
                    {{self.TargetKeyType}}? parent,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    var nodes = contexts.For<{{_entity}}>().Set<{{_entity}}>().AsNoTracking();

                    for (var depth = 0; parent is { } current; depth++)
                    {
                        if (current == {{_keyParameter}} || depth >= global::Haldus.HaldusModel.MaxHierarchyDepth)
                        {
                            return true;
                        }

                        parent = await nodes
                            .Where(item => item.{{_key}} == current)
                            .Select(item => item.{{self.PropertyName}})
                            .FirstOrDefaultAsync(cancellationToken);
                    }

                    return false;
                }

            """);
    }

    /// <summary>
    /// Başka kayıtların referans verdiği bir kayıt silinemez; veritabanının reddi 500 yerine çakışma olarak
    /// bildirilir. Bu her fiziksel silmede yapılır, çünkü kayda kimin referans verdiği burada bilinemez:
    /// referans başka bir modülden veya kullanıcının elle yazdığı bir ilişkiden gelebilir. Silme geri
    /// alınabilirse kayıt fiziksel olarak kalkmadığı için bu durum oluşmaz.
    /// </summary>
    private string SaveDelete()
    {
        if (_resource.Has(Traits.SoftDeletable))
        {
            return Save();
        }

        var concurrency = _concurrency || GuardsState
            ? $$"""
                    catch (global::Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
                    {
                        return {{Problems}}.{{(_concurrency ? "PreconditionFailed" : "StateChanged")}}(messages);
                    }

            """
            : string.Empty;

        return $$"""
                    try
                    {
                        await context.SaveChangesAsync(cancellationToken);
                    }
            {{concurrency}}        catch (global::Microsoft.EntityFrameworkCore.DbUpdateException)
                    {
                        return {{Problems}}.InUse(messages, "{{_entity}}", {{_keyParameter}});
                    }

            """;
    }

    private string UpdateAssignments() =>
        string.Concat(_updatable.Select(property => $"        entity.{property.Name} = request.{property.Name};\n"))
        + (HasMetadata ? "        entity.Metadata = global::Haldus.HaldusMetadata.Create(request.Metadata);\n" : string.Empty);

    private string PatchAssignments() => PatchAssignments(_updatable);

    /// <summary>Kısmi bir isteğin gönderdiği alanları kayda yazar; yalnızca gönderilenler değişir.</summary>
    private string PatchAssignments(IEnumerable<PropertyModel> properties)
    {
        var builder = new StringBuilder();

        foreach (var property in properties)
        {
            var value = property switch
            {
                { IsNonNullableReference: true } => $"request.{property.Name}!",
                { IsNonNullableValue: true } => $"request.{property.Name}.GetValueOrDefault()",
                _ => $"request.{property.Name}",
            };

            builder.Append($$"""
                        if (((global::Haldus.IHaldusPatch)request).IsSet("{{property.Name}}"))
                        {
                            entity.{{property.Name}} = {{value}};
                        }


                """);
        }

        if (HasMetadata)
        {
            // Çanta birleştirilir: gönderilen anahtarlar yazılır, null gönderilenler silinir. Sınır, isteğin
            // kendisine değil birleşmiş çantaya uygulanır.
            builder.Append($$"""
                        if (((global::Haldus.IHaldusPatch)request).IsSet("Metadata"))
                        {
                            entity.Metadata = global::Haldus.HaldusMetadata.Merge(entity.Metadata, request.Metadata);

                            if (entity.Metadata.Count > global::Haldus.HaldusMetadata.MaxEntries)
                            {
                                return {{Problems}}.MetadataInvalid(messages);
                            }
                        }


                """);
        }

        return builder.ToString();
    }

    /// <summary>Bir yazmanın, kayda dokunmadan önce yaptığı kontroller: referansların varlığı ve doğal anahtarın benzersizliği.</summary>
    private string WriteChecks(bool patch, bool creating) => ReferenceChecks(patch, creating) + NaturalKeyCheck(patch, creating);

    private bool IsCreateOnly(string propertyName) =>
        _resource.Properties.Any(property => property.Name == propertyName && property.IsCreateOnly);

    /// <summary>
    /// Doğal anahtarı aynı olan başka bir kayıt varsa yazmayı reddeder. Silinmiş ve süresi dolmuş kayıtlar da
    /// değeri tutar (indeks onları da kapsar); kiracı, sahip ve ebeveyn süzgeçleri ise kapsamı çizdiği için kalır.
    /// </summary>
    private string NaturalKeyCheck(bool patch, bool creating)
    {
        if (NaturalKey is not { } key
            || _resource.Properties.FirstOrDefault(candidate => candidate.Name == key.PropertyName) is not { IsWritable: true } property
            || (!creating && property.IsCreateOnly))
        {
            return string.Empty;
        }

        var isSet = patch ? $"((global::Haldus.IHaldusPatch)request).IsSet(\"{key.PropertyName}\")\n            && " : string.Empty;
        var self = creating ? string.Empty : $" && item.{_key} != {_keyParameter}";

        return NaturalKeyQuery(key, $"request.{key.PropertyName}", isSet, self);
    }

    /// <param name="value">Benzersiz olması gereken değeri veren ifade.</param>
    /// <param name="condition">Kontrolün önüne konan koşul (ör. alan PATCH'te gönderildiyse).</param>
    /// <param name="self">Kaydın kendisini dışarıda bırakan koşul; yeni kayıtta boştur.</param>
    private string NaturalKeyQuery(NaturalKeyModel key, string value, string condition, string self)
    {
        var hidden = new List<string>();
        if (_resource.Has(Traits.SoftDeletable))
        {
            hidden.Add("global::Haldus.HaldusFilters.SoftDelete");
        }

        if (_resource.Expiry is not null)
        {
            hidden.Add("global::Haldus.HaldusFilters.Expiry");
        }

        var ignore = hidden.Count == 0 ? string.Empty : $".IgnoreQueryFilters(new[] {{ {string.Join(", ", hidden)} }})";

        // Adrese girmeyen ebeveyn yalnızca oluşturma isteğinde gelir; var olan kayıtta kaydın kendisinden okunur.
        var owner = self.Length == 0 && value.StartsWith("request.", StringComparison.Ordinal) ? "request" : "entity";
        var under = _resource.ShallowParent is { } shallow ? $" && item.{shallow.PropertyName} == {owner}.{shallow.PropertyName}" : string.Empty;

        return $$"""
                    if ({{condition}}await contexts.For<{{_entity}}>().Set<{{_entity}}>(){{ignore}}
                        .AnyAsync(item => item.{{key.PropertyName}} == {{value}}{{self}}{{ScopeCondition("item")}}{{under}}, cancellationToken))
                    {
                        return {{Problems}}.Duplicate(messages, "{{_entity}}", "{{ResourceEmitter.CamelCase(key.PropertyName)}}", {{value}});
                    }


            """;
    }

    // ── Sıra ───────────────────────────────────────────────────────────────────────────────────────────
    // Konumlar kapsam içinde boşluksuzdur; kapsamın üyelerini değiştiren her yazma aynı kilidin altında yapılır.

    private bool Orderable => _resource.Has(Traits.Orderable);

    /// <summary>Ağaçta bir düğüm, güncellemeyle başka bir üst düğüme taşınabilir mi; taşınırsa sırası da değişir.</summary>
    private bool OrderFollowsParent =>
        Orderable && _resource.SelfReference is { } self && !IsCreateOnly(self.PropertyName);

    /// <summary>Sıranın kapsamındaki kayıtlar: aynı ebeveynin altındakiler ve, ağaçta, aynı üst düğümün çocukları.</summary>
    /// <param name="under">Ağaçta üst düğümü veren ifade.</param>
    private string Siblings(string under) =>
        $"context.Set<{_entity}>(){ScopeFilter}"
        + (_resource.ShallowParent is { } shallow ? $".Where(item => item.{shallow.PropertyName} == entity.{shallow.PropertyName})" : string.Empty)
        + (_resource.SelfReference is { } self ? $".Where(item => item.{self.PropertyName} == {under})" : string.Empty);

    private string SiblingsOf(string record) =>
        Siblings(_resource.SelfReference is { } self ? $"{record}.{self.PropertyName}" : string.Empty);

    /// <summary>
    /// Kilidin anahtarı kaynağı ve kapsamı ayırır. Ağaçta üst düğüm anahtara girmez: düğüm bir daldan ötekine
    /// taşınırken iki dal birlikte değişir ve tek kilit yeter.
    /// </summary>
    private string OrderLock()
    {
        var scope = new List<string>();
        if (_tenant)
        {
            scope.Add("global::Haldus.HaldusScope.TenantId(context)");
        }

        if (_resource.Has(Traits.Owned))
        {
            scope.Add("global::Haldus.HaldusScope.UserId(context)");
        }

        if (Parent is not null)
        {
            scope.Add(ParentParameter);
        }

        return $"await global::Haldus.HaldusOrdering.BeginAsync(context, \"{_plural}\", cancellationToken{string.Concat(scope.Select(part => ", " + part))})";
    }

    private string OrderBegin() => Orderable ? $"        await using var ordering = {OrderLock()};\n" : string.Empty;

    /// <summary>Yeni (veya geri yüklenen) kaydı kapsamının sonuna koyar.</summary>
    private string OrderAppend() => Orderable
        ? OrderBegin() + $"        entity.Position = await global::Haldus.HaldusOrdering.NextAsync({SiblingsOf("entity")}, cancellationToken);\n"
        : string.Empty;

    /// <summary>Kapsamdan çıkan kaydın arkasındakileri bir öne alır.</summary>
    private string OrderCloseGap() => Orderable
        ? $"        await global::Haldus.HaldusOrdering.CloseGapAsync({SiblingsOf("entity")}, entity.Position, cancellationToken);\n"
        : string.Empty;

    private string OrderCommit() => Orderable ? "        await ordering.CommitAsync(cancellationToken);\n" : string.Empty;

    private string OrderRemember() => OrderFollowsParent
        ? $"        var orderedUnder = entity.{_resource.SelfReference!.PropertyName};\n"
        : string.Empty;

    /// <summary>Düğüm başka bir üst düğüme taşındıysa eski yerindeki boşluğu kapatır ve yeni dalın sonuna koyar.</summary>
    private string OrderReparent() => OrderFollowsParent
        ? $$"""
                    await using var ordering = entity.{{_resource.SelfReference!.PropertyName}} == orderedUnder
                        ? null
                        : {{OrderLock()}};

                    if (ordering is not null)
                    {
                        await global::Haldus.HaldusOrdering.CloseGapAsync({{Siblings("orderedUnder")}}, entity.Position, cancellationToken);
                        entity.Position = await global::Haldus.HaldusOrdering.NextAsync({{SiblingsOf("entity")}}, cancellationToken);
                    }


            """
        : string.Empty;

    private string OrderReparentCommit() => OrderFollowsParent
        ? """

                    if (ordering is not null)
                    {
                        await ordering.CommitAsync(cancellationToken);
                    }

            """
        : string.Empty;

    /// <summary>Kaydı kapsamındaki başka bir yere taşır: <c>POST /{id}/move</c>.</summary>
    private void EmitMove()
    {
        _map.Append($$"""
                    group.MapPost("{{_keyRoute}}/move", MoveAsync)
                        .WithName("Move{{_entity}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<global::Haldus.MoveRequest>>()
                        .ProducesValidationProblem()
                        .ProducesProblem(404){{StateMetadata(null)}}{{ConcurrencyMetadata()}};


            """);

        _handlers.Append($$"""

                private static async {{OkOrProblem}} MoveAsync(
            {{ScopeParameter}}{{KeyParameter}}        global::Haldus.MoveRequest request,
            {{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}{{OwnerGuard("move")}}        var context = contexts.For<{{_entity}}>();
            {{OrderBegin()}}        var entity = await context.Set<{{_entity}}>().FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ExpectVersion()}}        // Kapsamdaki kayıtlar kilidin altında okunur ve yeniden numaralanır; değişenler birlikte kaydedilir.
                    await global::Haldus.HaldusOrdering.MoveAsync(
                        {{SiblingsOf("entity")}}.OrderBy(item => item.Position).ThenBy(item => item.{{_key}}),
                        entity,
                        request.Position.GetValueOrDefault(),
                        cancellationToken);
            {{Save()}}{{OrderCommit()}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """);
    }

    /// <summary>Gömülü satırları başka bir kayıttan, kendi kimlikleriyle ve sıraları korunarak kopyalar.</summary>
    private string CopyRows(string source)
    {
        var rows = new StringBuilder();

        foreach (var detail in _resource.Details)
        {
            var position = ResourceEmitter.CamelCase(detail.CollectionName) + "Position";
            var assignments = string.Join(
                ", ",
                new[] { $"{detail.KeyName} = global::System.Guid.CreateVersion7()", $"Position = {position}++" }
                    .Concat(detail.Properties.Where(property => property.IsWritable).Select(property => $"{property.Name} = row.{property.Name}")));

            rows.Append($$"""

                        var {{position}} = 0;
                        foreach (var row in {{source}}.{{detail.CollectionName}}.OrderBy(row => row.Position).ThenBy(row => row.{{detail.KeyName}}))
                        {
                            entity.{{detail.CollectionName}}.Add(new {{detail.QualifiedName}} { {{assignments}} });
                        }

                """);
        }

        return rows.ToString();
    }

    /// <summary>
    /// Bir kaydın yüklenmiş satırlarını kendi sıralarına dizer. Veritabanı satırları belirli bir sırayla vermez;
    /// kullanıcının sözleşmesi onları okurken (ör. başka bir kayda taşırken) sıra korunmalıdır.
    /// </summary>
    private static string SortRows(string record, IEnumerable<string> collections) => string.Concat(collections.Select(collection =>
        $"        {record}.{collection}.Sort(static (left, right) => left.Position.CompareTo(right.Position));\n"));

    /// <summary>
    /// Kullanıcının kodu satır ekleyip çıkarmış olabilir; kimliği olmayan satıra kimlik verilir ve sıra, satırların
    /// koleksiyondaki yerine göre yeniden yazılır.
    /// </summary>
    private string NormalizeRows()
    {
        var rows = new StringBuilder();

        foreach (var detail in _resource.Details)
        {
            var index = ResourceEmitter.CamelCase(detail.CollectionName) + "Index";

            rows.Append($$"""
                        var {{index}} = 0;
                        foreach (var row in entity.{{detail.CollectionName}})
                        {
                            if (row.{{detail.KeyName}} == default)
                            {
                                row.{{detail.KeyName}} = global::System.Guid.CreateVersion7();
                            }

                            row.Position = {{index}}++;
                        }


                """);
        }

        return rows.ToString();
    }

    // ── Zincir: ters kayıt ve yerine geçme ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Kapanmış bir halka (ters çevrilmiş veya yerine geçilmiş kayıt) değiştirilemez: karşı kaydı onun o anki
    /// halinden üretilmiştir.
    /// </summary>
    private string ChainGuard() => ChainClosedGuard() + ApprovalLock() + string.Concat(ChainOperation.For(_resource).Where(chain => chain.Terminal).Select(chain => $$"""
                if (entity.{{chain.Forward}} is not null)
                {
                    return {{Problems}}.ChainLocked(messages, global::Haldus.HaldusMessageKeys.ReversalLockedDetail, "{{_entity}}", {{_keyParameter}});
                }


        """));

    // ── Sürümler ve değişiklik akışı ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Kaydın saklanan sürümleri: liste, tek sürüm ve geri yükleme. Anlık görüntüyü interceptor alır; burada
    /// görüntüden izlenmeyen bir kayıt kurulur (<c>HaldusRevisions.FindAsync</c>) ve yanıt, her yanıt gibi,
    /// <c>ToResponse</c> ile ondan üretilir. Geri yükleme bir güncellemedir ve onun korumalarından geçer.
    /// </summary>
    private void EmitRevisions()
    {
        const string Revisions = "global::Haldus.PagedResult<global::Haldus.RevisionInfo>";

        _map.Append($$"""
                    group.MapGet("{{_keyRoute}}/revisions", ListRevisionsAsync)
                        .WithName("List{{_entity}}Revisions")
                        .ProducesProblem(404);

                    group.MapGet("{{_keyRoute}}/revisions/{revisionId:long}", GetRevisionAsync)
                        .WithName("Get{{_entity}}Revision")
                        .ProducesProblem(404);


            """);

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Results<{{Results}}.Ok<{{Revisions}}>, {{Problem}}>> ListRevisionsAsync(
            {{ScopeParameter}}{{KeyParameter}}        [{{Http}}.AsParameters] global::Haldus.PageRequest page,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>().AsNoTracking().FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

                    return {{Http}}.TypedResults.Ok(await global::Haldus.HaldusRevisions.ListAsync(
                        context, entity, {{KeyText}}, page.Skip, page.PageOrDefault, page.PageSizeOrDefault, cancellationToken));
                }

                private static async {{OkOrProblem}} GetRevisionAsync(
            {{ScopeParameter}}{{KeyParameter}}        long revisionId,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>().AsNoTracking().FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

                    // Sürüm yalnızca bu kaydın sürümleri arasında aranır; başka bir kaydın sürümü bulunamaz sayılır.
                    var revision = await global::Haldus.HaldusRevisions.FindAsync(context, entity, {{KeyText}}, revisionId, cancellationToken);

                    if (revision is null)
                    {
                        return {{Problems}}.NotFound(messages, "{{_entity}}Revision", revisionId);
                    }

                    return {{Http}}.TypedResults.Ok(ToResponse(revision));
                }

            """);

        if (!_resource.Has(Operations.Update))
        {
            return;
        }

        // Ağaçtaki yer içerik değildir; düğümü eski yerine taşımak döngü kurabilir, bu yüzden geri yüklenmez.
        var restored = _updatable.Where(property => _resource.References.All(reference =>
            reference.Kind != ReferenceKind.Self || reference.PropertyName != property.Name));
        var assignments = string.Concat(restored.Select(property => $"        entity.{property.Name} = revision.{property.Name};\n"))
            + (HasMetadata ? "        entity.Metadata = new(revision.Metadata);\n" : string.Empty);

        var rows = new StringBuilder();

        foreach (var detail in _resource.Details)
        {
            var collection = detail.CollectionName;
            var local = ResourceEmitter.CamelCase(collection);
            var fields = string.Concat(detail.Properties
                .Where(property => property.IsWritable)
                .Select(property => $"            row.{property.Name} = source.{property.Name};\n"));

            // Satırlar kimlikleriyle geri gelir: duran satır güncellenir, silinmiş olan aynı kimlikle yeniden eklenir,
            // sürümde bulunmayan silinir.
            rows.Append($$"""

                        var {{local}} = entity.{{collection}}.ToDictionary(row => row.{{detail.KeyName}});
                        var {{local}}Position = 0;

                        foreach (var source in revision.{{collection}}.OrderBy(row => row.Position).ThenBy(row => row.{{detail.KeyName}}))
                        {
                            if (!{{local}}.Remove(source.{{detail.KeyName}}, out var row))
                            {
                                row = new {{detail.QualifiedName}} { {{detail.KeyName}} = source.{{detail.KeyName}}, {{detail.Parent!.PropertyName}} = entity.{{_key}} };
                                entity.{{collection}}.Add(row);
                                context.Add(row);
                            }

                            row.Position = {{local}}Position++;
                {{fields}}        }

                        foreach (var removed in {{local}}.Values)
                        {
                            entity.{{collection}}.Remove(removed);
                            context.Remove(removed);
                        }

                """);
        }

        var touch = _resource.Details.Length == 0
            ? string.Empty
            : "\n        // Yalnızca satırlar değişse de bu, kaydın bir yazmasıdır.\n        context.Entry(entity).State = global::Microsoft.EntityFrameworkCore.EntityState.Modified;\n";

        // Geri gelen referansların hedefleri bu arada silinmiş veya görünmez olmuş olabilir; yeniden doğrulanır.
        var references = ReferenceChecks(patch: false, creating: false, source: "entity");

        _map.Append($$"""
                    group.MapPost("{{_keyRoute}}/revisions/{revisionId:long}/rollback", RollbackAsync)
                        .WithName("Rollback{{_entity}}")
                        .ProducesProblem(400)
                        .ProducesProblem(404){{StateMetadata(_resource.Workflow?.EditableIn)}}{{ConcurrencyMetadata()}};


            """);

        _handlers.Append($$"""

                private static async {{OkOrProblem}} RollbackAsync(
            {{ScopeParameter}}{{KeyParameter}}        long revisionId,
            {{HttpContextParameter}}{{(Sliding ? TimeProviderParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}{{OwnerGuard("update")}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

                    var revision = await global::Haldus.HaldusRevisions.FindAsync(context, entity, {{KeyText}}, revisionId, cancellationToken);

                    if (revision is null)
                    {
                        return {{Problems}}.NotFound(messages, "{{_entity}}Revision", revisionId);
                    }

            {{ChainGuard()}}{{StateGuard(_resource.Workflow?.EditableIn, "update")}}{{ExpectVersion()}}{{assignments}}{{rows}}{{touch}}
            {{references}}{{Slide()}}{{Compute()}}{{Save()}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """);
    }

    /// <summary>
    /// Değişiklik akışı. Günlükten yalnızca isteği yapanın görebileceği kayıtların satırları okunur
    /// (<c>HaldusChanges.ReadAsync</c>); değişen kayıtlar kaynağın kendi filtrelerinden geçerek yüklenir ve artık
    /// bulunamayanlar silinmiş olarak bildirilir.
    /// </summary>
    private void EmitChanges()
    {
        var page = $"global::Haldus.ChangeFeedPage<{_entity}Response>";
        var change = $"global::Haldus.Change<{_entity}Response>";
        var parse = _resource.IdKind == IdKind.Guid ? "global::System.Guid.Parse(item.RecordId)" : "item.RecordId";
        var text = _resource.IdKind == IdKind.Guid
            ? $"record.{_key}.ToString()"
            : $"record.{_key}";
        var parent = Parent is null
            ? "null"
            : $"global::System.Convert.ToString({ParentParameter}, global::System.Globalization.CultureInfo.InvariantCulture)";
        var returnType = Parent is null
            ? $"{Task}<{Results}.Ok<{page}>>"
            : $"{Task}<{Results}.Results<{Results}.Ok<{page}>, {Problem}>>";

        _map.Append($$"""
                    group.MapGet("/changes", ChangesAsync)
                        .WithName("List{{_entity}}Changes"){{(Parent is null ? string.Empty : "\n            .ProducesProblem(404)")}};


            """);

        _handlers.Append($$"""

                private static async {{returnType}} ChangesAsync(
            {{ScopeParameter}}        long? since,
                    int? pageSize,
            {{ContextsParameter}}{{(Parent is null ? string.Empty : MessagesParameter)}}{{CancellationParameter}}    {
            {{ParentCheck()}}        var context = contexts.For<{{_entity}}>();
                    var window = await global::Haldus.HaldusChanges.ReadAsync<{{_entity}}>(context, since, pageSize, {{parent}}, cancellationToken);
                    var ids = window.Items.Select(item => {{parse}}).ToList();

                    var records = await context.Set<{{_entity}}>()
                        .AsNoTracking(){{Includes}}
                        .Where(item => ids.Contains(item.{{_key}}){{ScopeCondition("item")}})
                        .ToListAsync(cancellationToken);
                    var current = records.ToDictionary(record => {{text}});

                    var items = window.Items
                        .Select(item => current.TryGetValue(item.RecordId, out var record)
                            ? new {{change}}(item.RecordId, Deleted: false, ToResponse(record), item.ChangedAt)
                            : new {{change}}(item.RecordId, Deleted: true, null, item.ChangedAt))
                        .ToList();

                    return {{Http}}.TypedResults.Ok(new {{page}}(items, window.Next, window.HasMore));
                }

            """);
    }

    // ── Taslak ve yayın ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Yayındaki kayıt silinemez: yayındaki kopyası sessizce onunla birlikte giderdi. Önce yayından kaldırılır.</summary>
    private string PublishedGuard() => !_resource.Has(Traits.Publishable)
        ? string.Empty
        : $$"""
                    if (entity.PublishedAt is not null)
                    {
                        return {{Problems}}.StateConflict(messages, "delete", "Published");
                    }


            """;

    /// <summary>
    /// Yayın uçları. Yayındaki hal aynı tabloda, taslağına işaret eden (<c>PublishedOfId</c>) ayrı bir satırdır ve
    /// modeldeki <c>HaldusFilters.Publication</c> filtresiyle kaynağın bütün sıradan sorgularından gizlenir;
    /// yalnızca buradaki uçlar o filtreyi (ve yalnızca onu) kaldırır. Kopya, taslağın yazılabilir alanlarını ve
    /// gömülü satırlarını taşır ve her yayında yeniden kurulur. Dışarıya taslağın kimliğiyle gösterilir.
    /// </summary>
    private void EmitPublishing()
    {
        const string Publication = "global::Haldus.HaldusFilters.Publication";

        var copies = $"contexts.For<{_entity}>().Set<{_entity}>().AsNoTracking(){Includes}\n            .IgnoreQueryFilters(new[] {{ {Publication} }})";
        var draftId = _resource.IdKind == IdKind.String ? "copy.PublishedOfId!" : "copy.PublishedOfId!.Value";
        var paged = $"global::Haldus.PagedResult<{_entity}Response>";
        var position = Orderable ? ", Position = source.Position" : string.Empty;

        _map.Append($$"""
                    group.MapGet("/published", ListPublishedAsync)
                        .WithName("ListPublished{{_plural}}");

                    group.MapGet("/published{{_keyRoute}}", GetPublishedAsync)
                        .WithName("GetPublished{{_entity}}")
                        .ProducesProblem(404);

                    group.MapPost("{{_keyRoute}}/publish", PublishAsync)
                        .WithName("Publish{{_entity}}")
                        .ProducesProblem(404)
                        .ProducesProblem(409){{ConcurrencyMetadata()}};

                    group.MapPost("{{_keyRoute}}/unpublish", UnpublishAsync)
                        .WithName("Unpublish{{_entity}}")
                        .ProducesProblem(404){{ConcurrencyMetadata()}};


            """);

        _handlers.Append($$"""

                /// <summary>Yayındaki kayıtlar: taslakların son yayınlanan halleri, en son yayınlanan başta.</summary>
                private static async {{Task}}<{{Results}}.Ok<{{paged}}>> ListPublishedAsync(
            {{ScopeParameter}}        [{{Http}}.AsParameters] global::Haldus.PageRequest page,
            {{FilterParameters()}}{{ContextsParameter}}{{CancellationParameter}}    {
                    var query = {{copies}}
                        .Where(item => item.PublishedOfId != null){{ScopeFilter}};
            {{ApplyFilter()}}
                    var totalCount = await query.LongCountAsync(cancellationToken);
                    var copies = await query
                        .OrderByDescending(item => item.PublishedAt)
                        .ThenBy(item => item.{{_key}})
                        .Skip(page.Skip)
                        .Take(page.PageSizeOrDefault)
                        .ToListAsync(cancellationToken);

                    return {{Http}}.TypedResults.Ok(new {{paged}}(
                        copies.Select(ToPublished).ToList(), page.PageOrDefault, page.PageSizeOrDefault, totalCount));
                }

                private static async {{OkOrProblem}} GetPublishedAsync(
            {{ScopeParameter}}{{KeyParameter}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var copy = await {{copies}}
                        .FirstOrDefaultAsync(item => item.PublishedOfId == {{_keyParameter}}{{ScopeCondition("item")}}, cancellationToken);

                    if (copy is null)
                    {
                        return {{NotFound}};
                    }

                    return {{Http}}.TypedResults.Ok(ToPublished(copy));
                }

                /// <summary>Kopya, taslağının kimliğiyle gösterilir; kendi anahtarı dışarıya çıkmaz.</summary>
                private static {{_entity}}Response ToPublished({{_entity}} copy) => ToResponse(copy) with { {{_key}} = {{draftId}} };

                /// <summary>Taslağın şimdiki halini yayına alır; önceki yayın bununla değişir.</summary>
                private static async {{OkOrProblem}} PublishAsync(
            {{ScopeParameter}}{{KeyParameter}}{{HttpContextParameter}}{{TimeProviderParameter}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}{{OwnerGuard("publish")}}        var context = contexts.For<{{_entity}}>();
                    var source = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (source is null)
                    {
                        return {{NotFound}};
                    }

            {{ExpectVersion().Replace("Entry(entity)", "Entry(source)")}}        var previous = await context.Set<{{_entity}}>()
                        .IgnoreQueryFilters(new[] { {{Publication}} })
                        .FirstOrDefaultAsync(item => item.PublishedOfId == source.{{_key}}, cancellationToken);

                    if (previous is not null)
                    {
                        // Satırları veritabanı onunla birlikte siler.
                        context.Set<{{_entity}}>().Remove(previous);
                    }

                    var now = global::Haldus.HaldusTime.UtcNow(timeProvider);
                    var entity = new {{_entity}} { {{CreateInitializer(source: "source")}}{{position}}, PublishedOfId = source.{{_key}}, PublishedAt = now };
            {{CopyRows("source")}}{{Compute()}}
                    source.PublishedAt = now;
                    context.Set<{{_entity}}>().Add(entity);

                    try
                    {
                        await context.SaveChangesAsync(cancellationToken);
                    }
                    catch (global::Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
                    {
                        // Taslak ya da önceki kopya bu sırada başka bir istekle değişti.
                        return {{Problems}}.{{(_concurrency ? "PreconditionFailed" : "StateChanged")}}(messages);
                    }
                    catch (global::Microsoft.EntityFrameworkCore.DbUpdateException taken)
                        when (global::Haldus.HaldusDb.IsUniqueViolation(taken, "UX_{{_plural}}_PublishedOfId"))
                    {
                        // Aynı taslağı aynı anda yayınlayan iki istekten ikincisi: bir taslağın tek kopyası olur.
                        return {{Problems}}.StateChanged(messages);
                    }

            {{WriteETag().Replace("entity.Version", "source.Version")}}        return {{Http}}.TypedResults.Ok(ToResponse(source));
                }

                /// <summary>Kaydı yayından kaldırır; taslak durur. Yayında olmayan kayıtta bir şey değişmez.</summary>
                private static async {{OkOrProblem}} UnpublishAsync(
            {{ScopeParameter}}{{KeyParameter}}{{HttpContextParameter}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}{{OwnerGuard("unpublish")}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ExpectVersion()}}        var copy = await context.Set<{{_entity}}>()
                        .IgnoreQueryFilters(new[] { {{Publication}} })
                        .FirstOrDefaultAsync(item => item.PublishedOfId == entity.{{_key}}, cancellationToken);

                    if (copy is not null)
                    {
                        context.Set<{{_entity}}>().Remove(copy);
                    }

                    entity.PublishedAt = null;
            {{Save()}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """);
    }

    // ── Onay ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Onaylanmış kaydın içeriği değişmez: onay, onaylayanın gördüğü içeriğe verilmiştir.</summary>
    private string ApprovalLock() => _resource.Approval is null
        ? string.Empty
        : $$"""
                    if (entity.ApprovalStatus == global::Haldus.ApprovalStatus.Approved)
                    {
                        return {{Problems}}.StateConflict(messages, "update", entity.ApprovalStatus);
                    }


            """;

    /// <summary>
    /// İçerik değişince onayı düşürmek interceptor'ın işidir (kaydeden her yolda, hangi koddan gelirse gelsin
    /// çalışır). Önizleme kaydetmediği için sonucu doğru göstermek üzere aynı kuralı kendisi uygular.
    /// </summary>
    private string ApprovalPreview() => _resource.Approval is null
        ? string.Empty
        : "        global::Haldus.HaldusApprovals.ResetIfChanged(entity, context);\n";

    /// <summary>
    /// Onay isteyen geçiş veya aksiyon, kayıt onaylanmadan çalışmaz. Onayın izin verdiği işlemin kendi yazdıkları
    /// onayı düşürmez; başka her içerik değişikliği düşürür.
    /// </summary>
    private string ApprovalGate(string route) => _resource.Approval is not { } approval || !approval.RequiredFor.Contains(route)
        ? string.Empty
        : $$"""
                    if (entity.ApprovalStatus != global::Haldus.ApprovalStatus.Approved)
                    {
                        return {{Problems}}.StateConflict(messages, "{{route}}", entity.ApprovalStatus);
                    }

                    global::Haldus.HaldusApprovals.Exempt(entity);


            """;

    private string KeyText => $"global::System.Convert.ToString({_keyParameter}, global::System.Globalization.CultureInfo.InvariantCulture)!";

    /// <summary>
    /// Onay ve ret uçları. Kimin karar verebileceği ve kararın kayda nasıl yazıldığı çalışma zamanındadır
    /// (<c>HaldusApprovals.DecideAsync</c>); uç kaydı bulur, yazma kurallarından geçirir ve kaydeder.
    /// </summary>
    private void EmitApproval(bool approve)
    {
        var name = approve ? "Approve" : "Reject";
        var route = approve ? "approve" : "reject";

        _map.Append($$"""
                    group.MapPost("{{_keyRoute}}/{{route}}", {{name}}Async)
                        .WithName("{{name}}{{_entity}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<global::Haldus.ApprovalDecisionRequest>>()
                        .ProducesValidationProblem()
                        .ProducesProblem(401)
                        .ProducesProblem(403)
                        .ProducesProblem(404)
                        .ProducesProblem(409){{ConcurrencyMetadata()}};


            """);

        _handlers.Append($$"""

                private static async {{OkOrProblem}} {{name}}Async(
            {{ScopeParameter}}{{KeyParameter}}        global::Haldus.ApprovalDecisionRequest? request,
            {{HttpContextParameter}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}{{OwnerGuard(route)}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ChainClosedGuard()}}{{ExpectVersion()}}        var outcome = await global::Haldus.HaldusApprovals.DecideAsync(
                        context, entity, {{KeyText}}, approve: {{(approve ? "true" : "false")}}, request?.Note, levels: {{_resource.Approval!.Levels}}, cancellationToken);

                    if (outcome != global::Haldus.ApprovalOutcome.Recorded)
                    {
                        return {{Problems}}.ApprovalDenied(messages, outcome, "{{route}}", entity.ApprovalStatus);
                    }

            {{Save()}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """);
    }

    /// <summary>Kayıt için verilmiş kararların listesi; kayıt isteği yapana görünür değilse 404 döner.</summary>
    private void EmitApprovalList()
    {
        const string Decisions = "global::System.Collections.Generic.IReadOnlyList<global::Haldus.ApprovalDecision>";

        _map.Append($$"""
                    group.MapGet("{{_keyRoute}}/approvals", ListApprovalsAsync)
                        .WithName("List{{_entity}}Approvals")
                        .ProducesProblem(404);


            """);

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Results<{{Results}}.Ok<{{Decisions}}>, {{Problem}}>> ListApprovalsAsync(
            {{ScopeParameter}}{{KeyParameter}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>().AsNoTracking().FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

                    return {{Http}}.TypedResults.Ok(
                        await global::Haldus.HaldusApprovals.ListAsync(context, entity, {{KeyText}}, cancellationToken));
                }

            """);
    }

    /// <summary>
    /// Kapanmış bir kayıtta (ters çevrilmiş veya yerine geçilmiş) hiçbir yazma çalışmaz; geçişler ve aksiyonlar da.
    /// Ters kaydın kendisi ise düzenlenemez (<see cref="ChainGuard"/>) ama geçişlerden geçebilir: yeni bir kayıt
    /// olarak başlangıç durumunda doğar ve örneğin kesinleştirilmesi gerekir.
    /// </summary>
    private string ChainClosedGuard() => string.Concat(ChainOperation.For(_resource).Select(chain => $$"""
                if (entity.{{chain.Back}} is not null)
                {
                    return {{Problems}}.ChainLocked(messages, global::Haldus.HaldusMessageKeys.{{chain.LockedDetail}}, "{{_entity}}", {{_keyParameter}});
                }


        """));

    /// <summary>
    /// Bir çiftin parçası olan kayıt silinemez: silinirse öteki uç hiçbir şeye işaret etmez ya da sonsuza dek
    /// kapalı kalır. Hiçbir çifte girmemiş kayıt (ör. ters çevrilmemiş bir taslak) serbestçe silinir.
    /// </summary>
    private string ChainDeleteGuard() => PublishedGuard() + string.Concat(ChainOperation.For(_resource).Select(chain => $$"""
                if (entity.{{chain.Back}} is not null || entity.{{chain.Forward}} is not null)
                {
                    return {{Problems}}.ChainLocked(messages, global::Haldus.HaldusMessageKeys.ChainMemberDetail, "{{_entity}}", {{_keyParameter}});
                }


        """));

    /// <summary>
    /// Kaydın karşı kaydını (ters kayıt veya yerine geçen) üretir. Yeni kayıt eskisinin kopyası olarak kurulur,
    /// entity sözleşmeyi uyguluyorsa o çağrılır, sonra istekte gönderilen alanlar yazılır. İki kayıt tek kaydetmede
    /// birbirine bağlanır; bir kaydın yalnızca bir karşı kaydı olabilir.
    /// </summary>
    private void EmitChain(ChainOperation chain)
    {
        var request = $"{chain.Pascal}{_entity}Request";
        var locked = $"{Problems}.ChainLocked(messages, global::Haldus.HaldusMessageKeys.{chain.LockedDetail}, \"{_entity}\", {_keyParameter})";

        _map.Append($$"""
                    group.MapPost("{{_keyRoute}}/{{chain.Route}}", {{chain.Pascal}}Async)
                        .WithName("{{chain.Pascal}}{{_entity}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{{request}}>>()
                        .ProducesValidationProblem(){{IdempotencyFilter(request)}}{{(Idempotent ? "\n            .ProducesProblem(422)" : string.Empty)}}
                        .ProducesProblem(404)
                        .ProducesProblem(409){{ConcurrencyMetadata()}};


            """);

        // Ters kayıt zincirin sonudur; yeniden ters çevrilmesi asıl kaydı geri getirmez, yalnızca izini bulandırır.
        var terminal = chain.Terminal
            ? $$"""
                    if (original.{{chain.Forward}} is not null)
                    {
                        return {{Problems}}.ChainLocked(messages, global::Haldus.HaldusMessageKeys.ReversalDetail, "{{_entity}}", {{_keyParameter}});
                    }


            """
            : string.Empty;

        var implemented = _resource.Contracts.Contains(chain.Contract);
        var contract = implemented
            ? SortRows("original", _resource.Details.Select(detail => detail.CollectionName))
                + $"        ((global::Haldus.{chain.Contract}<{_entity}>)entity).{chain.ContractMethod}(original);\n\n"
            : string.Empty;

        // Asıl kayıt da değişir (karşı kaydına işaret eder); o sırada başkası değiştirdiyse kaydetme başarısız olur.
        var concurrency = _concurrency || GuardsState
            ? $$"""
                    catch (global::Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
                    {
                        return {{Problems}}.{{(_concurrency ? "PreconditionFailed" : "StateChanged")}}(messages);
                    }

            """
            : string.Empty;

        // Kontrolü birlikte geçen iki istekten ikincisi benzersiz indekse takılır.
        var taken = $$"""
                    catch (global::Microsoft.EntityFrameworkCore.DbUpdateException taken)
                        when (global::Haldus.HaldusDb.IsUniqueViolation(taken, "{{chain.IndexName(_resource)}}"))
                    {
                        return {{locked}};
                    }

            """;

        var duplicate = NaturalKey is { } key
            ? NaturalKeyQuery(key, $"entity.{key.PropertyName}", string.Empty, string.Empty)
            : string.Empty;

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Results<{{Results}}.Created<{{_entity}}Response>, {{Problem}}>> {{chain.Pascal}}Async(
            {{ScopeParameter}}{{KeyParameter}}        {{request}}? request,
            {{HttpContextParameter}}{{(HasLifetime ? TimeProviderParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}{{OwnerGuard(chain.Route)}}        // Gövde isteğe bağlıdır; gönderilmediyse hiçbir alan değişmez.
                    request ??= new {{request}}();

                    var context = contexts.For<{{_entity}}>();
                    var original = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (original is null)
                    {
                        return {{NotFound}};
                    }

                    if (original.{{chain.Back}} is not null)
                    {
                        return {{locked}};
                    }

            {{terminal}}{{StateGuard(chain.AllowedIn(_resource), chain.Route).Replace("entity.Status", "original.Status")}}{{ExpectVersion().Replace("Entry(entity)", "Entry(original)")}}        var entity = new {{_entity}} { {{CreateInitializer(source: "original")}}, {{chain.Forward}} = original.{{_key}} };
            {{CopyRows("original")}}
            {{contract}}{{PatchAssignments(_writable)}}{{(implemented ? NormalizeRows() : string.Empty)}}{{ReferenceChecks(patch: false, source: "entity")}}{{Compute()}}{{duplicate}}        original.{{chain.Back}} = entity.{{_key}};
            {{SaveCreated(concurrency + taken)}}

            {{WriteETag()}}        return {{Http}}.TypedResults.Created(global::Haldus.HaldusRoutes.Sibling(httpContext, entity.{{_key}}), ToResponse(entity));
                }

            """);
    }

    // ── Sırlar ve token ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Oluşturma ucunun ürettiği, yalnızca o yanıtta gösterilen değerler.</summary>
    private string SecretVariables() =>
        (_resource.Secret is { } secret
            ? $"        var secret = global::Haldus.HaldusSecrets.Generate({ResourceEmitter.Literal(secret.Prefix)});\n"
            : string.Empty)
        + (_resource.Token is { } token
            ? $"        var token = global::Haldus.HaldusSecrets.Generate({ResourceEmitter.Literal(token.Prefix)});\n"
            : string.Empty);

    /// <summary>Sırrın kayda yazılan alanları; sırrın kendisi hiçbir alana yazılmaz.</summary>
    /// <param name="prefix">Atamaların hedefi: başlangıç değerlerinde boş, var olan kayıtta <c>entity.</c>.</param>
    private IEnumerable<string> SecretAssignments(string prefix)
    {
        if (_resource.Secret is not { } secret)
        {
            yield break;
        }

        yield return $"{prefix}SecretHash = global::Haldus.HaldusSecrets.Hash(secret)";
        yield return $"{prefix}SecretHint = global::Haldus.HaldusSecrets.Hint(secret)";

        if (secret.Recoverable)
        {
            yield return $"{prefix}SecretCipher = global::Haldus.HaldusSecretProtection.Protect<{_entity}>(httpContext.RequestServices, secret)";
        }
    }

    /// <summary>Oluşturma yanıtı; sır ve token yalnızca burada (ve döndürmede) görünür.</summary>
    private string CreatedBody()
    {
        var once = new[] { _resource.Secret is null ? null : "Secret = secret", _resource.Token is null ? null : "Token = token" }
            .Where(part => part is not null)
            .ToArray();

        return once.Length == 0 ? "ToResponse(entity)" : $"ToResponse(entity) with {{ {string.Join(", ", once)} }}";
    }

    /// <summary>
    /// Sırrı döndürür: yenisi üretilir ve yalnızca bu yanıtta gösterilir. Örtüşme bildirildiyse eski sır o süre
    /// boyunca geçerli kalır; bildirilmediyse hemen geçersiz olur. Döndürme kaydın içeriğine dokunmaz, bu yüzden
    /// düzenleme durum kuralına bağlı değildir (sızmış bir sır her durumda kapatılabilmelidir); kapanmış bir kayıtta
    /// ise çalışmaz.
    /// </summary>
    private void EmitRotate()
    {
        var secret = _resource.Secret!;

        _map.Append($$"""
                    group.MapPost("{{_keyRoute}}/rotate", RotateAsync)
                        .WithName("Rotate{{_entity}}")
                        .ProducesProblem(404){{StateMetadata(null)}}{{ConcurrencyMetadata()}};


            """);

        var keep = secret.OverlapTicks is { } ticks
            ? $"        entity.PreviousSecretHash = entity.SecretHash;\n        entity.PreviousSecretExpiresAt = {Now} + global::System.TimeSpan.FromTicks({ticks}L);\n"
            : string.Empty;

        _handlers.Append($$"""

                private static async {{OkOrProblem}} RotateAsync(
            {{ScopeParameter}}{{KeyParameter}}{{HttpContextParameter}}{{(secret.OverlapTicks is null ? string.Empty : TimeProviderParameter)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}{{OwnerGuard("rotate")}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ChainClosedGuard()}}{{ExpectVersion()}}        var secret = global::Haldus.HaldusSecrets.Generate({{ResourceEmitter.Literal(secret.Prefix)}});
            {{keep}}{{string.Concat(SecretAssignments("entity.").Select(assignment => $"        {assignment};\n"))}}{{Save()}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity) with { Secret = secret });
                }

            """);
    }

    /// <summary>Kaydı, isteği yapanın kiracısına bakmadan, token'ıyla bulan sorgu.</summary>
    private string TokenQuery => $"contexts.For<{_entity}>().Set<{_entity}>()"
        + (_tenant ? ".IgnoreQueryFilters(new[] { global::Haldus.HaldusFilters.Tenant })" : string.Empty)
        + ".AsNoTracking()";

    /// <summary>
    /// Kaydı token ile okutur. Token'ı taşıyan kişinin oturumu ve kiracısı yoktur; onu kayıt üzerindeki token
    /// tanıtır. Yalnızca kiracı filtresi kaldırılır: silinmiş veya süresi dolmuş bir kayıt token ile de bulunamaz.
    /// </summary>
    private void EmitTokenRead()
    {
        _map.Append($$"""
                    group.MapGet("/by-token", GetByTokenAsync)
                        .WithName("Get{{_entity}}ByToken")
                        .WithMetadata(global::Haldus.HaldusTokenAccess.Instance)
                        .AllowAnonymous()
                        .ProducesProblem(404);


            """);

        _handlers.Append($$"""

                private static async {{OkOrProblem}} GetByTokenAsync(
            {{HttpContextParameter}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var tokenHash = global::Haldus.HaldusTokenAccess.ReadHash(httpContext);
                    var entity = tokenHash is null
                        ? null
                        : await {{TokenQuery}}{{Includes}}
                            .FirstOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);

                    if (entity is null)
                    {
                        return {{Problems}}.TokenInvalid(messages);
                    }

            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """);
    }

    private bool RunsByToken(TransitionModel transition) =>
        _resource.Token is { } token && token.Operations.Contains(transition.Route);

    /// <summary>Token ile çalıştırılabilen bir geçişin ikinci adresi: <c>POST /by-token/{geçiş}</c>.</summary>
    private string TokenTransitionMap(
        TransitionModel transition,
        string method,
        List<(string Type, string Name)> parameters,
        string validation)
    {
        if (!RunsByToken(transition))
        {
            return string.Empty;
        }

        // Kayıt adresten değil token'dan bulunur; kimlik parametresi yoktur.
        var byToken = parameters.Where(parameter => parameter.Name != _keyParameter).ToList();
        var lambdaParameters = string.Join(",\n", byToken.Select(parameter => $"                {parameter.Type} {parameter.Name}"));
        var arguments = string.Join(", ", byToken.Select(parameter => parameter.Name == "httpContext" ? "apply, httpContext" : parameter.Name));

        return $$"""
                    group.MapPost("/by-token/{{transition.Route}}", (
            {{lambdaParameters}}) =>
                            Token{{method}}({{arguments}}))
                        .WithName("{{transition.PascalName}}{{_entity}}ByToken"){{validation}}
                        .WithMetadata(global::Haldus.HaldusTokenAccess.Instance)
                        .AllowAnonymous()
                        .ProducesProblem(404)
                        .ProducesProblem(409)
                        .ProducesProblem(422){{ConcurrencyMetadata()}};


            """;
    }

    /// <summary>
    /// Token'dan kaydı bulur ve asıl geçişi çağırır; böylece geçişin bütün kuralları (durum, sürüm, iş mantığı)
    /// token ile gelene de aynen uygulanır. Kayıt bir kiracıya aitse geçiş o kiracı adına çalışır.
    /// </summary>
    private void EmitTokenTransition(TransitionModel transition, string method, string? request, string apply, string returnType)
    {
        if (!RunsByToken(transition))
        {
            return;
        }

        const string Injection = "global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions";

        var identity = _tenant
            ? $$"""

                    // Token'ı taşıyanın kiracısı yoktur; geçiş, kaydın kiracısı adına çalışır.
                    using var identity = global::Haldus.HaldusAmbient.Use(
                        found.TenantId,
                        {{Injection}}.GetRequiredService<global::Haldus.ICurrentUser>(httpContext.RequestServices).UserId);

            """
            : string.Empty;

        _handlers.Append($$"""

                private static async {{returnType}} Token{{method}}(
            {{(request is null ? string.Empty : $"        {request} request,\n")}}        {{apply}} apply,
            {{HttpContextParameter}}{{(Sliding ? TimeProviderParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var tokenHash = global::Haldus.HaldusTokenAccess.ReadHash(httpContext);
                    var found = tokenHash is null
                        ? null
                        : await {{TokenQuery}}
                            .Where(item => item.TokenHash == tokenHash)
                            .Select(item => new { item.{{_key}}{{(_tenant ? ", item.TenantId" : string.Empty)}} })
                            .FirstOrDefaultAsync(cancellationToken);

                    if (found is null)
                    {
                        return {{Problems}}.TokenInvalid(messages);
                    }
            {{identity}}
                    return await {{method}}(found.{{_key}}, {{(request is null ? string.Empty : "request, ")}}apply, httpContext, {{(Sliding ? "timeProvider, " : string.Empty)}}contexts, messages, cancellationToken);
                }

            """);
    }

    // ── Başka bir kayıttan üretme ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Geçişin doğurduğu kayıtları kurar ve bağlama ekler; geçişle aynı kaydetmede kalıcı olurlar. Kayıt
    /// kurulamazsa geçiş de tamamlanmaz.
    /// </summary>
    private string Spawn(TransitionModel transition)
    {
        const string Injection = "global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions";
        var builder = new StringBuilder();

        foreach (var spawn in _resource.Spawns.Where(spawn => spawn.Transition == transition.Route))
        {
            var prefix = spawn.TargetNamespace.Prefix();
            var created = "created" + spawn.TargetName;
            var time = spawn.NeedsTime
                ? $"{Injection}.GetRequiredService<global::System.TimeProvider>(httpContext.RequestServices), "
                : string.Empty;

            builder.Append(SortRows("entity", _resource.Details.Select(detail => detail.CollectionName)));
            builder.Append($$"""
                        var {{created}} = await {{prefix}}{{spawn.TargetName}}Endpoints.From{{spawn.SourceName}}Async(
                            entity, new {{prefix}}Create{{spawn.TargetName}}From{{spawn.SourceName}}Request(), {{time}}contexts, messages, cancellationToken);

                        if ({{created}}.Problem is not null)
                        {
                            return {{created}}.Problem;
                        }

                        contexts.For<{{spawn.TargetType}}>().Set<{{spawn.TargetType}}>().Add({{created}}.Record!);


                """);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Kaydı başka bir kayıttan üretir: <c>POST /orders/from-cart/{cartId}</c>. Kurma işi ayrı bir metottadır;
    /// kaynağın bir geçişi kaydı doğuruyorsa aynı metodu o çağırır.
    /// </summary>
    private void EmitOrigin(OriginModel origin)
    {
        var request = $"Create{_entity}From{origin.SourceName}Request";
        var parameter = ResourceEmitter.CamelCase(origin.SourceName + origin.SourceKeyName);
        var includes = string.Concat(origin.SourceIncludes.Select(collection => $".Include(item => item.{collection})"));
        var time = HasLifetime ? TimeProviderParameter : string.Empty;
        var qualified = ResourceEmitter.QualifiedName(_resource, _resource.Namespace);

        EmitOriginBuilder(origin, request, time, qualified);

        if (origin.Trigger is not null)
        {
            // Kayıt yalnızca kaynağının geçişiyle doğar. Açık bir uç, geçişin koşullarını (durum, iş kuralı)
            // atlayarak aynı kaydı üretmenin yolu olurdu.
            return;
        }

        _map.Append($$"""
                    group.MapPost("/from-{{Naming.ToKebabCase(origin.SourceName)}}/{{{parameter}}{{origin.SourceRouteConstraint}}}", CreateFrom{{origin.SourceName}}Async)
                        .WithName("Create{{_entity}}From{{origin.SourceName}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{{request}}>>()
                        .ProducesValidationProblem(){{IdempotencyFilter(request)}}{{(Idempotent ? "\n            .ProducesProblem(422)" : string.Empty)}}
                        .ProducesProblem(404){{(_resource.OwnerGuard is null && NaturalKey is null ? string.Empty : "\n            .ProducesProblem(409)")}};


            """);

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Results<{{Results}}.Created<{{_entity}}Response>, {{Problem}}>> CreateFrom{{origin.SourceName}}Async(
            {{ScopeParameter}}        {{origin.SourceKeyType}} {{parameter}},
                    {{request}}? request,
            {{HttpContextParameter}}{{time}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{(_resource.OwnerGuard is null ? ParentCheck() : OwnerGuard("create"))}}        // Gövde isteğe bağlıdır; kaynakta karşılığı olmayan alanları doldurur.
                    request ??= new {{request}}();

                    // Kaynak kendi filtrelerinden geçerek okunur; görünmeyen bir kayıttan kayıt üretilemez.
                    var source = await contexts.For<{{origin.SourceType}}>().Set<{{origin.SourceType}}>().AsNoTracking(){{includes}}
                        .FirstOrDefaultAsync(item => item.{{origin.SourceKeyName}} == {{parameter}}, cancellationToken);

                    if (source is null)
                    {
                        return {{Problems}}.NotFound(messages, "{{origin.SourceName}}", {{parameter}});
                    }

            {{SortRows("source", origin.SourceIncludes)}}        var built = await From{{origin.SourceName}}Async(source, request, {{ScopeArgument}}{{(HasLifetime ? "timeProvider, " : string.Empty)}}contexts, messages, cancellationToken);

                    if (built.Problem is not null)
                    {
                        return built.Problem;
                    }

                    var entity = built.Record!;
                    var context = contexts.For<{{_entity}}>();
            {{SaveCreated()}}

            {{WriteETag()}}        return {{Http}}.TypedResults.Created(global::Haldus.HaldusRoutes.Sibling(httpContext, entity.{{_key}}), ToResponse(entity));
                }

            """);
    }

    private void EmitOriginBuilder(OriginModel origin, string request, string time, string qualified)
    {
        var contract = origin.HasContract
            ? $"        ((global::Haldus.ICreatedFrom<{origin.SourceType}>)entity).CreateFrom(source);\n\n"
            : string.Empty;

        var duplicate = NaturalKey is { } key
            ? NaturalKeyQuery(key, $"entity.{key.PropertyName}", string.Empty, string.Empty)
            : string.Empty;

        // Kaydı kurar ama eklemez ve kaydetmez: aynı adlı alanlar kaynaktan gelir, entity sözleşmeyi uyguluyorsa
        // gerisini o doldurur, en son istekte gönderilen alanlar yazılır. Kontroller oluşturma ucundakilerdir.
        _handlers.Append($$"""

                /// <summary>
                /// Kaydı <c>{{origin.SourceName}}</c> kaydından kurar; eklemek ve kaydetmek çağıranın işidir.
                /// </summary>
                public static async {{Task}}<global::Haldus.HaldusBuilt<{{qualified}}>> From{{origin.SourceName}}Async(
                    {{origin.SourceType}} source,
                    {{request}} request,
            {{ScopeParameter}}{{time}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var entity = new {{_entity}} { {{string.Join(", ", new[] { CreateInitializer(source: "source", only: origin.CopiedProperties, metadata: origin.CopiesMetadata), $"{origin.ForeignKey} = source.{origin.SourceKeyName}" }.Where(part => part.Length > 0))}} };

            {{contract}}{{PatchAssignments(_writable)}}{{(origin.HasContract ? NormalizeRows() : string.Empty)}}{{ReferenceChecks(patch: false, source: "entity")}}{{Compute()}}{{duplicate}}        return entity;
                }

            """);
    }

    // ── Önizleme ve kopya ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bir yazmanın sonucunu, kaydetmeden gösterir. Önizleme, gerçek ucun kontrollerinden ve hesaplamasından
    /// geçer; yalnızca kaydetme ve kaydederken yapılan atamalar (kimlik, numara, zaman damgaları) eksiktir.
    /// </summary>
    private void EmitPreview()
    {
        if (_resource.Has(Operations.Create))
        {
            _map.Append($$"""
                        group.MapPost("/preview", PreviewAsync)
                            .WithName("Preview{{_entity}}")
                            .AddEndpointFilter<global::Haldus.HaldusValidationFilter<Create{{_entity}}Request>>()
                            .ProducesValidationProblem(){{(Parent is null ? string.Empty : "\n            .ProducesProblem(404)")}}{{(_resource.OwnerGuard is null && NaturalKey is null ? string.Empty : "\n            .ProducesProblem(409)")}};


                """);

            _handlers.Append($$"""

                    private static async {{OkOrProblem}} PreviewAsync(
                {{ScopeParameter}}        Create{{_entity}}Request request,
                {{(HasLifetime ? TimeProviderParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                {{(_resource.OwnerGuard is null ? ParentCheck() : OwnerGuard("create"))}}{{WriteChecks(patch: false, creating: true)}}        var entity = new {{_entity}} { {{CreateInitializer(preview: true)}} };
                {{CreateDetails()}}{{Compute()}}
                        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                    }

                """);
        }

        if (_resource.Has(Operations.Update) && !_resource.Archetype.HasClientKey())
        {
            _map.Append($$"""
                        group.MapPost("{{_keyRoute}}/preview", PreviewUpdateAsync)
                            .WithName("Preview{{_entity}}Update")
                            .AddEndpointFilter<global::Haldus.HaldusValidationFilter<Update{{_entity}}Request>>()
                            .ProducesValidationProblem()
                            .ProducesProblem(404){{(_resource.Workflow?.EditableIn is null && _resource.OwnerGuard is null && NaturalKey is null ? string.Empty : "\n            .ProducesProblem(409)")}};


                """);

            _handlers.Append($$"""

                    private static async {{OkOrProblem}} PreviewUpdateAsync(
                {{ScopeParameter}}{{KeyParameter}}        Update{{_entity}}Request request,
                {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                {{OwnerGuard("update")}}        var context = contexts.For<{{_entity}}>();
                        var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                        if (entity is null)
                        {
                            return {{NotFound}};
                        }

                {{ChainGuard()}}{{StateGuard(_resource.Workflow?.EditableIn, "update")}}{{WriteChecks(patch: false, creating: false)}}{{CycleCheck(patch: false)}}{{UpdateAssignments()}}
                {{SyncDetails(patch: false)}}{{Compute()}}{{ApprovalPreview()}}        var preview = ToResponse(entity);

                        // Hiçbir şey kaydedilmez. Bağlamda bekleyen değişiklikler de bırakılır ki isteğin devamında
                        // çalışan bir kod (ör. istek sonunda kaydeden bir ara katman) onları kalıcı yapmasın.
                        context.ChangeTracker.Clear();
                        return {{Http}}.TypedResults.Ok(preview);
                    }

                """);
        }
    }

    /// <summary>
    /// Kaydın kopyasını oluşturur. Yazılabilir alanlar ve gömülü satırlar kaynaktan gelir, istekte gönderilen
    /// alanlar onların yerine geçer; geri kalan her şey yeni bir kayıtta olduğu gibi atanır.
    /// </summary>
    private void EmitClone()
    {
        var request = $"Clone{_entity}Request";

        _map.Append($$"""
                    group.MapPost("{{_keyRoute}}/clone", CloneAsync)
                        .WithName("Clone{{_entity}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{{request}}>>()
                        .ProducesValidationProblem(){{IdempotencyFilter(request)}}{{(Idempotent ? "\n            .ProducesProblem(422)" : string.Empty)}}
                        .ProducesProblem(404){{(_resource.OwnerGuard is null && NaturalKey is null ? string.Empty : "\n            .ProducesProblem(409)")}};


            """);

        // Kopyanın doğal anahtarı, istek onu değiştirmediyse kaynağınkiyle aynıdır ve çakışır; bu, istemciye yeni
        // bir değer vermesi gerektiğini söyler.
        var duplicate = NaturalKey is { } key
            ? NaturalKeyQuery(key, $"entity.{key.PropertyName}", string.Empty, string.Empty)
            : string.Empty;

        var rows = CopyRows("source");

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Results<{{Results}}.Created<{{_entity}}Response>, {{Problem}}>> CloneAsync(
            {{ScopeParameter}}{{KeyParameter}}        {{request}}? request,
            {{HttpContextParameter}}{{(HasLifetime ? TimeProviderParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{OwnerGuard("clone")}}        // Gövde isteğe bağlıdır; gönderilmediyse hiçbir alan değişmez.
                    request ??= new {{request}}();

                    var context = contexts.For<{{_entity}}>();
                    var source = await context.Set<{{_entity}}>().AsNoTracking(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (source is null)
                    {
                        return {{NotFound}};
                    }

                    var entity = new {{_entity}} { {{CreateInitializer(source: "source")}} };

            {{PatchAssignments(_writable)}}{{ReferenceChecks(patch: false, source: "entity")}}{{rows}}{{Compute()}}{{duplicate}}{{SaveCreated()}}

            {{WriteETag()}}        return {{Http}}.TypedResults.Created(global::Haldus.HaldusRoutes.Sibling(httpContext, entity.{{_key}}), ToResponse(entity));
                }

            """);
    }

    /// <summary>Kaydetme doğal anahtarın indeksine takılırsa, hatayı 500 yerine çakışma olarak bildirir.</summary>
    private string DuplicateCatch() => NaturalKey is not { } key
        ? string.Empty
        : $$"""
                    catch (global::Microsoft.EntityFrameworkCore.DbUpdateException duplicate)
                        when (global::Haldus.HaldusDb.IsUniqueViolation(duplicate, "{{ResourceEmitter.NaturalKeyIndex(_resource)}}"))
                    {
                        return {{Problems}}.Duplicate(messages, "{{_entity}}", "{{ResourceEmitter.CamelCase(key.PropertyName)}}", entity.{{key.PropertyName}});
                    }

            """;

    /// <summary>Yeni bir kaydı kaydeder.</summary>
    private string SaveNew(string catches = "") => NaturalKey is null && catches.Length == 0
        ? "        await context.SaveChangesAsync(cancellationToken);\n"
        : $$"""
                    try
                    {
                        await context.SaveChangesAsync(cancellationToken);
                    }
            {{catches}}{{DuplicateCatch()}}
            """;

    /// <summary>Hazır bir kod parçasını daha içerideki bir bloğa yerleştirmek için her satırını girintiler.</summary>
    private static string Indent(string code, int spaces)
    {
        var padding = new string(' ', spaces);
        return string.Join("\n", code.Split('\n').Select(line => line.Length == 0 ? line : padding + line));
    }

    private string ConcurrencyMetadata() => _concurrency ? "\n            .ProducesProblem(412)\n            .ProducesProblem(428)" : string.Empty;

    private string ReadIfMatch() => _concurrency
        ? $$"""
                    var ifMatch = global::Haldus.HaldusETag.ReadIfMatch(httpContext);
                    if (ifMatch.Kind == global::Haldus.IfMatchKind.Missing)
                    {
                        return {{Problems}}.PreconditionRequired(messages);
                    }

                    if (ifMatch.Kind == global::Haldus.IfMatchKind.Invalid)
                    {
                        return {{Problems}}.PreconditionFailed(messages);
                    }


            """
        : string.Empty;

    /// <summary>İstemcinin okuduğu sürümü özgün değer yapar; kayıt o zamandan beri değiştiyse kaydetme başarısız olur.</summary>
    private string ExpectVersion() => _concurrency
        ? """
                    if (ifMatch.Version is { } expectedVersion)
                    {
                        context.Entry(entity).Property(item => item.Version).OriginalValue = expectedVersion;
                    }


            """
        : string.Empty;

    /// <summary>
    /// Durum makinesi olan ama sürüm kontrolü olmayan kaynakta durum, eşzamanlılık belirtecidir: kayıt
    /// okunduktan sonra başka bir istek durumunu değiştirdiyse kaydetme başarısız olur.
    /// </summary>
    private bool GuardsState => (_resource.Workflow is not null || _resource.Approval is not null) && !_concurrency;

    private string Save()
    {
        var concurrency = _concurrency || GuardsState
            ? $$"""
                        catch (global::Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
                        {
                            return {{Problems}}.{{(_concurrency ? "PreconditionFailed" : "StateChanged")}}(messages);
                        }

                """
            : string.Empty;

        var duplicate = DuplicateCatch();

        return Effective(concurrency.Length == 0 && duplicate.Length == 0
            ? "        await context.SaveChangesAsync(cancellationToken);\n"
            : $$"""
                        try
                        {
                            await context.SaveChangesAsync(cancellationToken);
                        }
                {{concurrency}}{{duplicate}}
                """);
    }

    private string WriteETag() => _concurrency
        ? "        global::Haldus.HaldusETag.Write(httpContext, entity.Version);\n"
        : string.Empty;
}
