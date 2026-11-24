using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;

namespace Haldus.Generators.Emit;

/// <summary>
/// Çoktan-çoğa bir bağın entity'sini ve endpoint'lerini üretir. Bağın kendi kimliği yoktur; anahtarı iki
/// ucun anahtarlarının çiftidir, bu yüzden aynı bağ iki kez kurulamaz.
/// </summary>
internal static class AssociationEmitter
{
    private const string Task = "global::System.Threading.Tasks.Task";
    private const string Http = "global::Microsoft.AspNetCore.Http";
    private const string Results = "global::Microsoft.AspNetCore.Http.HttpResults";
    private const string Problems = "global::Haldus.HaldusProblems";

    public static string EmitEntity(AssociationModel association) =>
        $$"""
        {{ResourceEmitter.Header}}
        {{ResourceEmitter.NamespaceLine(association.Namespace)}}
        public partial class {{association.LinkName}}
        {
            public {{association.Left.IdType}} {{association.LeftKey}} { get; set; }{{Initializer(association.Left)}}

            public {{association.Right.IdType}} {{association.RightKey}} { get; set; }{{Initializer(association.Right)}}
        }

        """;

    public static string EmitEndpoints(AssociationModel association)
    {
        var left = association.Left;
        var right = association.Right;
        var link = association.LinkName;
        var leftKey = association.LeftKey;
        var rightKey = association.RightKey;
        var leftParameter = ResourceEmitter.CamelCase(leftKey);
        var rightParameter = ResourceEmitter.CamelCase(rightKey);
        var lefts = Naming.Pluralize(left.EntityName);
        var rights = Naming.Pluralize(right.EntityName);
        var leftRoute = $"/{Naming.ToKebabCase(lefts)}/{{{leftParameter}{Constraint(left)}}}";
        var rightRoute = $"/{Naming.ToKebabCase(rights)}/{{{rightParameter}{Constraint(right)}}}";
        var pair = $"{leftRoute}/{Naming.ToKebabCase(rights)}/{{{rightParameter}{Constraint(right)}}}";
        var noContentOrProblem = $"{Task}<{Results}.Results<{Results}.NoContent, {Results}.ProblemHttpResult>>";
        var matches = $"link.{leftKey} == {leftParameter} && link.{rightKey} == {rightParameter}";

        return $$"""
            {{ResourceEmitter.Header}}
            using System.Linq;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Routing;
            using Microsoft.EntityFrameworkCore;

            {{ResourceEmitter.NamespaceLine(association.Namespace)}}
            public static partial class {{link}}Endpoints
            {
                public static global::Microsoft.AspNetCore.Routing.RouteGroupBuilder Map(
                    global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
                {
                    var group = endpoints.MapGroup(string.Empty).WithTags("{{lefts}}");

                    group.MapGet("{{leftRoute}}/{{Naming.ToKebabCase(rights)}}", List{{rights}}Async)
                        .WithName("List{{left.EntityName}}{{rights}}");

                    group.MapGet("{{pair}}", ExistsAsync)
                        .WithName("Get{{link}}")
                        .ProducesProblem(404);

                    group.MapPut("{{pair}}", LinkAsync)
                        .WithName("Put{{link}}")
                        .ProducesProblem(404);

                    group.MapDelete("{{pair}}", UnlinkAsync)
                        .WithName("Delete{{link}}")
                        .ProducesProblem(404);

                    group.MapGet("{{rightRoute}}/{{Naming.ToKebabCase(lefts)}}", List{{lefts}}Async)
                        .WithName("List{{right.EntityName}}{{lefts}}");

                    return group;
                }

                private static async {{Task}}<{{Results}}.Ok<global::Haldus.PagedResult<{{right.QualifiedName}}Response>>> List{{rights}}Async(
                    {{left.IdType}} {{leftParameter}},
                    [{{Http}}.AsParameters] global::Haldus.PageRequest page,
                    global::Haldus.IHaldusDbContextProvider contexts,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    var links = contexts.For<{{link}}>().Set<{{link}}>();
                    var query = contexts.For<{{right.QualifiedName}}>().Set<{{right.QualifiedName}}>()
                        .AsNoTracking()
                        .Where(item => links.Any(link => link.{{leftKey}} == {{leftParameter}} && link.{{rightKey}} == item.{{right.KeyName}}));

                    var totalCount = await query.LongCountAsync(cancellationToken);
                    var items = await query
                        .OrderBy(item => item.{{right.KeyName}})
                        .Skip(page.Skip)
                        .Take(page.PageSizeOrDefault)
                        .Select(item => {{Projection(right)}})
                        .ToListAsync(cancellationToken);

                    return {{Http}}.TypedResults.Ok(
                        new global::Haldus.PagedResult<{{right.QualifiedName}}Response>(items, page.PageOrDefault, page.PageSizeOrDefault, totalCount));
                }

                private static async {{Task}}<{{Results}}.Ok<global::Haldus.PagedResult<{{left.QualifiedName}}Response>>> List{{lefts}}Async(
                    {{right.IdType}} {{rightParameter}},
                    [{{Http}}.AsParameters] global::Haldus.PageRequest page,
                    global::Haldus.IHaldusDbContextProvider contexts,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    var links = contexts.For<{{link}}>().Set<{{link}}>();
                    var query = contexts.For<{{left.QualifiedName}}>().Set<{{left.QualifiedName}}>()
                        .AsNoTracking()
                        .Where(item => links.Any(link => link.{{rightKey}} == {{rightParameter}} && link.{{leftKey}} == item.{{left.KeyName}}));

                    var totalCount = await query.LongCountAsync(cancellationToken);
                    var items = await query
                        .OrderBy(item => item.{{left.KeyName}})
                        .Skip(page.Skip)
                        .Take(page.PageSizeOrDefault)
                        .Select(item => {{Projection(left)}})
                        .ToListAsync(cancellationToken);

                    return {{Http}}.TypedResults.Ok(
                        new global::Haldus.PagedResult<{{left.QualifiedName}}Response>(items, page.PageOrDefault, page.PageSizeOrDefault, totalCount));
                }

                private static async {{noContentOrProblem}} ExistsAsync(
                    {{left.IdType}} {{leftParameter}},
                    {{right.IdType}} {{rightParameter}},
                    global::Haldus.IHaldusDbContextProvider contexts,
                    global::Haldus.IHaldusMessages messages,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    if (!await contexts.For<{{link}}>().Set<{{link}}>().AnyAsync(link => {{matches}}, cancellationToken))
                    {
                        return {{Problems}}.NotFound(messages, "{{link}}", $"{{{leftParameter}}}/{{{rightParameter}}}");
                    }

                    return {{Http}}.TypedResults.NoContent();
                }

                /// <summary>Bağı kurar. Bağ zaten varsa hiçbir şey değişmez; istek yinelenebilir.</summary>
                private static async {{noContentOrProblem}} LinkAsync(
                    {{left.IdType}} {{leftParameter}},
                    {{right.IdType}} {{rightParameter}},
                    global::Haldus.IHaldusDbContextProvider contexts,
                    global::Haldus.IHaldusMessages messages,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    // İki uç da kendi filtrelerinden geçerek aranır; görünmeyen bir kayda bağ kurulamaz.
                    if (!await contexts.For<{{left.QualifiedName}}>().Set<{{left.QualifiedName}}>()
                        .AnyAsync(item => item.{{left.KeyName}} == {{leftParameter}}, cancellationToken))
                    {
                        return {{Problems}}.NotFound(messages, "{{left.EntityName}}", {{leftParameter}});
                    }

                    if (!await contexts.For<{{right.QualifiedName}}>().Set<{{right.QualifiedName}}>()
                        .AnyAsync(item => item.{{right.KeyName}} == {{rightParameter}}, cancellationToken))
                    {
                        return {{Problems}}.NotFound(messages, "{{right.EntityName}}", {{rightParameter}});
                    }

                    var context = contexts.For<{{link}}>();
                    if (await context.Set<{{link}}>().AnyAsync(link => {{matches}}, cancellationToken))
                    {
                        return {{Http}}.TypedResults.NoContent();
                    }

                    var created = new {{link}} { {{leftKey}} = {{leftParameter}}, {{rightKey}} = {{rightParameter}} };
                    context.Set<{{link}}>().Add(created);

                    try
                    {
                        await context.SaveChangesAsync(cancellationToken);
                    }
                    catch (global::Microsoft.EntityFrameworkCore.DbUpdateException)
                    {
                        // Aynı bağı başka bir istek aynı anda kurduysa sonuç zaten istenendir; başka bir
                        // nedenle başarısız olduysa hata yutulmaz.
                        context.Entry(created).State = global::Microsoft.EntityFrameworkCore.EntityState.Detached;
                        if (!await context.Set<{{link}}>().AnyAsync(link => {{matches}}, cancellationToken))
                        {
                            throw;
                        }
                    }

                    return {{Http}}.TypedResults.NoContent();
                }

                private static async {{noContentOrProblem}} UnlinkAsync(
                    {{left.IdType}} {{leftParameter}},
                    {{right.IdType}} {{rightParameter}},
                    global::Haldus.IHaldusDbContextProvider contexts,
                    global::Haldus.IHaldusMessages messages,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    var deleted = await contexts.For<{{link}}>().Set<{{link}}>()
                        .Where(link => {{matches}})
                        .ExecuteDeleteAsync(cancellationToken);

                    if (deleted == 0)
                    {
                        return {{Problems}}.NotFound(messages, "{{link}}", $"{{{leftParameter}}}/{{{rightParameter}}}");
                    }

                    return {{Http}}.TypedResults.NoContent();
                }
            }

            """;
    }

    /// <summary>Bağ kaydının model yapılandırması.</summary>
    public static string EmitModel(AssociationModel association)
    {
        var link = association.QualifiedLinkName;
        var left = association.Left;
        var right = association.Right;
        const string DeleteBehavior = "global::Microsoft.EntityFrameworkCore.DeleteBehavior";

        // Bağ, ancak iki ucu da isteği yapana görünürse görünür. Koşul modele konur ki bağı okuyan her
        // sorgu (iki yönlü listeler, varlık kontrolü, silme) aynı korumadan geçsin.
        return $$"""
                        modelBuilder.Entity<{{link}}>(entity =>
                        {
                            entity.ToTable("{{Haldus.Generators.Infrastructure.Naming.Pluralize(association.LinkName)}}");
                            entity.HasKey(item => new { item.{{association.LeftKey}}, item.{{association.RightKey}} });
                            entity.HasOne<{{left.QualifiedName}}>().WithMany().HasForeignKey(item => item.{{association.LeftKey}}).OnDelete({{DeleteBehavior}}.{{association.LeftOnDelete}});
                            entity.HasOne<{{right.QualifiedName}}>().WithMany().HasForeignKey(item => item.{{association.RightKey}}).OnDelete({{DeleteBehavior}}.{{association.RightOnDelete}});
                            entity.HasIndex(item => item.{{association.RightKey}});
                            entity.HasQueryFilter(global::Haldus.HaldusFilters.Association, item =>
                                context.Set<{{left.QualifiedName}}>().Any(left => left.{{left.KeyName}} == item.{{association.LeftKey}})
                                && context.Set<{{right.QualifiedName}}>().Any(right => right.{{right.KeyName}} == item.{{association.RightKey}}));
                        });

            """;
    }

    /// <summary>Bir kaynağın yanıt DTO'suna izdüşümü; bağ listeleri karşı ucun yanıtını döndürür.</summary>
    private static string Projection(ResourceModel resource)
    {
        var assignments = new[] { $"{resource.KeyName} = item.{resource.KeyName}" }
            .Concat(resource.Properties.Select(property => $"{property.Name} = item.{property.Name}"))
            .Concat(TraitCatalog.ResponseMembers(resource.Traits).Select(member => $"{member.Name} = item.{member.Name}"));

        return $"new {resource.QualifiedName}Response {{ {string.Join(", ", assignments)} }}";
    }

    private static string Constraint(ResourceModel resource) => resource.IdKind switch
    {
        IdKind.Int32 => ":int",
        IdKind.Int64 => ":long",
        IdKind.String => string.Empty,
        _ => ":guid",
    };

    private static string Initializer(ResourceModel resource) => resource.IdKind == IdKind.String ? " = \"\";" : string.Empty;
}
