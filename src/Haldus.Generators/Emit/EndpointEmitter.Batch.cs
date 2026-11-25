using Haldus.Generators.Model;

namespace Haldus.Generators.Emit;

/// <summary>
/// Toplu uçlar (<c>.Batch()</c>). Her öğe, tekil ucun kullandığı kod parçalarıyla işlenir; böylece doğrulama,
/// referans ve durum kuralları, satır eşitleme ve trait davranışları toplu yolda da aynen uygulanır. Atomik
/// ve kısmi mod yalnızca bir öğe başarısız olduğunda ne yapıldığında ayrılır.
/// </summary>
internal sealed partial class EndpointEmitter
{
    private const string DbContextParameter = "        global::Microsoft.EntityFrameworkCore.DbContext context,\n";
    private const string Batch = "global::Haldus.HaldusBatch";

    private string ScopeArgument => Parent is null ? string.Empty : $"{ParentParameter}, ";

    private string SavedParameter => $"        global::System.Collections.Generic.List<{_entity}> saved,\n";

    private void EmitBatch()
    {
        var batch = _resource.Batch!;

        if (_resource.Has(Operations.Get))
        {
            EmitBatchGet(batch);
        }

        if (_resource.Has(Operations.Create))
        {
            EmitBatchWrite(batch, create: true);
        }

        if (_resource.Has(Operations.Update))
        {
            EmitBatchWrite(batch, create: false);
        }

        if (_resource.Has(Operations.Delete))
        {
            EmitBatchDelete(batch);
        }
    }

    private string TooLarge(string count, BatchModel batch) => $$"""
                if ({{count}} > {{batch.MaxItems}})
                {
                    return {{Problems}}.BatchTooLarge(messages, {{batch.MaxItems}});
                }


        """;

    /// <summary>Okuma her zaman kısmidir: bulunanlar ve bulunamayan kimlikler ayrı döner.</summary>
    private void EmitBatchGet(BatchModel batch)
    {
        var request = $"global::Haldus.BatchIdsRequest<{_resource.IdType}>";
        var result = $"global::Haldus.BatchGetResult<{_entity}Response, {_resource.IdType}>";

        _map.Append($$"""
                    group.MapPost("/batch-get", BatchGetAsync)
                        .WithName("BatchGet{{_plural}}")
                        .WithMetadata(global::Haldus.HaldusReadOnly.Instance)
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{{request}}>>()
                        .ProducesValidationProblem();


            """);

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Results<{{Results}}.Ok<{{result}}>, {{Problem}}>> BatchGetAsync(
            {{ScopeParameter}}        {{request}} request,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var ids = request.Ids.Distinct().ToList();

            {{TooLarge("ids.Count", batch)}}        var items = await contexts.For<{{_entity}}>().Set<{{_entity}}>().AsNoTracking(){{ScopeFilter}}
                        .Where(item => ids.Contains(item.{{_key}}))
                        .OrderBy(item => item.{{_key}})
                        .Select(item => new {{_entity}}Response { {{ResponseAssignments}} })
                        .ToListAsync(cancellationToken);

                    var found = items.Select(item => item.{{_key}}).ToHashSet();
                    return {{Http}}.TypedResults.Ok(new {{result}}(items, ids.Where(id => !found.Contains(id)).ToList()));
                }

            """);
    }

    private void EmitBatchWrite(BatchModel batch, bool create)
    {
        var operation = create ? "Create" : "Update";
        var request = $"Batch{operation}{_entity}Request";
        var item = create ? $"Create{_entity}Request" : $"BatchUpdate{_entity}Item";
        var needsTime = create ? HasLifetime : Sliding;
        var time = needsTime ? TimeProviderParameter : string.Empty;
        var timeArgument = needsTime ? "timeProvider, " : string.Empty;
        var response = $"{_entity}Response";

        // Ebeveynin (ve sahibin durumunun) kontrolü öğe başına değil, istek başına bir kez yapılır.
        var guard = create
            ? _resource.OwnerGuard is null ? ParentCheck() : OwnerGuard("create")
            : OwnerGuard("update");

        var call = $"Batch{operation}ItemAsync({ScopeArgument}items[index], context, saved, {timeArgument}contexts, messages, cancellationToken)";

        // Atomik modda öğeler isteğin doğrulama filtresinden geçer; kısmi modda her öğe ayrı doğrulanır ki
        // geçersiz bir öğe diğerlerini düşürmesin.
        var validation = batch.Atomic
            ? $"\n            .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{request}>>()"
            : string.Empty;
        var idempotency = create
            ? IdempotencyFilter(request) + (Idempotent ? "\n            .ProducesProblem(422)" : string.Empty)
            : string.Empty;

        _map.Append($$"""
                    group.MapPost("/batch-{{operation.ToLowerInvariant()}}", Batch{{operation}}Async)
                        .WithName("Batch{{operation}}{{_plural}}"){{validation}}
                        .ProducesValidationProblem(){{idempotency}}{{PartialMetadata(batch, response)}}
                        .ProducesProblem(404)
                        .ProducesProblem(409);


            """);

        var body = batch.Atomic
            ? $$"""
                    await using var batch = await {{Batch}}.BeginAsync(context, cancellationToken);

                    for (var index = 0; index < items.Count; index++)
                    {
                        if (await {{call}} is { } problem)
                        {
                            // Dönülürken toplu işlemin tamamı geri alınır.
                            return {{Batch}}.ItemProblem(problem, index);
                        }
                    }

                    await batch.CommitAsync(cancellationToken);
                    return {{Http}}.TypedResults.Ok(new global::Haldus.BatchResult<{{response}}>(saved.Select(ToResponse).ToList()));
            """
            : $$"""
                    var results = new global::System.Collections.Generic.List<global::Haldus.BatchItemResult<{{response}}>>(items.Count);

                    for (var index = 0; index < items.Count; index++)
                    {
                        // Her öğe kendi sınırı içinde işlenir; başarısız olan yalnızca kendini geri alır.
                        await using var itemScope = await {{Batch}}.BeginAsync(context, cancellationToken);
                        var problem = {{Batch}}.Validate(items[index], messages)
                            ?? await {{call}};

                        if (problem is null)
                        {
                            await itemScope.CommitAsync(cancellationToken);
                            results.Add({{Batch}}.Succeeded(index, {{(create ? 201 : 200)}}, ToResponse(saved[saved.Count - 1])));
                        }
                        else
                        {
                            results.Add({{Batch}}.Failed<{{response}}>(index, problem));
                        }
                    }

                    return {{Http}}.TypedResults.Json(new global::Haldus.BatchResults<{{response}}>(results), statusCode: 207);
            """;

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Results<{{BatchReturn(batch, response)}}, {{Problem}}>> Batch{{operation}}Async(
            {{ScopeParameter}}        {{request}} request,
            {{time}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var items = request.Items ?? global::System.Array.Empty<{{item}}>();

            {{TooLarge("items.Count", batch)}}{{guard}}        var context = contexts.For<{{_entity}}>();
                    var saved = new global::System.Collections.Generic.List<{{_entity}}>(items.Count);

            {{body}}
                }

            """);

        if (create)
        {
            var numbering = _resource.Numbering is { AssignOn: null }
                ? """
                            // Numara, toplu işlemin açık transaction'ı içinde ayrılır ve onunla birlikte sonuçlanır.
                            await using var numbering = await global::Haldus.HaldusNumbering.AllocateAsync(context, Numbering, cancellationToken);
                            entity.Number = numbering.Number;

                    """
                : string.Empty;

            _handlers.Append($$"""

                    /// <summary>Tek bir öğeyi, tekil oluşturma ucunun kurallarıyla oluşturur ve kaydeder.</summary>
                    private static async {{Task}}<{{Problem}}?> BatchCreateItemAsync(
                {{ScopeParameter}}        {{item}} request,
                {{DbContextParameter}}{{SavedParameter}}{{time}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                {{WriteChecks(patch: false, creating: true)}}        var entity = new {{_entity}} { {{CreateInitializer()}} };
                {{CreateDetails()}}{{Compute()}}{{Derive()}}{{OrderAppend()}}{{numbering}}        context.Set<{{_entity}}>().Add(entity);
                {{Effective(SaveNew())}}{{OrderCommit()}}{{DeriveCommit()}}        saved.Add(entity);
                        return null;
                    }

                """);
        }
        else
        {
            _handlers.Append($$"""

                    /// <summary>Tek bir öğeyi, tekil güncelleme ucunun kurallarıyla tam değiştirir ve kaydeder.</summary>
                    private static async {{Task}}<{{Problem}}?> BatchUpdateItemAsync(
                {{ScopeParameter}}        {{item}} request,
                {{DbContextParameter}}{{SavedParameter}}{{time}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                        var {{_keyParameter}} = request.{{_key}};
                        var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                        if (entity is null)
                        {
                            return {{NotFound}};
                        }

                {{ChainGuard()}}{{StateGuard(_resource.Workflow?.EditableIn, "update")}}{{WriteChecks(patch: false, creating: false)}}{{CycleCheck(patch: false)}}{{OrderRemember()}}{{UpdateAssignments()}}
                {{SyncDetails(patch: false)}}{{Slide()}}{{Compute()}}{{OrderReparent()}}{{Save()}}{{OrderReparentCommit()}}        saved.Add(entity);
                        return null;
                    }

                """);
        }
    }

    private void EmitBatchDelete(BatchModel batch)
    {
        var request = $"global::Haldus.BatchIdsRequest<{_resource.IdType}>";
        var call = $"BatchDeleteItemAsync({ScopeArgument}ids[index], context, messages, cancellationToken)";

        _map.Append($$"""
                    group.MapPost("/batch-delete", BatchDeleteAsync)
                        .WithName("BatchDelete{{_plural}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{{request}}>>()
                        .ProducesValidationProblem(){{PartialMetadata(batch, _resource.IdType)}}
                        .ProducesProblem(404)
                        .ProducesProblem(409);


            """);

        var body = batch.Atomic
            ? $$"""
                    await using var batch = await {{Batch}}.BeginAsync(context, cancellationToken);

                    for (var index = 0; index < ids.Count; index++)
                    {
                        if (await {{call}} is { } problem)
                        {
                            // Dönülürken toplu işlemin tamamı geri alınır.
                            return {{Batch}}.ItemProblem(problem, index);
                        }
                    }

                    await batch.CommitAsync(cancellationToken);
                    return {{Http}}.TypedResults.NoContent();
            """
            : $$"""
                    var results = new global::System.Collections.Generic.List<global::Haldus.BatchItemResult<{{_resource.IdType}}>>(ids.Count);

                    for (var index = 0; index < ids.Count; index++)
                    {
                        // Her öğe kendi sınırı içinde işlenir; başarısız olan yalnızca kendini geri alır.
                        await using var itemScope = await {{Batch}}.BeginAsync(context, cancellationToken);

                        if (await {{call}} is { } problem)
                        {
                            results.Add({{Batch}}.Failed<{{_resource.IdType}}>(index, problem));
                        }
                        else
                        {
                            await itemScope.CommitAsync(cancellationToken);
                            results.Add({{Batch}}.Succeeded(index, 204, ids[index]));
                        }
                    }

                    return {{Http}}.TypedResults.Json(new global::Haldus.BatchResults<{{_resource.IdType}}>(results), statusCode: 207);
            """;

        var success = batch.Atomic
            ? $"{Results}.NoContent"
            : $"{Results}.JsonHttpResult<global::Haldus.BatchResults<{_resource.IdType}>>";

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Results<{{success}}, {{Problem}}>> BatchDeleteAsync(
            {{ScopeParameter}}        {{request}} request,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var ids = request.Ids.Distinct().ToList();

            {{TooLarge("ids.Count", batch)}}{{OwnerGuard("delete")}}        var context = contexts.For<{{_entity}}>();

            {{body}}
                }

                /// <summary>Tek bir kaydı, tekil silme ucunun kurallarıyla siler.</summary>
                private static async {{Task}}<{{Problem}}?> BatchDeleteItemAsync(
            {{ScopeParameter}}{{KeyParameter}}{{DbContextParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ChainDeleteGuard()}}{{StateGuard(_resource.Workflow?.DeletableIn, "delete")}}{{OrderBegin()}}        context.Set<{{_entity}}>().Remove(entity);
            {{SaveDelete()}}{{OrderCloseGap()}}{{OrderCommit()}}        return null;
                }

            """);
    }

    private static string BatchReturn(BatchModel batch, string response) => batch.Atomic
        ? $"{Results}.Ok<global::Haldus.BatchResult<{response}>>"
        : $"{Results}.JsonHttpResult<global::Haldus.BatchResults<{response}>>";

    /// <summary>Kısmi modun 207 yanıtı tipli sonuçtan çıkarsanamaz; şemaya açıkça yazılır.</summary>
    private static string PartialMetadata(BatchModel batch, string item) => batch.Atomic
        ? string.Empty
        : $"\n            .Produces<global::Haldus.BatchResults<{item}>>(207)";
}
