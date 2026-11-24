using Haldus.Generators.Model;

namespace Haldus.Generators.Emit;

/// <summary>
/// Bakiyenin komutları: düzeltme, mutlak yazma, kovalar arası taşıma ve defter. Üçü de bakiyeyi kendi kilidinin
/// altında okuyup yazar ve değişimi, bakiyeyle aynı kaydetmede deftere ekler.
/// </summary>
internal sealed partial class EndpointEmitter
{
    private void EmitBalance()
    {
        var balance = _resource.Balance!;
        var buckets = balance.Buckets;
        var perTenant = _tenant ? "true" : "false";
        var tenant = _tenant ? ", TenantId = global::Haldus.HaldusScope.TenantId(context).GetValueOrDefault()" : string.Empty;

        // Bakiye sıfırın altına düşemez; düşebildiği bildirildiyse kontrol hiç üretilmez.
        string Floor(string value, string decreasing, string bucket) => balance.AllowNegative
            ? string.Empty
            : $$"""

                    if ({{value}} < 0 && {{decreasing}})
                    {
                        return {{Problems}}.BalanceInsufficient(messages, {{bucket}}, Read(entity, {{bucket}}));
                    }

            """;

        // Anahtar adresten gelir; kolonun alabileceğinden uzun bir anahtarın bakiyesi olamaz.
        var keyGuard = $$"""
                    if ({{_keyParameter}}.Length > 200)
                    {
                        return {{NotFound}};
                    }


            """;

        string Command(string name, string request, string filter) => $$"""
                    group.MapPost("{{_keyRoute}}/{{name.ToLowerInvariant()}}", {{name}}Async)
                        .WithName("{{name}}{{_entity}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<{{request}}>>()
                        .ProducesValidationProblem()
                        .ProducesProblem(409){{filter}};


            """;

        _map.Append(Command("Adjust", "global::Haldus.BalanceAdjustment", IdempotencyFilter("global::Haldus.BalanceAdjustment")));
        _map.Append(Command("Set", "global::Haldus.BalanceSetting", IdempotencyFilter("global::Haldus.BalanceSetting")));

        if (buckets.Length > 1)
        {
            _map.Append(Command("Move", "global::Haldus.BalanceMove", IdempotencyFilter("global::Haldus.BalanceMove")));
        }

        _map.Append($$"""
                    group.MapGet("{{_keyRoute}}/entries", EntriesAsync)
                        .WithName("List{{_entity}}Entries");


            """);

        _handlers.Append($$"""

                private const string BucketNames = "{{string.Join(", ", buckets.Select(bucket => bucket.Name))}}";

                private static readonly string Ledger = typeof({{_entity}}).FullName!;

                private static async {{OkOrProblem}} AdjustAsync(
            {{KeyParameter}}        global::Haldus.BalanceAdjustment request,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{keyGuard}}        if (BucketOf(request.Bucket) is not { } bucket)
                    {
                        return {{Problems}}.BalanceBucketUnknown(messages, "bucket", request.Bucket, BucketNames);
                    }

                    if (request.Delta == 0)
                    {
                        return {{Problems}}.BalanceAmountInvalid(messages, "delta");
                    }

                    var context = contexts.For<{{_entity}}>();
                    await using var scope = await global::Haldus.HaldusBalances.BeginAsync(context, Ledger, {{_keyParameter}}, cancellationToken);
                    var entity = await FindOrStartAsync(context, {{_keyParameter}}, cancellationToken);
                    var balance = Read(entity, bucket) + request.Delta;
            {{Floor("balance", "request.Delta < 0", "bucket")}}
                    Write(entity, bucket, balance);
                    global::Haldus.HaldusBalances.Record(context, Ledger, {{perTenant}}, {{_keyParameter}}, bucket, request.Delta, balance, request.Reason);
                    await context.SaveChangesAsync(cancellationToken);
                    await scope.CommitAsync(cancellationToken);

                    return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

                private static async {{OkOrProblem}} SetAsync(
            {{KeyParameter}}        global::Haldus.BalanceSetting request,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{keyGuard}}        if (BucketOf(request.Bucket) is not { } bucket)
                    {
                        return {{Problems}}.BalanceBucketUnknown(messages, "bucket", request.Bucket, BucketNames);
                    }
            {{(balance.AllowNegative ? string.Empty : $$"""

                    if (request.Quantity < 0)
                    {
                        return {{Problems}}.BalanceAmountInvalid(messages, "quantity");
                    }

            """)}}
                    var context = contexts.For<{{_entity}}>();
                    await using var scope = await global::Haldus.HaldusBalances.BeginAsync(context, Ledger, {{_keyParameter}}, cancellationToken);
                    var entity = await FindOrStartAsync(context, {{_keyParameter}}, cancellationToken);
                    var current = Read(entity, bucket);

                    // İstemcinin gördüğü bakiye bu sırada değiştiyse mutlak değer onun üzerine yazılmaz.
                    if (request.Expected is { } expected && expected != current)
                    {
                        return {{Problems}}.BalanceChanged(messages, bucket, current);
                    }

                    if (request.Quantity != current)
                    {
                        Write(entity, bucket, request.Quantity);
                        global::Haldus.HaldusBalances.Record(
                            context, Ledger, {{perTenant}}, {{_keyParameter}}, bucket, request.Quantity - current, request.Quantity, request.Reason);
                    }

                    await context.SaveChangesAsync(cancellationToken);
                    await scope.CommitAsync(cancellationToken);

                    return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """);

        if (buckets.Length > 1)
        {
            _handlers.Append($$"""

                    private static async {{OkOrProblem}} MoveAsync(
                {{KeyParameter}}        global::Haldus.BalanceMove request,
                {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                {{keyGuard}}        if (BucketOf(request.From) is not { } from)
                        {
                            return {{Problems}}.BalanceBucketUnknown(messages, "from", request.From, BucketNames);
                        }

                        if (BucketOf(request.To) is not { } to)
                        {
                            return {{Problems}}.BalanceBucketUnknown(messages, "to", request.To, BucketNames);
                        }

                        if (from == to)
                        {
                            return {{Problems}}.BalanceMoveSame(messages);
                        }

                        if (request.Quantity <= 0)
                        {
                            return {{Problems}}.BalanceAmountInvalid(messages, "quantity");
                        }

                        var context = contexts.For<{{_entity}}>();
                        await using var scope = await global::Haldus.HaldusBalances.BeginAsync(context, Ledger, {{_keyParameter}}, cancellationToken);
                        var entity = await FindOrStartAsync(context, {{_keyParameter}}, cancellationToken);
                        var source = Read(entity, from) - request.Quantity;
                {{Floor("source", "true", "from")}}
                        var target = Read(entity, to) + request.Quantity;

                        // İki kova tek kaydetmede değişir: miktar ya ikisinde birden yer değiştirir ya da hiçbirinde.
                        Write(entity, from, source);
                        Write(entity, to, target);
                        global::Haldus.HaldusBalances.Record(context, Ledger, {{perTenant}}, {{_keyParameter}}, from, -request.Quantity, source, request.Reason);
                        global::Haldus.HaldusBalances.Record(context, Ledger, {{perTenant}}, {{_keyParameter}}, to, request.Quantity, target, request.Reason);
                        await context.SaveChangesAsync(cancellationToken);
                        await scope.CommitAsync(cancellationToken);

                        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                    }

                """);
        }

        var names = string.Concat(buckets.Select(bucket => $"        {ResourceEmitter.Literal(bucket.Name)} => {ResourceEmitter.Literal(bucket.Name)},\n"));
        var reads = string.Concat(buckets.Skip(1).Select(bucket => $"        {ResourceEmitter.Literal(bucket.Name)} => entity.{bucket.Property},\n"));
        var writes = string.Concat(buckets.Select(bucket => $$"""
                        case {{ResourceEmitter.Literal(bucket.Name)}}:
                            entity.{{bucket.Property}} = value;
                            break;

            """));

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Ok<global::Haldus.PagedResult<global::Haldus.BalanceEntry>>> EntriesAsync(
            {{KeyParameter}}        [{{Http}}.AsParameters] global::Haldus.PageRequest page,
                    string? bucket,
            {{ContextsParameter}}{{CancellationParameter}}    {
                    return {{Http}}.TypedResults.Ok(await global::Haldus.HaldusBalances.EntriesAsync(
                        contexts.For<{{_entity}}>(), Ledger, {{perTenant}}, {{_keyParameter}}, bucket, page.PageOrDefault, page.PageSizeOrDefault, cancellationToken));
                }

                /// <summary>Hiç yazılmamış bir anahtarın bakiyesi sıfırdır; ilk yazma kaydı oluşturur.</summary>
                private static async {{Task}}<{{_entity}}> FindOrStartAsync(
                    global::Microsoft.EntityFrameworkCore.DbContext context,
                    string key,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    var entity = await context.Set<{{_entity}}>().FirstOrDefaultAsync(item => item.{{_key}} == key, cancellationToken);

                    if (entity is null)
                    {
                        entity = new {{_entity}} { {{_key}} = key{{tenant}} };
                        context.Add(entity);
                    }

                    return entity;
                }

                /// <summary>İstekteki kovanın adı; verilmediyse ilk kova, tanınmıyorsa <c>null</c>.</summary>
                private static string? BucketOf(string? requested) => requested switch
                {
                    null or "" => {{ResourceEmitter.Literal(buckets[0].Name)}},
            {{names}}        _ => null,
                };

                private static decimal Read({{_entity}} entity, string bucket) => bucket switch
                {
            {{reads}}        _ => entity.{{buckets[0].Property}},
                };

                private static void Write({{_entity}} entity, string bucket, decimal value)
                {
                    switch (bucket)
                    {
            {{writes}}        }
                }

            """);
    }
}
