using Haldus.Generators.Model;

namespace Haldus.Generators.Emit;

/// <summary>
/// Zamanlanmış yayın (<c>.ScheduledPublishing()</c>). Taslağa bir yayın zamanı konur (<c>ScheduledAt</c>); zamanı
/// gelince bakım (<c>HaldusMaintenance</c>) taslağı, elle yayınla aynı parçayı kullanarak yayına alır. Zamanlamak
/// taslağın bir yazmasıdır: sürüm ve sahiplik kurallarından geçer.
/// </summary>
internal sealed partial class EndpointEmitter
{
    private void EmitScheduledPublishing(string copy)
    {
        const string Injection = "global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions";

        _map.Append($$"""
                    group.MapPost("{{_keyRoute}}/schedule", ScheduleAsync)
                        .WithName("Schedule{{_entity}}")
                        .AddEndpointFilter<global::Haldus.HaldusValidationFilter<global::Haldus.PublicationSchedule>>()
                        .ProducesValidationProblem()
                        .ProducesProblem(404){{ConcurrencyMetadata()}};

                    group.MapDelete("{{_keyRoute}}/schedule", UnscheduleAsync)
                        .WithName("Unschedule{{_entity}}")
                        .ProducesProblem(404){{ConcurrencyMetadata()}};


            """);

        string Handler(string name, string parameter, string statement) => $$"""

                private static async {{OkOrProblem}} {{name}}(
            {{ScopeParameter}}{{KeyParameter}}{{parameter}}{{HttpContextParameter}}{{ContextsParameter}}{{MessagesParameter}}{{CancellationParameter}}    {
            {{ReadIfMatch()}}{{OwnerGuard("schedule")}}        var context = contexts.For<{{_entity}}>();
                    var entity = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(item => {{Match}}, cancellationToken);

                    if (entity is null)
                    {
                        return {{NotFound}};
                    }

            {{ExpectVersion()}}        {{statement}}
            {{Save()}}
            {{WriteETag()}}        return {{Http}}.TypedResults.Ok(ToResponse(entity));
                }

            """;

        // Geçmiş bir zaman da yazılabilir: taslak bakımın bir sonraki turunda yayınlanır.
        _handlers.Append(Handler("ScheduleAsync", "        global::Haldus.PublicationSchedule request,\n", "entity.ScheduledAt = request.PublishAt;"));
        _handlers.Append(Handler("UnscheduleAsync", string.Empty, "entity.ScheduledAt = null;"));

        _handlers.Append($$"""

                /// <summary>
                /// Zamanı gelen taslakları yayına alır. Bakım çağırır; bir isteğe ait değildir, bu yüzden her taslak kendi
                /// kiracısı ve sahibi adına yayınlanır.
                /// </summary>
                internal static async {{Task}}<int> PublishDueAsync(
                    global::System.IServiceProvider services,
                    global::System.DateTimeOffset now,
                    global::System.Threading.CancellationToken cancellationToken)
                {
                    var contexts = {{Injection}}.GetRequiredService<global::Haldus.IHaldusDbContextProvider>(services);
                    var context = contexts.For<{{_entity}}>();
                    var due = await global::Haldus.HaldusScheduledPublishing.DueAsync<{{_entity}}>(
                        context,
                        candidate => candidate.PublishedOfId == null && candidate.ScheduledAt != null && candidate.ScheduledAt <= now,
                        candidate => candidate.ScheduledAt,
                        cancellationToken);
                    var published = 0;

                    foreach (var scheduled in due)
                    {
                        using var identity = global::Haldus.HaldusAmbient.Use(
                            (scheduled as global::Haldus.IMultiTenant)?.TenantId,
                            (scheduled as global::Haldus.IOwned)?.OwnerId);

                        context.ChangeTracker.Clear();
                        var source = await context.Set<{{_entity}}>(){{Includes}}.FirstOrDefaultAsync(candidate => candidate.{{_key}} == scheduled.{{_key}}, cancellationToken);

                        // Bu sırada silinmiş veya zamanlaması kaldırılmış olabilir.
                        if (source is null || source.ScheduledAt is null || source.ScheduledAt > now)
                        {
                            continue;
                        }

            {{copy}}        source.ScheduledAt = null;

                        try
                        {
                            await context.SaveChangesAsync(cancellationToken);
                            published++;
                        }
                        catch (global::Microsoft.EntityFrameworkCore.DbUpdateException)
                        {
                            // Taslak bu sırada başka bir istekle değişti veya yayınlandı; bir sonraki turda yeniden denenir.
                        }
                    }

                    context.ChangeTracker.Clear();
                    return published;
                }

            """);
    }
}