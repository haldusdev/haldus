using Haldus.Generators.Model;

namespace Haldus.Generators.Emit;

/// <summary>
/// Paylaşılan kaydın kullanıcı başına okundu bilgisi (<c>.ReadReceipts()</c>). Bilgi kayıtta değil ayrı bir tabloda
/// durur (<c>HaldusReadReceipts</c>); bu yüzden kaydın kendisi yazılmaz: sürümü ilerlemez, onayı düşmez, düzenleme
/// kurallarına bakılmaz. Uçlar kaydı her zaman kendi filtrelerinden geçirerek bulur ve kullanıcısı çözülemeyen
/// isteği reddeder.
/// </summary>
internal sealed partial class EndpointEmitter
{
    /// <summary>Kaydın anahtarının metin hali; işaretler kayıtlarla bununla eşleşir.</summary>
    private string ReceiptKey => _resource.IdKind == IdKind.String
        ? $"item => item.{_key}"
        : $"item => item.{_key}.ToString()";

    private void EmitReadReceipts()
    {
        const string UserFilter = ".AddEndpointFilter<global::Haldus.HaldusUserRequiredFilter>()";
        const string State = "global::Haldus.ReadState";

        var archived = _resource.Has(Traits.Archivable) ? ".Where(item => item.ArchivedAt == null)" : string.Empty;

        _map.Append($$"""
                    group.MapPost("{{_keyRoute}}/read", MarkReadAsync)
                        .WithName("MarkRead{{_entity}}")
                        {{UserFilter}}
                        .ProducesProblem(401)
                        .ProducesProblem(404);

                    group.MapPost("{{_keyRoute}}/unread", MarkUnreadAsync)
                        .WithName("MarkUnread{{_entity}}")
                        {{UserFilter}}
                        .ProducesProblem(401)
                        .ProducesProblem(404);

                    group.MapGet("{{_keyRoute}}/read-state", ReadStateAsync)
                        .WithName("Get{{_entity}}ReadState")
                        {{UserFilter}}
                        .ProducesProblem(401)
                        .ProducesProblem(404);

                    group.MapPost("/read-all", ReadAllAsync)
                        .WithName("ReadAll{{_plural}}")
                        {{UserFilter}}
                        .ProducesProblem(401);

                    group.MapGet("/unread-count", UnreadCountAsync)
                        .WithName("CountUnread{{_plural}}")
                        {{UserFilter}}
                        .ProducesProblem(401);


            """);

        string Single(string name, string result, string action) => $$"""

                private static async {{Task}}<{{Results}}.Results<{{result}}, {{Problem}}>> {{name}}(
            {{ScopeParameter}}{{KeyParameter}}        global::System.TimeProvider timeProvider,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var context = contexts.For<{{_entity}}>();

                    if (global::Haldus.HaldusScope.UserId(context) is not { Length: > 0 } userId)
                    {
                        return {{Problems}}.UserRequired(messages);
                    }

                    // Kaydı göremeyen, okundu bilgisini de ne okuyabilir ne yazabilir.
                    if (!await context.Set<{{_entity}}>().AsNoTracking().AnyAsync(item => {{Match}}, cancellationToken))
                    {
                        return {{NotFound}};
                    }

            {{action}}
                }

            """;

        _handlers.Append(Single(
            "MarkReadAsync",
            $"{Results}.NoContent",
            $$"""
                    await global::Haldus.HaldusReadReceipts.MarkAsync<{{_entity}}>(context, {{KeyText}}, userId, global::Haldus.HaldusTime.UtcNow(timeProvider), cancellationToken);
                    return {{Http}}.TypedResults.NoContent();
            """));

        _handlers.Append(Single(
            "MarkUnreadAsync",
            $"{Results}.NoContent",
            $$"""
                    await global::Haldus.HaldusReadReceipts.UnmarkAsync<{{_entity}}>(context, {{KeyText}}, userId, cancellationToken);
                    return {{Http}}.TypedResults.NoContent();
            """));

        _handlers.Append(Single(
            "ReadStateAsync",
            $"{Results}.Ok<{State}>",
            $$"""
                    return {{Http}}.TypedResults.Ok(await global::Haldus.HaldusReadReceipts.StateAsync<{{_entity}}>(context, {{KeyText}}, userId, cancellationToken));
            """));

        _handlers.Append($$"""

                private static async {{Task}}<{{Results}}.Results<{{Results}}.Ok<global::Haldus.ReadAllResult>, {{Problem}}>> ReadAllAsync(
            {{ScopeParameter}}        global::System.TimeProvider timeProvider,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var context = contexts.For<{{_entity}}>();

                    if (global::Haldus.HaldusScope.UserId(context) is not { Length: > 0 } userId)
                    {
                        return {{Problems}}.UserRequired(messages);
                    }

                    // Yalnızca isteği yapanın görebildiği kayıtlar işaretlenir.
                    var visible = context.Set<{{_entity}}>().AsNoTracking(){{ScopeFilter}};
                    var updated = await global::Haldus.HaldusReadReceipts.MarkAllAsync(
                        context, visible, {{ReceiptKey}}, userId, global::Haldus.HaldusTime.UtcNow(timeProvider), cancellationToken);

                    return {{Http}}.TypedResults.Ok(new global::Haldus.ReadAllResult(updated));
                }

                private static async {{Task}}<{{Results}}.Results<{{Results}}.Ok<global::Haldus.UnreadCount>, {{Problem}}>> UnreadCountAsync(
            {{ScopeParameter}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var context = contexts.For<{{_entity}}>();

                    if (global::Haldus.HaldusScope.UserId(context) is not { Length: > 0 } userId)
                    {
                        return {{Problems}}.UserRequired(messages);
                    }

                    var visible = context.Set<{{_entity}}>().AsNoTracking(){{ScopeFilter}}{{archived}};
                    var count = await global::Haldus.HaldusReadReceipts.Filter(context, visible, {{ReceiptKey}}, userId, unread: true)
                        .LongCountAsync(cancellationToken);

                    return {{Http}}.TypedResults.Ok(new global::Haldus.UnreadCount(count));
                }

            """);
    }
}
