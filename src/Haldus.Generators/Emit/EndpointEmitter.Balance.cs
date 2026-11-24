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

        if (balance.HoldFrom is not null)
        {
            EmitBalanceHolds(balance, perTenant, keyGuard);
        }
    }

    /// <summary>
    /// Bakiyeye karşı süreli tutma. Kurallar çalışma zamanındadır (<c>HaldusBalanceHolds</c>): her işlem bakiyenin
    /// kilidi altında, kova, defter ve tutma kaydıyla birlikte tek kaydetmededir. Üretilen kod kovaların okunup
    /// yazılmasını ve bakiyenin bulunmasını ona verir.
    /// </summary>
    private void EmitBalanceHolds(BalanceModel balance, string perTenant, string keyGuard)
    {
        const string Injection = "global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions";
        const string Holds = "global::Haldus.HaldusBalanceHolds";
        const string Hold = "global::Haldus.BalanceHold";
        const string Failure = "global::Haldus.HoldFailure";

        var from = ResourceEmitter.Literal(balance.HoldFrom!);
        var to = ResourceEmitter.Literal(balance.HoldTo!);
        var allowNegative = balance.AllowNegative ? "true" : "false";
        var holdsRoute = $"{_keyRoute}/holds";

        _map.Append($$"""
                    group.MapPost("{{holdsRoute}}", HoldAsync)
                        .WithName("Hold{{_entity}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<global::Haldus.BalanceHoldRequest>>()
                        .ProducesValidationProblem()
                        .ProducesProblem(409){{IdempotencyFilter("global::Haldus.BalanceHoldRequest")}};

                    group.MapGet("{{holdsRoute}}", HoldsAsync)
                        .WithName("List{{_entity}}Holds");

                    group.MapPost("{{holdsRoute}}/{holdId:guid}/capture", CaptureHoldAsync)
                        .WithName("Capture{{_entity}}Hold")
                        .ProducesProblem(404)
                        .ProducesProblem(409);

                    group.MapPost("{{holdsRoute}}/{holdId:guid}/release", ReleaseHoldAsync)
                        .WithName("Release{{_entity}}Hold")
                        .ProducesProblem(404)
                        .ProducesProblem(409);


            """);

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Results<{{Results}}.Ok<{{Hold}}>, {{Problem}}>> HoldAsync(
            {{KeyParameter}}        global::Haldus.BalanceHoldRequest request,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{keyGuard}}        if (request.Quantity <= 0)
                    {
                        return {{Problems}}.BalanceAmountInvalid(messages, "quantity");
                    }

                    var result = await {{Holds}}.PlaceAsync<{{_entity}}>(
                        contexts.For<{{_entity}}>(),
                        Ledger,
                        {{perTenant}},
                        {{_keyParameter}},
                        {{from}},
                        {{to}},
                        request.Quantity,
                        request.ExpiresInSeconds is { } seconds ? global::System.TimeSpan.FromSeconds(seconds) : null,
                        request.Reason,
                        allowNegative: {{allowNegative}},
                        FindOrStartAsync,
                        Read,
                        Write,
                        cancellationToken);

                    if (result.Failure == {{Failure}}.Insufficient)
                    {
                        return {{Problems}}.BalanceInsufficient(messages, {{from}}, result.Balance);
                    }

                    if (result.Failure == {{Failure}}.TooMany)
                    {
                        return {{Problems}}.BalanceHoldLimit(messages);
                    }

                    return {{Http}}.TypedResults.Ok(result.Hold!);
                }

                private static async {{Task}}<{{Results}}.Ok<global::System.Collections.Generic.IReadOnlyList<{{Hold}}>>> HoldsAsync(
            {{KeyParameter}}{{ContextsParameter}}{{CancellationParameter}}    {
                    return {{Http}}.TypedResults.Ok(await {{Holds}}.ListAsync(
                        contexts.For<{{_entity}}>(), Ledger, {{perTenant}}, {{_keyParameter}}, cancellationToken));
                }

                private static {{OkOrProblem}} CaptureHoldAsync(
            {{KeyParameter}}        global::System.Guid holdId,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    =>
                    CloseHoldAsync({{_keyParameter}}, holdId, {{Holds}}.Captured, contexts, messages, cancellationToken);

                private static {{OkOrProblem}} ReleaseHoldAsync(
            {{KeyParameter}}        global::System.Guid holdId,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    =>
                    CloseHoldAsync({{_keyParameter}}, holdId, {{Holds}}.Released, contexts, messages, cancellationToken);

                private static async {{OkOrProblem}} CloseHoldAsync(
                    string {{_keyParameter}},
                    global::System.Guid holdId,
                    string outcome,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{keyGuard}}        var result = await {{Holds}}.CloseAsync<{{_entity}}>(
                        contexts.For<{{_entity}}>(), Ledger, {{perTenant}}, {{_keyParameter}}, holdId, outcome, {{from}}, {{to}},
                        allowNegative: {{allowNegative}}, FindOrStartAsync, Read, Write, cancellationToken);

                    return result.Failure switch
                    {
                        {{Failure}}.None => {{Http}}.TypedResults.Ok(ToResponse(result.Entity!)),
                        {{Failure}}.Closed => {{Problems}}.BalanceHoldClosed(messages),
                        {{Failure}}.Insufficient => {{Problems}}.BalanceInsufficient(messages, {{to}}, result.Balance),
                        _ => {{Problems}}.NotFound(messages, "Hold", holdId),
                    };
                }

                /// <summary>
                /// Süresi dolan tutmaları bırakır. Bakım çağırır; bir isteğe ait değildir, bu yüzden her tutma kendi
                /// kiracısı adına kapatılır.
                /// </summary>
                internal static async {{Task}}<int> ReleaseExpiredHoldsAsync(
                    global::System.IServiceProvider services,
                    global::System.DateTimeOffset now,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    var context = {{Injection}}.GetRequiredService<global::Haldus.IHaldusDbContextProvider>(services).For<{{_entity}}>();
                    var released = 0;
                    var failed = 0;

                    // Kapanan tutmalar "süresi dolmuş ve açık" olmaktan çıkar; kapatılamayanlar atlanarak sıradakilere
                    // geçilir. Böylece kapatılamayan birkaç tutma sıranın başını tutamaz.
                    for (var attempted = 0; attempted < {{Holds}}.MaxPerRun;)
                    {
                        var expired = await {{Holds}}.ExpiredAsync(context, Ledger, now, skip: failed, cancellationToken);

                        if (expired.Count == 0)
                        {
                            break;
                        }

                        attempted += expired.Count;

                        foreach (var hold in expired)
                        {
                            using var identity = global::Haldus.HaldusAmbient.Use(hold.TenantId, null);
                            context.ChangeTracker.Clear();

                            try
                            {
                                var result = await {{Holds}}.CloseAsync<{{_entity}}>(
                                    context, Ledger, {{perTenant}}, hold.Key, hold.Id, {{Holds}}.Expired, {{from}}, {{to}},
                                    allowNegative: {{allowNegative}}, FindOrStartAsync, Read, Write, cancellationToken);

                                if (result.Failure == {{Failure}}.None)
                                {
                                    released++;
                                }
                                else if (result.Failure != {{Failure}}.Closed)
                                {
                                    failed++;
                                }
                            }
                            catch (global::Microsoft.EntityFrameworkCore.DbUpdateException)
                            {
                                // Bakiye bu sırada başka bir istekle değişti; bir sonraki turda yeniden denenir.
                                failed++;
                            }
                        }
                    }

                    context.ChangeTracker.Clear();
                    return released;
                }

            """);
    }
}
