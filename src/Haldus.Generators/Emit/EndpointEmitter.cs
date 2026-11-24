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
            .Concat(TraitCatalog.ResponseMembers(_resource.Traits).Select(member => $"{member.Name} = item.{member.Name}")));

    private string NotFound => $"{Problems}.NotFound(messages, \"{_entity}\", {_keyParameter})";

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
                public static global::Microsoft.AspNetCore.Routing.RouteGroupBuilder Map(
                    global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
                {
                    var group = endpoints.MapGroup("/{{route}}").WithTags("{{(_resource.Archetype == Archetype.Singleton ? _entity : _plural)}}");

            {{tenantFilter}}{{_map}}        return group;
                }
            {{_handlers}}
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
    }

    private void EmitList()
    {
        var softDelete = _resource.Has(Traits.SoftDeletable);
        var archive = _resource.Has(Traits.Archivable);

        _map.Append($$"""
                    group.MapGet("/", ListAsync)
                        .WithName("List{{_plural}}");


            """);

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Ok<global::Haldus.PagedResult<{{_entity}}Response>>> ListAsync(
                    [{{Http}}.AsParameters] global::Haldus.PageRequest page,
            {{(softDelete ? "        bool? includeDeleted,\n" : string.Empty)}}{{(archive ? "        bool? includeArchived,\n" : string.Empty)}}{{FilterParameters()}}{{ContextsParameter}}{{CancellationParameter}}    {
                    var query = contexts.For<{{_entity}}>().Set<{{_entity}}>().AsNoTracking();

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
                    var query = contexts.For<{{_entity}}>().Set<{{_entity}}>().AsNoTracking();

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
            {{KeyParameter}}{{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var entity = await contexts.For<{{_entity}}>().Set<{{_entity}}>()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(item => item.{{_key}} == {{_keyParameter}}, cancellationToken);

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
        var assignments = string.Join(
            ", ",
            (_resource.IdKind == IdKind.Guid ? [$"{_key} = global::System.Guid.CreateVersion7()"] : Array.Empty<string>())
                .Concat(_writable.Select(property => $"{property.Name} = request.{property.Name}")));

        _map.Append($$"""
                    group.MapPost("/", CreateAsync)
                        .WithName("Create{{_entity}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<Create{{_entity}}Request>>()
                        .ProducesValidationProblem();


            """);

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Created<{{_entity}}Response>> CreateAsync(
                    Create{{_entity}}Request request,
            {{HttpContextParameter}}{{ContextsParameter}}{{CancellationParameter}}    {
                    var entity = new {{_entity}} { {{assignments}} };

                    var context = contexts.For<{{_entity}}>();
                    context.Set<{{_entity}}>().Add(entity);
                    await context.SaveChangesAsync(cancellationToken);

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
                        .ProducesProblem(404){{ConcurrencyMetadata()}};


            """);

        _handlers.Append($$"""

                private static async {{OkOrProblem}} {{operation}}Async(
            {{KeyParameter}}        {{operation}}{{_entity}}Request request,
            {{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>().FirstOrDefaultAsync(item => item.{{_key}} == {{_keyParameter}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ExpectVersion()}}{{assignments}}
            {{Save()}}
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

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Results<{{Results}}.Ok<{{_entity}}Response>, {{Results}}.Created<{{_entity}}Response>>> PutAsync(
            {{KeyParameter}}        Put{{_entity}}Request request,
            {{HttpContextParameter}}{{(_tenant ? TenantsParameter : string.Empty)}}{{ContextsParameter}}{{CancellationParameter}}    {
                    var context = contexts.For<{{_entity}}>();

                    for (var attempt = 0; ; attempt++)
                    {
                        var entity = await context.Set<{{_entity}}>().FirstOrDefaultAsync(item => item.{{_key}} == {{_keyParameter}}, cancellationToken);

                        if (entity is not null)
                        {
            {{Indent(UpdateAssignments(), 8)}}                await context.SaveChangesAsync(cancellationToken);
                            return {{Http}}.TypedResults.Ok(ToResponse(entity));
                        }

                        entity = new {{_entity}} { {{creation}} };
                        context.Set<{{_entity}}>().Add(entity);

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
                        .ProducesProblem(404){{ConcurrencyMetadata()}};


            """);

        _handlers.Append($$"""

                private static async {{NoContentOrProblem}} DeleteAsync(
            {{KeyParameter}}{{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>().FirstOrDefaultAsync(item => item.{{_key}} == {{_keyParameter}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ExpectVersion()}}        context.Set<{{_entity}}>().Remove(entity);
            {{Save()}}
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
            {{KeyParameter}}{{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>()
                        .IgnoreQueryFilters(new[] { global::Haldus.HaldusFilters.SoftDelete })
                        .FirstOrDefaultAsync(item => item.{{_key}} == {{_keyParameter}} && item.DeletedAt != null, cancellationToken);

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
            {{KeyParameter}}{{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}        var query = contexts.For<{{_entity}}>().Set<{{_entity}}>()
                        .IgnoreQueryFilters(new[] { global::Haldus.HaldusFilters.SoftDelete })
                        .Where(item => item.{{_key}} == {{_keyParameter}} && item.DeletedAt != null);

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
            {{KeyParameter}}        global::System.TimeProvider timeProvider,
            {{(_concurrency ? HttpContextParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>().FirstOrDefaultAsync(item => item.{{_key}} == {{_keyParameter}}, cancellationToken);

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

            {{Indent(assignments, 4).TrimEnd('\n')}}

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
                    {{_entity}}SearchRequest request,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var budget = new global::Haldus.SearchBudget();
                    budget.CountSortFields(request.Sort?.Count ?? 0);
                    var predicate = BuildPredicate(request.Where, budget);

                    if (budget.Exceeded)
                    {
                        return {{Problems}}.SearchTooComplex(messages);
                    }

                    var query = contexts.For<{{_entity}}>().Set<{{_entity}}>().AsNoTracking();

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
