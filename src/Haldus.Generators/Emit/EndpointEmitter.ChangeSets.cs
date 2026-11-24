using Haldus.Generators.Model;

namespace Haldus.Generators.Emit;

/// <summary>
/// Değişiklik kümeleri: bir kayda uygulanacak değişikliklerin adım adım biriktirilmesi, önizlenmesi ve tek seferde
/// uygulanması. Uygulama ve önizleme, kısmi güncelleme ucunun parçalarını kullanır; bu yüzden o ucun her kuralı
/// (doğrulama, referanslar, benzersizlik, hesaplanan alanlar, sürüm, kapanmış kayıt) burada da geçerlidir. Tek fark,
/// düzenlenebilir durum kuralının yerini kümenin kendi durum listesinin almasıdır.
/// </summary>
internal sealed partial class EndpointEmitter
{
    private void EmitChangeSets()
    {
        var allowedIn = _resource.ChangeSets!.AllowedIn;
        var info = "global::Haldus.ChangeSetInfo";
        var infoOrProblem = $"{Task}<{Results}.Results<{Results}.Ok<{info}>, {Problem}>>";
        var route = $"{_keyRoute}/edits";
        var metadata = $"\n            .ProducesProblem(404)\n            .ProducesProblem(409)";

        _map.Append($$"""
                    group.MapPost("{{route}}", BeginEditAsync)
                        .WithName("Begin{{_entity}}Edit"){{metadata}};

                    group.MapGet("{{route}}", ListEditsAsync)
                        .WithName("List{{_entity}}Edits")
                        .ProducesProblem(404);

                    group.MapGet("{{route}}/{editId:guid}", GetEditAsync)
                        .WithName("Get{{_entity}}Edit")
                        .ProducesProblem(404);

                    group.MapPatch("{{route}}/{editId:guid}", StageEditAsync)
                        .WithName("Stage{{_entity}}Edit")
                        .Accepts<Patch{{_entity}}Request>("application/json")
                        .ProducesValidationProblem(){{metadata}};

                    group.MapGet("{{route}}/{editId:guid}/preview", PreviewEditAsync)
                        .WithName("Preview{{_entity}}Edit")
                        .ProducesValidationProblem(){{metadata}};

                    group.MapPost("{{route}}/{editId:guid}/commit", CommitEditAsync)
                        .WithName("Commit{{_entity}}Edit")
                        .ProducesValidationProblem(){{metadata}}{{ConcurrencyMetadata()}};

                    group.MapDelete("{{route}}/{editId:guid}", DiscardEditAsync)
                        .WithName("Discard{{_entity}}Edit"){{metadata}};


            """);

        // Küme, kaydı isteği yapan görebiliyorsa bulunur; kaydın kendisi her zaman kendi filtrelerinden geçerek okunur.
        var findEdit = $$"""
                    var edit = await global::Haldus.HaldusChangeSets.FindAsync<{{_entity}}>(context, {{KeyText}}, editId, cancellationToken);

                    if (edit is null)
                    {
                        return {{Problems}}.NotFound(messages, "ChangeSet", editId);
                    }


            """;

        var openEdit = findEdit + $$"""
                    if (edit.Status != global::Haldus.ChangeSetStatus.Open)
                    {
                        return {{Problems}}.ChangeSetClosed(messages, edit.Status);
                    }


            """;

        var readRequest = $$"""
                    if (global::Haldus.HaldusChangeSetRequests.Read<Patch{{_entity}}Request>(edit, httpContext, messages, out var request) is { } invalid)
                    {
                        return invalid;
                    }


            """;

        string Visible(string returnType, string name, string parameters, string body) => $$"""

                private static async {{returnType}} {{name}}(
            {{ScopeParameter}}{{KeyParameter}}{{parameters}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var context = contexts.For<{{_entity}}>();

                    if (!await context.Set<{{_entity}}>().AnyAsync(item => {{Match}}, cancellationToken))
                    {
                        return {{NotFound}};
                    }

            {{body}}    }

            """;

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Results<{{Results}}.Created<{{info}}>, {{Problem}}>> BeginEditAsync(
            {{ScopeParameter}}{{KeyParameter}}{{HttpContextParameter}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>().AsNoTracking().FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ChainGuard()}}{{StateGuard(allowedIn, "edit")}}        var edit = await global::Haldus.HaldusChangeSets.BeginAsync<{{_entity}}>(context, {{KeyText}}, cancellationToken);

                    return {{Http}}.TypedResults.Created(
                        global::Haldus.HaldusRoutes.Child(httpContext, edit.Id), global::Haldus.HaldusChangeSets.ToInfo(edit));
                }

            """);

        _handlers.Append(Visible(
            $"{Task}<{Results}.Results<{Results}.Ok<global::System.Collections.Generic.List<{info}>>, {Problem}>>",
            "ListEditsAsync",
            string.Empty,
            $$"""
                    return {{Http}}.TypedResults.Ok(await global::Haldus.HaldusChangeSets.ListAsync<{{_entity}}>(context, {{KeyText}}, cancellationToken));

            """));

        _handlers.Append(Visible(
            infoOrProblem,
            "GetEditAsync",
            "        global::System.Guid editId,\n",
            $$"""
            {{findEdit}}        return {{Http}}.TypedResults.Ok(global::Haldus.HaldusChangeSets.ToInfo(edit));

            """));

        _handlers.Append(Visible(
            infoOrProblem,
            "StageEditAsync",
            $"        global::System.Guid editId,\n        global::System.Text.Json.JsonElement changes,\n{HttpContextParameter}",
            $$"""
            {{openEdit}}        // Değişiklik, kısmi güncelleme isteğiyle aynı kurallardan geçer; geçmezse küme değişmez.
                    if (global::Haldus.HaldusChangeSetRequests.Stage<Patch{{_entity}}Request>(edit, changes, httpContext, messages) is { } invalid)
                    {
                        return invalid;
                    }

                    try
                    {
                        await context.SaveChangesAsync(cancellationToken);
                    }
                    catch (global::Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
                    {
                        // Küme bu sırada uygulandı ya da ondan vazgeçildi.
                        return {{Problems}}.StateChanged(messages);
                    }

                    return {{Http}}.TypedResults.Ok(global::Haldus.HaldusChangeSets.ToInfo(edit));

            """));

        _handlers.Append(Visible(
            NoContentOrProblem,
            "DiscardEditAsync",
            "        global::System.Guid editId,\n",
            $$"""
            {{openEdit}}        global::Haldus.HaldusChangeSets.Close(context, edit, committed: false);

                    try
                    {
                        await context.SaveChangesAsync(cancellationToken);
                    }
                    catch (global::Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
                    {
                        return {{Problems}}.StateChanged(messages);
                    }

                    return {{Http}}.TypedResults.NoContent();

            """));

        _handlers.Append($$"""

                /// <summary>Küme şimdi uygulansaydı kaydın nasıl olacağını döndürür; hiçbir şey kaydetmez.</summary>
                private static async {{OkOrProblem}} PreviewEditAsync(
            {{ScopeParameter}}{{KeyParameter}}        global::System.Guid editId,
            {{HttpContextParameter}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{OwnerGuard("commit")}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{openEdit}}{{readRequest}}{{ChainGuard()}}{{StateGuard(allowedIn, "commit")}}{{WriteChecks(patch: true, creating: false)}}{{CycleCheck(patch: true)}}{{PatchAssignments()}}
            {{SyncDetails(patch: true)}}{{Compute()}}{{ApprovalPreview()}}        var preview = ToResponse(entity);

                    // Hiçbir şey kaydedilmez; bağlamda bekleyen değişiklikler de bırakılır.
                    context.ChangeTracker.Clear();
                    return {{Http}}.TypedResults.Ok(preview);
                }

                /// <summary>
                /// Kümeyi kayda uygular ve kapatır; ikisi tek kaydetmededir. Kayıt, küme hazırlanırken değişmiş olabilir:
                /// değişiklikler kaydın şimdiki haline uygulanır ve kısmi güncellemenin bütün kurallarından yeniden geçer.
                /// </summary>
                private static async {{OkOrProblem}} CommitEditAsync(
            {{ScopeParameter}}{{KeyParameter}}        global::System.Guid editId,
            {{HttpContextParameter}}{{(Sliding ? TimeProviderParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}{{OwnerGuard("commit")}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{openEdit}}{{readRequest}}{{ChainGuard()}}{{StateGuard(allowedIn, "commit")}}{{WriteChecks(patch: true, creating: false)}}{{CycleCheck(patch: true)}}{{ExpectVersion()}}{{OrderRemember()}}{{PatchAssignments()}}
            {{SyncDetails(patch: true)}}{{Slide()}}{{Compute()}}{{OrderReparent()}}        global::Haldus.HaldusChangeSets.Close(context, edit, committed: true);

            {{Save(always: true)}}{{OrderReparentCommit()}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """);
    }
}
