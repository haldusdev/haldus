using Haldus.Generators.Model;

namespace Haldus.Generators.Emit;

/// <summary>
/// Yinelenen kaydın oluşları. Oluşlar saklanmaz: kuraldan hesaplanır (<c>HaldusRecurrence</c>) ve tek tek ertelenip iptal
/// edilebilir. Uçlar kaydı her zaman kendi filtrelerinden geçirerek bulur; kaydı göremeyen oluşlarını da göremez.
/// Tek bir oluşu değiştirmek serinin bir yazmasıdır: güncelleme ucunun korumalarından (sürüm, sahibin durumu, kapanmış
/// veya onaylanmış kayıt, düzenlenebilir durum) geçer, seriyle aynı kaydetmede kalıcı olur ve serinin sürümünü ilerletir.
/// </summary>
internal sealed partial class EndpointEmitter
{
    private void EmitRecurrence()
    {
        var occurrence = "global::Haldus.Occurrence";
        var route = $"{_keyRoute}/occurrences";
        var metadata = $"{StateMetadata(_resource.Workflow?.EditableIn)}{ConcurrencyMetadata()}";

        _map.Append($$"""
                    group.MapGet("{{route}}", OccurrencesAsync)
                        .WithName("List{{_entity}}Occurrences")
                        .ProducesProblem(404);

                    group.MapPut("{{route}}/{start}", ChangeOccurrenceAsync)
                        .WithName("Change{{_entity}}Occurrence")
                        .ProducesProblem(404){{metadata}};

                    group.MapDelete("{{route}}/{start}", RestoreOccurrenceAsync)
                        .WithName("Restore{{_entity}}Occurrence")
                        .ProducesProblem(404){{metadata}};


            """);

        string Change(string name, string parameters, string change) => $$"""

                private static async {{Task}}<{{Results}}.Results<{{Results}}.Ok<{{occurrence}}>, {{Problem}}>> {{name}}(
            {{ScopeParameter}}{{KeyParameter}}        global::System.DateTimeOffset start,
            {{parameters}}{{(_concurrency ? HttpContextParameter : string.Empty)}}{{(Sliding ? TimeProviderParameter : string.Empty)}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}{{OwnerGuard("update")}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ChainGuard()}}{{StateGuard(_resource.Workflow?.EditableIn, "update")}}{{ExpectVersion()}}        // Yalnızca kuralın gerçekten verdiği bir oluş ertelenebilir veya iptal edilebilir.
                    var changed = await global::Haldus.HaldusRecurrence.ChangeAsync(context, entity, {{KeyText}}, start, {{change}}, cancellationToken);

                    if (changed is null)
                    {
                        return {{Problems}}.NotFound(messages, "Occurrence", start);
                    }

                    // Oluşun değişmesi serinin değişmesidir: sürümü ilerler ve seriye bağlı her kural (onay, sürümler) işler.
                    context.Entry(entity).State = global::Microsoft.EntityFrameworkCore.EntityState.Modified;
            {{Slide()}}{{Save()}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(changed);
                }

            """;

        _handlers.Append($$"""

                /// <summary>Serinin verilen aralıktaki oluşları; aralık verilmezse ilk oluştan başlayarak bir yıl.</summary>
                private static async {{Task}}<{{Results}}.Results<{{Results}}.Ok<global::System.Collections.Generic.List<{{occurrence}}>>, {{Problem}}>> OccurrencesAsync(
            {{ScopeParameter}}{{KeyParameter}}        global::System.DateTimeOffset? from,
                    global::System.DateTimeOffset? to,
            {{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
                    var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>().AsNoTracking().FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

                    var first = from ?? entity.StartsAt;
                    var last = to ?? first.AddYears(1);

                    return {{Http}}.TypedResults.Ok(
                        await global::Haldus.HaldusRecurrence.OccurrencesAsync(context, entity, {{KeyText}}, first, last, cancellationToken));
                }

            """);

        _handlers.Append(Change("ChangeOccurrenceAsync", "        global::Haldus.OccurrenceChange request,\n", "request"));
        _handlers.Append(Change("RestoreOccurrenceAsync", string.Empty, "new global::Haldus.OccurrenceChange()"));
    }
}
