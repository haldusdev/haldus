using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;

namespace Haldus.Generators.Emit;

/// <summary>
/// Geçerli kullanıcı ile bir kaynak arasındaki bağın (favori, yıldız, oy) entity'sini ve endpoint'lerini
/// üretir. Bağın anahtarı kullanıcı ile karşı ucun anahtarının çiftidir; uçlar <c>/me</c> altındadır ve
/// adreste kullanıcı yer almaz, bu yüzden kimse başkasının bağına ulaşamaz.
/// </summary>
internal static class UserLinkEmitter
{
    private const string Task = "global::System.Threading.Tasks.Task";
    private const string Http = "global::Microsoft.AspNetCore.Http";
    private const string Results = "global::Microsoft.AspNetCore.Http.HttpResults";
    private const string Problems = "global::Haldus.HaldusProblems";

    public static string EmitEntity(UserLinkModel link) =>
        $$"""
        {{ResourceEmitter.Header}}
        {{ResourceEmitter.NamespaceLine(link.Namespace)}}
        {{(link.Declared ? string.Empty : "public ")}}partial class {{link.LinkName}} : global::Haldus.IOwned
        {
            public string OwnerId { get; set; } = "";

            public {{link.Right.IdType}} {{link.RightKey}} { get; set; }{{(link.Right.IdKind == IdKind.String ? " = \"\";" : string.Empty)}}
        }

        """;

    public static string EmitEndpoints(UserLinkModel link)
    {
        var right = link.Right;
        var name = link.LinkName;
        var entity = link.QualifiedLinkName;
        var rightKey = link.RightKey;
        var parameter = ResourceEmitter.CamelCase(rightKey);
        var item = $"/{{{parameter}{ResourceEmitter.RouteConstraint(right)}}}";
        var noContentOrProblem = $"{Task}<{Results}.Results<{Results}.NoContent, {Results}.ProblemHttpResult>>";

        // Karşı uç kiracıya bağlıysa kiracısı çözülemeyen istek hiçbir kayda ulaşamaz; nedenini söyleyen yanıt döner.
        var tenantFilter = right.Has(Traits.MultiTenant)
            ? "        group.AddEndpointFilter<global::Haldus.HaldusTenantRequiredFilter>();\n"
            : string.Empty;

        return $$"""
            {{ResourceEmitter.Header}}
            using System.Linq;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Routing;
            using Microsoft.EntityFrameworkCore;

            {{ResourceEmitter.NamespaceLine(link.Namespace)}}
            public static partial class {{name}}Endpoints
            {
                public static global::Microsoft.AspNetCore.Routing.RouteGroupBuilder Map(
                    global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
                {
                    var group = endpoints.MapGroup("/{{link.Route}}").WithTags("{{link.CollectionName}}");

            {{tenantFilter}}        group.AddEndpointFilter<global::Haldus.HaldusUserRequiredFilter>();

                    group.MapGet("/", ListAsync)
                        .WithName("List{{link.CollectionName}}");

                    group.MapGet("{{item}}", ExistsAsync)
                        .WithName("Get{{name}}")
                        .ProducesProblem(404);

                    group.MapPut("{{item}}", LinkAsync)
                        .WithName("Put{{name}}")
                        .ProducesProblem(404);

                    group.MapDelete("{{item}}", UnlinkAsync)
                        .WithName("Delete{{name}}")
                        .ProducesProblem(404);

                    return group;
                }

                /// <summary>Geçerli kullanıcının bağlı olduğu kayıtlar. Bağlar sorgu filtresiyle kullanıcıya süzülür.</summary>
                private static async {{Task}}<{{Results}}.Ok<global::Haldus.PagedResult<{{right.QualifiedName}}Response>>> ListAsync(
                    [{{Http}}.AsParameters] global::Haldus.PageRequest page,
                    global::Haldus.IHaldusDbContextProvider contexts,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    var links = contexts.For<{{entity}}>().Set<{{entity}}>();
                    var query = contexts.For<{{right.QualifiedName}}>().Set<{{right.QualifiedName}}>()
                        .AsNoTracking()
                        .Where(item => links.Any(link => link.{{rightKey}} == item.{{right.KeyName}}));

                    var totalCount = await query.LongCountAsync(cancellationToken);
                    var items = await query
                        .OrderBy(item => item.{{right.KeyName}})
                        .Skip(page.Skip)
                        .Take(page.PageSizeOrDefault)
                        .Select(item => new {{right.QualifiedName}}Response { {{ResourceEmitter.ResponseAssignments(right)}} })
                        .ToListAsync(cancellationToken);

                    return {{Http}}.TypedResults.Ok(
                        new global::Haldus.PagedResult<{{right.QualifiedName}}Response>(items, page.PageOrDefault, page.PageSizeOrDefault, totalCount));
                }

                private static async {{noContentOrProblem}} ExistsAsync(
                    {{right.IdType}} {{parameter}},
                    global::Haldus.IHaldusDbContextProvider contexts,
                    global::Haldus.IHaldusMessages messages,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    if (!await contexts.For<{{entity}}>().Set<{{entity}}>().AnyAsync(link => link.{{rightKey}} == {{parameter}}, cancellationToken))
                    {
                        return {{Problems}}.NotFound(messages, "{{name}}", {{parameter}});
                    }

                    return {{Http}}.TypedResults.NoContent();
                }

                /// <summary>Bağı kurar. Bağ zaten varsa hiçbir şey değişmez; istek yinelenebilir.</summary>
                private static async {{noContentOrProblem}} LinkAsync(
                    {{right.IdType}} {{parameter}},
                    global::Haldus.IHaldusDbContextProvider contexts,
                    global::Haldus.IHaldusMessages messages,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    // Karşı uç kendi filtrelerinden geçerek aranır; görünmeyen bir kayda bağ kurulamaz.
                    if (!await contexts.For<{{right.QualifiedName}}>().Set<{{right.QualifiedName}}>()
                        .AnyAsync(item => item.{{right.KeyName}} == {{parameter}}, cancellationToken))
                    {
                        return {{Problems}}.NotFound(messages, "{{right.EntityName}}", {{parameter}});
                    }

                    var context = contexts.For<{{entity}}>();
                    if (await context.Set<{{entity}}>().AnyAsync(link => link.{{rightKey}} == {{parameter}}, cancellationToken))
                    {
                        return {{Http}}.TypedResults.NoContent();
                    }

                    // Sahip anahtarın parçasıdır; kayıt izlenmeye başlamadan önce atanır. Kullanıcının varlığını
                    // grubun filtresi güvenceye alır.
                    var created = new {{entity}}
                    {
                        OwnerId = global::Haldus.HaldusScope.UserId(context) ?? string.Empty,
                        {{rightKey}} = {{parameter}},
                    };
                    context.Set<{{entity}}>().Add(created);

                    try
                    {
                        await context.SaveChangesAsync(cancellationToken);
                    }
                    catch (global::Microsoft.EntityFrameworkCore.DbUpdateException)
                    {
                        // Aynı bağı başka bir istek aynı anda kurduysa sonuç zaten istenendir; başka bir
                        // nedenle başarısız olduysa hata yutulmaz.
                        context.Entry(created).State = global::Microsoft.EntityFrameworkCore.EntityState.Detached;
                        if (!await context.Set<{{entity}}>().AnyAsync(link => link.{{rightKey}} == {{parameter}}, cancellationToken))
                        {
                            throw;
                        }
                    }

                    return {{Http}}.TypedResults.NoContent();
                }

                private static async {{noContentOrProblem}} UnlinkAsync(
                    {{right.IdType}} {{parameter}},
                    global::Haldus.IHaldusDbContextProvider contexts,
                    global::Haldus.IHaldusMessages messages,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    var deleted = await contexts.For<{{entity}}>().Set<{{entity}}>()
                        .Where(link => link.{{rightKey}} == {{parameter}})
                        .ExecuteDeleteAsync(cancellationToken);

                    if (deleted == 0)
                    {
                        return {{Problems}}.NotFound(messages, "{{name}}", {{parameter}});
                    }

                    return {{Http}}.TypedResults.NoContent();
                }
            }

            """;
    }

    /// <summary>Bağ kaydının model yapılandırması.</summary>
    public static string EmitModel(UserLinkModel link)
    {
        var right = link.Right;

        // Bağ yalnızca sahibine ve ancak karşı ucu isteği yapana görünürse görünür. Koşullar modele konur ki
        // bağı okuyan her sorgu (liste, varlık kontrolü, silme) aynı korumadan geçsin. Karşı uç silinince bağ da
        // silinir: bağ yalnızca bir işarettir ve bir kullanıcının işareti kaydın silinmesini engellememelidir.
        return $$"""
                        modelBuilder.Entity<{{link.QualifiedLinkName}}>(entity =>
                        {
                            entity.ToTable("{{link.CollectionName}}");
                            entity.HasKey(item => new { item.OwnerId, item.{{link.RightKey}} });
                            entity.Property(item => item.OwnerId).HasMaxLength(200);
                            entity.HasOne<{{right.QualifiedName}}>().WithMany().HasForeignKey(item => item.{{link.RightKey}}).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade);
                            entity.HasIndex(item => item.{{link.RightKey}});
                            entity.HasQueryFilter(global::Haldus.HaldusFilters.Owner, item => item.OwnerId == global::Haldus.HaldusScope.UserId(context));
                            entity.HasQueryFilter(global::Haldus.HaldusFilters.Association, item =>
                                context.Set<{{right.QualifiedName}}>().Any(right => right.{{right.KeyName}} == item.{{link.RightKey}}));
                        });

            """;
    }
}
