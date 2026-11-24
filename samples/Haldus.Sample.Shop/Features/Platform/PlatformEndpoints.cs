using Microsoft.EntityFrameworkCore;

namespace Haldus.Sample.Shop.Features.Platform;

public static class PlatformEndpoints
{
    public static void MapPlatform(this IEndpointRouteBuilder app)
    {
        // Kod + ad listesi. Entity hiç yazılmadı; Haldus tamamını üretir.
        app.MapLookup<Country>();

        // Anahtarı istemci verir: PUT /feature-flags/{key}
        app.MapStore<FeatureFlag>();

        // Aynı anahtar her kiracıda ayrı tutulur.
        app.MapStore<Preference>()
            .MultiTenant();

        // Fiyat gömülü bir değer nesnesidir: { "price": { "amount": 99, "currency": "TRY" } }
        app.MapCrud<Plan>()
            .Embeds<Money>();

        // Uygulama genelinde tek kayıt.
        app.MapSingleton<MaintenanceNotice>();

        // Kiracı başına tek kayıt.
        app.MapSingleton<StoreSettings>()
            .MultiTenant()
            .Auditable();

        // Kısa bir iş: mantığı lambda'dadır ve bağımlılıklarını işin kapsamından alır.
        app.MapJob<CatalogRecount>(async (job, context, cancellationToken) =>
        {
            var db = context.Services.GetRequiredService<AppDbContext>();
            job.Record(await db.Products.CountAsync(cancellationToken));
        });
    }
}
