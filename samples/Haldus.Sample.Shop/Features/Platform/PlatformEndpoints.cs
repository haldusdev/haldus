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

        // Uygulama genelinde tek kayıt.
        app.MapSingleton<MaintenanceNotice>();

        // Kiracı başına tek kayıt.
        app.MapSingleton<StoreSettings>()
            .MultiTenant()
            .Auditable();
    }
}
