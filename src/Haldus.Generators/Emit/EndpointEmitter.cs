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
    private bool CreateCanFail => ChecksReferences || NaturalKey is not null;

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

        if (_resource.Batch is not null)
        {
            EmitBatch();
        }

        foreach (var transition in _resource.Transitions)
        {
            EmitTransition(transition);
        }

        if (_resource.Has(Operations.Update) || _resource.Has(Operations.Patch))
        {
            EmitCycleHelper();
            EmitSyncHelpers();
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

        // Durum makinesi olan kaynak duruma göre süzülebilir; kullanıcının filtresi aynı adı taşıyorsa o geçerlidir.
        var status = _resource.Filter?.Properties.Any(property => property.Name == "Status") != true
            ? _resource.StatusType
            : null;
        var statusParameter = status is null ? string.Empty : $"        {status}? status,\n";

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

        if (status is not null)
        {
            _handlers.Append("""

                        if (status is { } statusValue)
                        {
                            query = query.Where(item => item.Status == statusValue);
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

        var source = sortable ? "sorted" : $"query\n            .OrderBy(item => item.{_key})";

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

                    // Sayfalama kararlı olsun diye sıralama her zaman anahtarla tamamlanır.
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

        _handlers.Append($$"""

                private static async {{OkOrProblem}} GetBy{{key.PropertyName}}Async(
            {{ScopeParameter}}        {{key.Type}} {{parameter}},
            {{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var entity = await contexts.For<{{_entity}}>().Set<{{_entity}}>()
                        .AsNoTracking(){{Includes}}
                        .FirstOrDefaultAsync(item => item.{{key.PropertyName}} == {{parameter}}{{ScopeCondition("item")}}, cancellationToken);

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
    private string CreateInitializer()
    {
        var keys = new List<string>();
        if (_resource.KeyGeneration == KeyGeneration.Prefixed)
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

        if (HasMetadata)
        {
            keys.Add("Metadata = global::Haldus.HaldusMetadata.Create(request.Metadata)");
        }

        return string.Join(
            ", ", keys.Concat(_writable.Select(property => $"{property.Name} = request.{property.Name}")));
    }

    private void EmitCreate()
    {
        var assignments = CreateInitializer();

        // Numara oluşturulurken atanıyorsa, kayıtla aynı transaction'da ayrılır ve onunla birlikte kalıcı olur.
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

        if (NaturalKey is not null)
        {
            // Ön kontrolü geçen iki eşzamanlı istekten ikincisi indekse takılır; o da çakışma olarak bildirilir.
            save = save.Replace("        await context.SaveChangesAsync(cancellationToken);", SaveNew().TrimEnd('\n'));
        }

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
            {{(_resource.OwnerGuard is null ? ParentCheck() : OwnerGuard("create"))}}{{WriteChecks(patch: false, creating: true)}}        var entity = new {{_entity}} { {{assignments}} };
            {{CreateDetails()}}{{Compute()}}
                    var context = contexts.For<{{_entity}}>();
            {{save}}

            {{WriteETag()}}        var location = $"{httpContext.Request.PathBase}{httpContext.Request.Path.Value?.TrimEnd('/')}/{entity.{{_key}}}";
                    return {{Http}}.TypedResults.{{result}}(location, ToResponse(entity));
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

            {{StateGuard(_resource.Workflow?.EditableIn, "update")}}{{WriteChecks(patch: operation == "Patch", creating: false)}}{{CycleCheck(patch: operation == "Patch")}}{{ExpectVersion()}}{{assignments}}
            {{SyncDetails(patch: operation == "Patch")}}{{Slide()}}{{Compute()}}{{Save()}}
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

            {{StateGuard(_resource.Workflow?.DeletableIn, "delete")}}{{ExpectVersion()}}        context.Set<{{_entity}}>().Remove(entity);
            {{SaveDelete()}}
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

            {{ExpectVersion()}}        entity.DeletedAt = null;
            {{Save()}}
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

            _handlers.Append($$"""

                    private static async {{Task}}<{{Results}}.Ok<{{_entity}}Response>> GetAsync(
                {{ContextsParameter}}{{CancellationParameter}}    {
                        var entity = await contexts.For<{{_entity}}>().Set<{{_entity}}>()
                            .AsNoTracking()
                            .FirstOrDefaultAsync(cancellationToken);

                        return {{Http}}.TypedResults.Ok(ToResponse(entity ?? new {{_entity}}()));
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
            : "context.Entry(entity).Property(global::Haldus.HaldusModel.SingletonKey).CurrentValue = 1;";

        _map.Append($$"""
                    group.{{mapMethod}}("/", {{operation}}Async)
                        .WithName("{{operation}}{{_entity}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{{operation}}{{_entity}}Request>>()
                        .ProducesValidationProblem();


            """);

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Ok<{{_entity}}Response>> {{operation}}Async(
                    {{operation}}{{_entity}}Request request,
            {{(_tenant ? TenantsParameter : string.Empty)}}{{ContextsParameter}}{{CancellationParameter}}    {
                    var context = contexts.For<{{_entity}}>();

                    for (var attempt = 0; ; attempt++)
                    {
                        var entity = await context.Set<{{_entity}}>().FirstOrDefaultAsync(cancellationToken);
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
            {{requireHandler}}        return group.MapPost("{{_keyRoute}}/{{transition.Route}}", (
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

            {{StateGuard(transition.IsAction ? null : transition.From, transition.Route)}}{{ExpectVersion()}}{{declareResult}}        try
                    {
            {{logic}}        }
                    catch (global::Haldus.TransitionRejectedException rejection)
                    {
                        return {{Problems}}.TransitionRejected(messages, rejection.Message);
                    }

            {{setStatus}}{{Slide()}}{{Compute()}}{{allocate}}{{Save()}}{{commit}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok({{(response is null ? "ToResponse(entity)" : "result")}});
                }

            """);
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

        foreach (var detail in _resource.Details)
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
        foreach (var detail in _resource.Details)
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
    private string ReferenceChecks(bool patch, bool creating = true)
    {
        var builder = new StringBuilder();

        foreach (var reference in _resource.References.Where(reference => reference.Kind != ReferenceKind.Parent))
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
                value = $"request.{property}";
                condition = string.Empty;
            }
            else
            {
                value = $"{field}Reference";
                condition = $"request.{property} is {{ }} {value}\n            && ";
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

    private string PatchAssignments()
    {
        var builder = new StringBuilder();

        foreach (var property in _updatable)
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
        var isSet = patch ? $"((global::Haldus.IHaldusPatch)request).IsSet(\"{key.PropertyName}\")\n            && " : string.Empty;
        var self = creating ? string.Empty : $" && item.{_key} != {_keyParameter}";

        return $$"""
                    if ({{isSet}}await contexts.For<{{_entity}}>().Set<{{_entity}}>(){{ignore}}
                        .AnyAsync(item => item.{{key.PropertyName}} == request.{{key.PropertyName}}{{self}}{{ScopeCondition("item")}}, cancellationToken))
                    {
                        return {{Problems}}.Duplicate(messages, "{{_entity}}", "{{ResourceEmitter.CamelCase(key.PropertyName)}}", request.{{key.PropertyName}});
                    }


            """;
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
    private string SaveNew() => NaturalKey is null
        ? "        await context.SaveChangesAsync(cancellationToken);\n"
        : $$"""
                    try
                    {
                        await context.SaveChangesAsync(cancellationToken);
                    }
            {{DuplicateCatch()}}
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
    private bool GuardsState => _resource.Workflow is not null && !_concurrency;

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

        return concurrency.Length == 0 && duplicate.Length == 0
            ? "        await context.SaveChangesAsync(cancellationToken);\n"
            : $$"""
                        try
                        {
                            await context.SaveChangesAsync(cancellationToken);
                        }
                {{concurrency}}{{duplicate}}
                """;
    }

    private string WriteETag() => _concurrency
        ? "        global::Haldus.HaldusETag.Write(httpContext, entity.Version);\n"
        : string.Empty;
}
