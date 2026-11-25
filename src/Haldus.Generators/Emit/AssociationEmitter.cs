using System.Text;
using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;

namespace Haldus.Generators.Emit;

/// <summary>
/// Çoktan-çoğa bir bağın entity'sini ve endpoint'lerini üretir. Bağın kendi kimliği yoktur; anahtarı iki
/// ucun anahtarlarının çiftidir, bu yüzden aynı bağ iki kez kurulamaz. Kullanıcı bağ sınıfına kendi alanlarını
/// yazdıysa (üyelikteki rol gibi) bağ yük taşır: <c>PUT</c> gövdesinde yazılır, okunduğunda döner. Bağın bir durum
/// makinesi de olabilir (<c>Workflow</c>): durum yeni bağda başlangıç değerini alır ve yalnızca bağın builder'ında
/// bildirilen gövdesiz geçişlerle değişir.
/// </summary>
internal static class AssociationEmitter
{
    private const string Task = "global::System.Threading.Tasks.Task";
    private const string Http = "global::Microsoft.AspNetCore.Http";
    private const string Results = "global::Microsoft.AspNetCore.Http.HttpResults";
    private const string Problems = "global::Haldus.HaldusProblems";

    /// <summary>Tek istekte topluca kurulabilecek en çok bağ.</summary>
    private const int MaxReplaced = 1000;

    public static string EmitEntity(AssociationModel association) =>
        $$"""
        {{ResourceEmitter.Header}}
        {{ResourceEmitter.NamespaceLine(association.Namespace)}}
        {{(association.LinkDeclared ? string.Empty : "public ")}}partial class {{association.LinkName}}
        {
            public {{association.Left.IdType}} {{association.LeftKey}} { get; set; }{{Initializer(association.Left)}}

            public {{association.Right.IdType}} {{association.RightKey}} { get; set; }{{Initializer(association.Right)}}
        {{(association.Workflow is { } workflow && !association.StatusDeclared ? $"\n    public {workflow.StatusType} Status {{ get; set; }}\n" : string.Empty)}}}

        """;

    /// <summary>Yük veya durum taşıyan bağın istek ve yanıt tipleri; bağ ikisini de taşımıyorsa <c>null</c>.</summary>
    public static string? EmitDtos(AssociationModel association)
    {
        if (!association.HasResponse)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append(ResourceEmitter.Header).Append('\n');
        builder.Append(ResourceEmitter.NamespaceLine(association.Namespace)).Append('\n');

        builder.Append($"public sealed partial record {association.LinkName}Response\n{{\n");
        builder.Append($"    public required {association.Left.IdType} {association.LeftKey} {{ get; init; }}\n");
        builder.Append($"    public required {association.Right.IdType} {association.RightKey} {{ get; init; }}\n");

        foreach (var property in association.Payload)
        {
            builder.Append($"    public required {property.Type} {property.Name} {{ get; init; }}\n");
        }

        if (association.Workflow is { } workflow)
        {
            // Durum, uygulamanın genel JSON ayarından bağımsız olarak adıyla yazılır.
            builder.Append($"    [global::System.Text.Json.Serialization.JsonConverter(typeof(global::System.Text.Json.Serialization.JsonStringEnumConverter<{workflow.StatusType}>))]\n");
            builder.Append($"    public required {workflow.StatusType} Status {{ get; init; }}\n");
        }

        builder.Append("}\n");

        if (!association.HasPayload)
        {
            return builder.ToString();
        }

        builder.Append($"\npublic sealed partial record {association.LinkName}Request\n{{\n");

        var first = true;
        foreach (var property in association.Payload.Where(property => property.IsWritable))
        {
            builder.Append(first ? string.Empty : "\n");
            first = false;

            foreach (var attribute in property.ValidationAttributes)
            {
                builder.Append($"    [{attribute.Rendered}]\n");
            }

            builder.Append($"    public {property.Type} {property.Name} {{ get; init; }}{(property.IsNonNullableReference ? " = default!;" : string.Empty)}\n");
        }

        builder.Append("}\n");
        return builder.ToString();
    }

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
        var linkOrProblem = $"{Task}<{Results}.Results<{Results}.Ok<{link}Response>, {Results}.ProblemHttpResult>>";
        var matches = $"link.{leftKey} == {leftParameter} && link.{rightKey} == {rightParameter}";
        var payload = association.HasPayload;
        var typed = association.HasResponse;
        var status = association.Workflow is { } workflow ? $"{workflow.StatusType}.{workflow.Initial}" : null;

        var extraMap = new StringBuilder();
        var extraHandlers = new StringBuilder();
        string exists;
        string linkHandler;

        if (typed)
        {
            var requestParameter = payload ? $"\n        {link}Request request," : string.Empty;
            var stateCatch = status is null
                ? string.Empty
                : $$"""
                                catch (global::Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException) when (!isNew)
                                {
                                    // Bağın durumu bu sırada bir geçişle değişti (veya bağ kaldırıldı).
                                    return {{Problems}}.StateChanged(messages);
                                }

                    """;

            var assignments = string.Concat(association.Payload
                .Where(property => property.IsWritable)
                .Select(property => $"                existing.{property.Name} = request.{property.Name};\n"));

            exists = $$"""

                    private static async {{linkOrProblem}} ExistsAsync(
                        {{left.IdType}} {{leftParameter}},
                        {{right.IdType}} {{rightParameter}},
                        global::Haldus.IHaldusDbContextProvider contexts,
                        global::Haldus.IHaldusMessages messages,
                        global::System.Threading.CancellationToken cancellationToken)
                    {
                        var existing = await contexts.For<{{link}}>().Set<{{link}}>().AsNoTracking()
                            .FirstOrDefaultAsync(link => {{matches}}, cancellationToken);

                        if (existing is null)
                        {
                            return {{Problems}}.NotFound(messages, "{{link}}", $"{{{leftParameter}}}/{{{rightParameter}}}");
                        }

                        return {{Http}}.TypedResults.Ok(ToLinkResponse(existing));
                    }

                """;

            linkHandler = $$"""

                    /// <summary>Bağı kurar veya, zaten varsa, taşıdığı alanları değiştirir; istek yinelenebilir.</summary>
                    private static async {{linkOrProblem}} LinkAsync(
                        {{left.IdType}} {{leftParameter}},
                        {{right.IdType}} {{rightParameter}},{{requestParameter}}
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

                        for (var attempt = 0; ; attempt++)
                        {
                            var existing = await context.Set<{{link}}>().FirstOrDefaultAsync(link => {{matches}}, cancellationToken);
                            var isNew = existing is null;
                            existing ??= new {{link}} { {{leftKey}} = {{leftParameter}}, {{rightKey}} = {{rightParameter}}{{(status is null ? string.Empty : $", Status = {status}")}} };

                {{assignments}}
                            if (isNew)
                            {
                                context.Set<{{link}}>().Add(existing);
                            }

                            try
                            {
                                await context.SaveChangesAsync(cancellationToken);
                            }
                {{stateCatch}}            catch (global::Microsoft.EntityFrameworkCore.DbUpdateException) when (isNew && attempt == 0)
                            {
                                // Aynı bağı başka bir istek aynı anda kurdu; ikinci turda güncelleme olarak tamamlanır.
                                context.Entry(existing).State = global::Microsoft.EntityFrameworkCore.EntityState.Detached;
                                continue;
                            }

                            return {{Http}}.TypedResults.Ok(ToLinkResponse(existing));
                        }
                    }

                    private static {{link}}Response ToLinkResponse({{link}} link) => new()
                    {
                        {{leftKey}} = link.{{leftKey}},
                        {{rightKey}} = link.{{rightKey}},
                {{string.Concat(association.Payload.Select(property => $"        {property.Name} = link.{property.Name},\n"))}}{{(status is null ? string.Empty : "        Status = link.Status,\n")}}    };

                """;

            // Bağların kendisi (yükleriyle) iki yönden listelenir; uçların kendisi öteki listelerdedir.
            extraMap.Append($$"""

                        group.MapGet("{{leftRoute}}/{{Naming.ToKebabCase(rights)}}/links", List{{left.EntityName}}LinksAsync)
                            .WithName("List{{left.EntityName}}{{link}}Links");

                        group.MapGet("{{rightRoute}}/{{Naming.ToKebabCase(lefts)}}/links", List{{right.EntityName}}LinksAsync)
                            .WithName("List{{right.EntityName}}{{link}}Links");

                """);

            extraHandlers.Append(EmitLinkList(association, left, leftKey, rightKey, leftParameter));
            extraHandlers.Append(EmitLinkList(association, right, rightKey, leftKey, rightParameter));

            foreach (var transition in association.Transitions)
            {
                extraMap.Append($$"""

                            group.MapPost("{{pair}}/{{transition.Route}}", {{transition.PascalName}}Async)
                                .WithName("{{transition.PascalName}}{{link}}")
                                .ProducesProblem(404)
                                .ProducesProblem(409);

                    """);

                extraHandlers.Append($$"""

                        /// <summary>Bağın durumunu değiştirir; bağ sayılan durumlardan birinde değilse 409 döner.</summary>
                        private static async {{linkOrProblem}} {{transition.PascalName}}Async(
                            {{left.IdType}} {{leftParameter}},
                            {{right.IdType}} {{rightParameter}},
                            global::Haldus.IHaldusDbContextProvider contexts,
                            global::Haldus.IHaldusMessages messages,
                            global::System.Threading.CancellationToken cancellationToken)
                        {
                            var context = contexts.For<{{link}}>();
                            var existing = await context.Set<{{link}}>().FirstOrDefaultAsync(link => {{matches}}, cancellationToken);

                            if (existing is null)
                            {
                                return {{Problems}}.NotFound(messages, "{{link}}", $"{{{leftParameter}}}/{{{rightParameter}}}");
                            }

                            if (existing.Status is not ({{string.Join(" or ", transition.From.Select(state => $"{association.Workflow!.StatusType}.{state}"))}}))
                            {
                                return {{Problems}}.StateConflict(messages, "{{transition.Route}}", existing.Status);
                            }

                            existing.Status = {{association.Workflow!.StatusType}}.{{transition.To}};

                            try
                            {
                                await context.SaveChangesAsync(cancellationToken);
                            }
                            catch (global::Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
                            {
                                // Kontrol ile kaydetme arasında başka bir istek durumu değiştirdi.
                                return {{Problems}}.StateChanged(messages);
                            }

                            return {{Http}}.TypedResults.Ok(ToLinkResponse(existing));
                        }

                    """);
            }
        }
        else
        {
            exists = $$"""

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

                """;

            linkHandler = $$"""

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

                """;

            // Yük taşımayan bağlar topluca değiştirilebilir: bir ucun bağları, verilen listeyle aynı olur.
            extraMap.Append($$"""

                        group.MapPut("{{leftRoute}}/{{Naming.ToKebabCase(rights)}}", Replace{{rights}}Async)
                            .WithName("Replace{{left.EntityName}}{{rights}}")
                            .AddEndpointFilter<global::Haldus.HaldusValidationFilter<global::Haldus.BatchIdsRequest<{{right.IdType}}>>>()
                            .ProducesValidationProblem()
                            .ProducesProblem(404);

                        group.MapPut("{{rightRoute}}/{{Naming.ToKebabCase(lefts)}}", Replace{{lefts}}Async)
                            .WithName("Replace{{right.EntityName}}{{lefts}}")
                            .AddEndpointFilter<global::Haldus.HaldusValidationFilter<global::Haldus.BatchIdsRequest<{{left.IdType}}>>>()
                            .ProducesValidationProblem()
                            .ProducesProblem(404);

                """);

            extraHandlers.Append(EmitReplace(association, left, right, leftKey, rightKey, leftParameter, rights));
            extraHandlers.Append(EmitReplace(association, right, left, rightKey, leftKey, rightParameter, lefts));
        }

        var validation = payload
            ? $"\n            .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{link}Request>>()\n            .ProducesValidationProblem()"
            : string.Empty;

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
                    var group = endpoints.MapGroup(string.Empty).WithTags("{{lefts}}")
                        .WithMetadata(new global::Haldus.HaldusResourceScope({{(association.TenantScoped ? "true" : "false")}}, {{(association.OwnerScoped ? "true" : "false")}}));

                    group.MapGet("{{leftRoute}}/{{Naming.ToKebabCase(rights)}}", List{{rights}}Async)
                        .WithName("List{{left.EntityName}}{{rights}}");

                    group.MapGet("{{pair}}", ExistsAsync)
                        .WithName("Get{{link}}")
                        .ProducesProblem(404);

                    group.MapPut("{{pair}}", LinkAsync)
                        .WithName("Put{{link}}"){{validation}}
                        .ProducesProblem(404);

                    group.MapDelete("{{pair}}", UnlinkAsync)
                        .WithName("Delete{{link}}")
                        .ProducesProblem(404);

                    group.MapGet("{{rightRoute}}/{{Naming.ToKebabCase(lefts)}}", List{{lefts}}Async)
                        .WithName("List{{right.EntityName}}{{lefts}}");
            {{extraMap}}
                    return group;
                }
            {{EmitList(association, left, right, leftKey, rightKey, leftParameter, rights)}}{{EmitList(association, right, left, rightKey, leftKey, rightParameter, lefts)}}{{exists}}{{linkHandler}}
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
            {{extraHandlers}}}

            """;
    }

    /// <summary>Bir ucun bağlı olduğu karşı uç kayıtlarının listesi.</summary>
    private static string EmitList(
        AssociationModel association,
        ResourceModel from,
        ResourceModel to,
        string fromKey,
        string toKey,
        string parameter,
        string targets) =>
        $$"""

            private static async {{Task}}<{{Results}}.Ok<global::Haldus.PagedResult<{{to.QualifiedName}}Response>>> List{{targets}}Async(
                {{from.IdType}} {{parameter}},
                [{{Http}}.AsParameters] global::Haldus.PageRequest page,
                global::Haldus.IHaldusDbContextProvider contexts,
                global::System.Threading.CancellationToken cancellationToken)
            {
                var links = contexts.For<{{association.LinkName}}>().Set<{{association.LinkName}}>();
                var query = contexts.For<{{to.QualifiedName}}>().Set<{{to.QualifiedName}}>()
                    .AsNoTracking()
                    .Where(item => links.Any(link => link.{{fromKey}} == {{parameter}} && link.{{toKey}} == item.{{to.KeyName}}));

                var totalCount = await query.LongCountAsync(cancellationToken);
                var items = await query
                    .OrderBy(item => item.{{to.KeyName}})
                    .Skip(page.Skip)
                    .Take(page.PageSizeOrDefault)
                    .Select(item => {{Projection(to)}})
                    .ToListAsync(cancellationToken);

                return {{Http}}.TypedResults.Ok(
                    new global::Haldus.PagedResult<{{to.QualifiedName}}Response>(items, page.PageOrDefault, page.PageSizeOrDefault, totalCount));
            }

        """;

    /// <summary>Bir ucun bağlarının, taşıdıkları alanlarla listesi.</summary>
    private static string EmitLinkList(AssociationModel association, ResourceModel from, string fromKey, string toKey, string parameter)
    {
        var link = association.LinkName;
        var assignments = string.Join(
            ", ",
            new[] { $"{association.LeftKey} = link.{association.LeftKey}", $"{association.RightKey} = link.{association.RightKey}" }
                .Concat(association.Payload.Select(property => $"{property.Name} = link.{property.Name}"))
            .Concat(association.Workflow is null ? [] : ["Status = link.Status"]));
        var statusParameter = association.Workflow is { } workflow ? $"\n        {workflow.StatusType}? status," : string.Empty;
        var statusFilter = association.Workflow is null
            ? string.Empty
            : """

                    if (status is not null)
                    {
                        query = query.Where(link => link.Status == status);
                    }

            """;

        return $$"""

                private static async {{Task}}<{{Results}}.Ok<global::Haldus.PagedResult<{{link}}Response>>> List{{from.EntityName}}LinksAsync(
                    {{from.IdType}} {{parameter}},
                    [{{Http}}.AsParameters] global::Haldus.PageRequest page,{{statusParameter}}
                    global::Haldus.IHaldusDbContextProvider contexts,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    // Bağın sorgu filtresi iki ucun da görünür olmasını şart koşar.
                    var query = contexts.For<{{link}}>().Set<{{link}}>().AsNoTracking().Where(link => link.{{fromKey}} == {{parameter}});
            {{statusFilter}}
                    var totalCount = await query.LongCountAsync(cancellationToken);
                    var items = await query
                        .OrderBy(link => link.{{toKey}})
                        .Skip(page.Skip)
                        .Take(page.PageSizeOrDefault)
                        .Select(link => new {{link}}Response { {{assignments}} })
                        .ToListAsync(cancellationToken);

                    return {{Http}}.TypedResults.Ok(
                        new global::Haldus.PagedResult<{{link}}Response>(items, page.PageOrDefault, page.PageSizeOrDefault, totalCount));
                }

            """;
    }

    /// <summary>
    /// Bir ucun bağlarını verilen listeyle aynı yapar: listede olmayan bağlar kaldırılır, eksikler kurulur; hepsi
    /// tek kaydetmede. İsteği yapana görünmeyen bağlara (ör. başka bir kiracının kaydıyla olanlara) dokunulmaz.
    /// </summary>
    private static string EmitReplace(
        AssociationModel association,
        ResourceModel from,
        ResourceModel to,
        string fromKey,
        string toKey,
        string parameter,
        string targets)
    {
        var link = association.LinkName;

        return $$"""

                private static async {{Task}}<{{Results}}.Results<{{Results}}.NoContent, {{Results}}.ProblemHttpResult>> Replace{{targets}}Async(
                    {{from.IdType}} {{parameter}},
                    global::Haldus.BatchIdsRequest<{{to.IdType}}> request,
                    global::Haldus.IHaldusDbContextProvider contexts,
                    global::Haldus.IHaldusMessages messages,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    var ids = request.Ids.Distinct().ToList();

                    if (ids.Count > {{MaxReplaced}})
                    {
                        return {{Problems}}.BatchTooLarge(messages, {{MaxReplaced}});
                    }

                    if (!await contexts.For<{{from.QualifiedName}}>().Set<{{from.QualifiedName}}>()
                        .AnyAsync(item => item.{{from.KeyName}} == {{parameter}}, cancellationToken))
                    {
                        return {{Problems}}.NotFound(messages, "{{from.EntityName}}", {{parameter}});
                    }

                    // Listedeki her kayıt isteği yapana görünür olmalıdır; biri bile bulunamazsa hiçbir şey değişmez.
                    var visible = await contexts.For<{{to.QualifiedName}}>().Set<{{to.QualifiedName}}>()
                        .Where(item => ids.Contains(item.{{to.KeyName}}))
                        .Select(item => item.{{to.KeyName}})
                        .ToListAsync(cancellationToken);

                    if (ids.Except(visible).Take(1).ToList() is [var missing])
                    {
                        return {{Problems}}.ReferenceNotFound(messages, "ids", "{{to.EntityName}}", missing!);
                    }

                    var context = contexts.For<{{link}}>();
                    var current = await context.Set<{{link}}>().Where(link => link.{{fromKey}} == {{parameter}}).ToListAsync(cancellationToken);

                    context.Set<{{link}}>().RemoveRange(current.Where(link => !ids.Contains(link.{{toKey}})));

                    foreach (var id in ids.Except(current.Select(link => link.{{toKey}})))
                    {
                        context.Set<{{link}}>().Add(new {{link}} { {{fromKey}} = {{parameter}}, {{toKey}} = id });
                    }

                    await context.SaveChangesAsync(cancellationToken);
                    return {{Http}}.TypedResults.NoContent();
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
                            entity.HasIndex(item => item.{{association.RightKey}});{{(association.Workflow is null ? string.Empty : "\n                entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(64).IsConcurrencyToken();")}}
                            entity.HasQueryFilter(global::Haldus.HaldusFilters.Association, item =>
                                context.Set<{{left.QualifiedName}}>().Any(left => left.{{left.KeyName}} == item.{{association.LeftKey}})
                                && context.Set<{{right.QualifiedName}}>().Any(right => right.{{right.KeyName}} == item.{{association.RightKey}}));
                        });

            """;
    }

    /// <summary>Bir kaynağın yanıt DTO'suna izdüşümü; bağ listeleri karşı ucun yanıtını döndürür.</summary>
    private static string Projection(ResourceModel resource) =>
        $"new {resource.QualifiedName}Response {{ {ResourceEmitter.ResponseAssignments(resource)} }}";

    private static string Constraint(ResourceModel resource) => ResourceEmitter.RouteConstraint(resource);

    private static string Initializer(ResourceModel resource) => resource.IdKind == IdKind.String ? " = \"\";" : string.Empty;
}
