using System.Text;
using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;

namespace Haldus.Generators.Emit;

/// <summary>Bir kaynağın Minimal API endpoint'lerini archetype'a, seçilen işlemlere ve trait'lere göre üretir.</summary>
internal sealed class EndpointEmitter
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
    }

    private string OkOrProblem => $"{Task}<{Results}.Results<{Results}.Ok<{_entity}Response>, {Problem}>>";

    private string NoContentOrProblem => $"{Task}<{Results}.Results<{Results}.NoContent, {Problem}>>";

    private string KeyParameter => $"        {_resource.IdType} {_keyParameter},\n";

    private string ResponseAssignments => string.Join(
        ", ",
        (_key.Length == 0 ? Array.Empty<string>() : [$"{_key} = item.{_key}"])
            .Concat(_resource.Properties.Select(property => $"{property.Name} = item.{property.Name}"))
            .Concat(_resource.ResponseMembers.Select(member => $"{member.Name} = item.{member.Name}"))
            .Concat(_resource.Details.Select(DetailProjection)));

    /// <summary>Tek bir kayıt yüklenirken gömülü detayları da getirir; yanıt ve eşitleme onlara ihtiyaç duyar.</summary>
    private string Includes => string.Concat(_resource.Details.Select(detail => $".Include(item => item.{detail.CollectionName})"));

    /// <summary>
    /// Gömülü detayların yanıta izdüşümü. Aynı ifade hem veritabanı sorgusunda hem bellekteki kayıtta
    /// çalışır; satırlar istekte gönderildikleri sırayla döner.
    /// </summary>
    private static string DetailProjection(ResourceModel detail)
    {
        var assignments = new[] { $"{detail.KeyName} = detail.{detail.KeyName}" }
            .Concat(detail.Properties.Select(property => $"{property.Name} = detail.{property.Name}"));

        return $"{detail.CollectionName} = item.{detail.CollectionName}"
            + $".OrderBy(detail => detail.Position).ThenBy(detail => detail.{detail.KeyName})"
            + $".Select(detail => new {detail.QualifiedName}Response {{ {string.Join(", ", assignments)} }}).ToList()";
    }

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

        var tenantFilter = _tenant
            ? "        group.AddEndpointFilter<global::Haldus.HaldusTenantRequiredFilter>();\n\n"
            : string.Empty;

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

        _map.Append($$"""
                    group.MapGet("/", ListAsync)
                        .WithName("List{{_plural}}");


            """);

        var self = _resource.SelfReference;
        var treeParameters = self is null
            ? string.Empty
            : $"        {self.TargetKeyType}? {ResourceEmitter.CamelCase(self.PropertyName)},\n        bool? roots,\n";

        // Durum makinesi olan kaynak duruma göre süzülebilir; kullanıcının filtresi aynı adı taşıyorsa o geçerlidir.
        var status = _resource.Workflow is { } workflow && _resource.Filter?.Properties.Any(property => property.Name == "Status") != true
            ? workflow.StatusType
            : null;
        var statusParameter = status is null ? string.Empty : $"        {status}? status,\n";

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Ok<global::Haldus.PagedResult<{{_entity}}Response>>> ListAsync(
            {{ScopeParameter}}        [{{Http}}.AsParameters] global::Haldus.PageRequest page,
            {{(softDelete ? "        bool? includeDeleted,\n" : string.Empty)}}{{(archive ? "        bool? includeArchived,\n" : string.Empty)}}{{treeParameters}}{{statusParameter}}{{FilterParameters()}}{{ContextsParameter}}{{CancellationParameter}}    {
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

        _handlers.Append($$"""

                    var totalCount = await query.LongCountAsync(cancellationToken);
                    var items = await query
                        .OrderBy(item => item.{{_key}})
                        .Skip(page.Skip)
                        .Take(page.PageSizeOrDefault)
                        .Select(item => new {{_entity}}Response { {{ResponseAssignments}} })
                        .ToListAsync(cancellationToken);

                    return {{Http}}.TypedResults.Ok(
                        new global::Haldus.PagedResult<{{_entity}}Response>(items, page.PageOrDefault, page.PageSizeOrDefault, totalCount));
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

    private void EmitCreate()
    {
        var keys = new List<string>();
        if (_resource.IdKind == IdKind.Guid)
        {
            keys.Add($"{_key} = global::System.Guid.CreateVersion7()");
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

        var assignments = string.Join(
            ", ", keys.Concat(_writable.Select(property => $"{property.Name} = request.{property.Name}")));

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

        // Referans veya ebeveyn bulunamazsa oluşturma reddedilir; dönüş tipi buna göre genişler.
        var returnType = ChecksReferences
            ? $"{Task}<{Results}.Results<{Results}.Created<{_entity}Response>, {Problem}>>"
            : $"{Task}<{Results}.Created<{_entity}Response>>";

        _map.Append($$"""
                    group.MapPost("/", CreateAsync)
                        .WithName("Create{{_entity}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<Create{{_entity}}Request>>()
                        .ProducesValidationProblem(){{IdempotencyFilter($"Create{_entity}Request")}}{{(Idempotent ? "\n            .ProducesProblem(422)" : string.Empty)}}{{(Parent is null ? string.Empty : "\n            .ProducesProblem(404)")}}{{(_resource.OwnerGuard is null ? string.Empty : "\n            .ProducesProblem(409)")}};


            """);

        _handlers.Append($$"""

                private static async {{returnType}} CreateAsync(
            {{ScopeParameter}}        Create{{_entity}}Request request,
            {{HttpContextParameter}}{{(HasLifetime ? TimeProviderParameter : string.Empty)}}{{ContextsParameter}}{{(ChecksReferences ? MessagesParameter : string.Empty)}}{{CancellationParameter}}    {
            {{(_resource.OwnerGuard is null ? ParentCheck() : OwnerGuard("create"))}}{{ReferenceChecks(patch: false)}}        var entity = new {{_entity}} { {{assignments}} };
            {{CreateDetails()}}{{Compute()}}
                    var context = contexts.For<{{_entity}}>();
            {{save}}

            {{WriteETag()}}        var location = $"{httpContext.Request.PathBase}{httpContext.Request.Path.Value?.TrimEnd('/')}/{entity.{{_key}}}";
                    return {{Http}}.TypedResults.Created(location, ToResponse(entity));
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

            {{StateGuard(_resource.Workflow?.EditableIn, "update")}}{{ReferenceChecks(patch: operation == "Patch")}}{{CycleCheck(patch: operation == "Patch")}}{{ExpectVersion()}}{{assignments}}
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

        if (_resource.IsReferenced)
        {
            // ExecuteDelete veritabanının reddini sarmalamadan fırlatır; o da çakışma olarak bildirilir.
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
        }

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
    /// Bir geçişin ucunu üretir. Uç, <c>MapTransition</c> çağrısı çalıştığında bağlanır; çağrıya verilen
    /// lambda (varsa) eşleyiciye parametre olarak gelir. Durum kontrolü, sürüm kontrolü ve kaydetme burada
    /// üretilir; iş mantığı istek tipinden, lambda'dan veya bir handler sınıfından gelir.
    /// </summary>
    private void EmitTransition(TransitionModel transition)
    {
        const string Services = "global::System.IServiceProvider";
        const string Cancellation = "global::System.Threading.CancellationToken";

        var workflow = _resource.Workflow!;
        var request = transition.RequestType;
        var method = $"Transition{transition.PascalName}Async";
        var apply = request is null
            ? $"global::System.Func<{_entity}, {Services}, {Cancellation}, {Task}>?"
            : $"global::System.Func<{_entity}, {request}, {Services}, {Cancellation}, {Task}>?";

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
                        global::Haldus.HaldusTransitionRegistry.RequireHandler<{{_entity}}, {{request}}>(group);
                    }


            """
            : string.Empty;

        var validation = request is null
            ? string.Empty
            : $"\n            .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{request}>>()\n            .ProducesValidationProblem()";

        _transitionMaps.Append($$"""

                public static global::Microsoft.AspNetCore.Builder.RouteHandlerBuilder Map{{transition.PascalName}}Transition(
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

        if (transition.ImplementsTransition)
        {
            logic.Append($"            ((global::Haldus.ITransition<{_entity}>)request).Apply(entity);\n\n");
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
                            else if (global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                                .GetService<global::Haldus.ITransitionHandler<{{_entity}}, {{request}}>>(httpContext.RequestServices) is { } handler)
                            {
                                await handler.HandleAsync(entity, request, cancellationToken);
                            }

                """);
        }

        // Numara bu duruma geçerken atanıyorsa, kayıtla aynı transaction'da ayrılır; daha önce atanmışsa korunur.
        var numbered = _resource.Numbering is { AssignOn: { } assignOn } && assignOn == transition.To;
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

        _handlers.Append($$"""

                private static async {{OkOrProblem}} {{method}}(
            {{ScopeParameter}}{{KeyParameter}}{{(request is null ? string.Empty : $"        {request} request,\n")}}        {{apply}} apply,
            {{HttpContextParameter}}{{(Sliding ? TimeProviderParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{StateGuard(transition.From, transition.Route)}}{{ExpectVersion()}}        try
                    {
            {{logic}}        }
                    catch (global::Haldus.TransitionRejectedException rejection)
                    {
                        return {{Problems}}.TransitionRejected(messages, rejection.Message);
                    }

                    entity.Status = {{workflow.StatusType}}.{{transition.To}};
            {{Slide()}}{{Compute()}}{{allocate}}{{Save()}}{{commit}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity));
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
    private string ReferenceChecks(bool patch)
    {
        var builder = new StringBuilder();

        foreach (var reference in _resource.References.Where(reference => reference.Kind != ReferenceKind.Parent))
        {
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
        if (_resource.SelfReference is not { } self)
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
    /// bildirilir. Silme geri alınabilirse kayıt fiziksel olarak kalkmadığı için bu durum oluşmaz.
    /// </summary>
    private string SaveDelete()
    {
        if (!_resource.IsReferenced || _resource.Has(Traits.SoftDeletable))
        {
            return Save();
        }

        var concurrency = _concurrency
            ? $$"""
                    catch (global::Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
                    {
                        return {{Problems}}.PreconditionFailed(messages);
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

    private string UpdateAssignments() => string.Concat(
        _writable.Select(property => $"        entity.{property.Name} = request.{property.Name};\n"));

    private string PatchAssignments()
    {
        var builder = new StringBuilder();

        foreach (var property in _writable)
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

        return builder.ToString();
    }

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

    private string Save() => _concurrency
        ? $$"""
                    try
                    {
                        await context.SaveChangesAsync(cancellationToken);
                    }
                    catch (global::Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
                    {
                        return {{Problems}}.PreconditionFailed(messages);
                    }

            """
        : "        await context.SaveChangesAsync(cancellationToken);\n";

    private string WriteETag() => _concurrency
        ? "        global::Haldus.HaldusETag.Write(httpContext, entity.Version);\n"
        : string.Empty;
}
