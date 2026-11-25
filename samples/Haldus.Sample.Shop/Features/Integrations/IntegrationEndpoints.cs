using Microsoft.EntityFrameworkCore;

namespace Haldus.Sample.Shop.Features.Integrations;

public static class IntegrationEndpoints
{
    /// <summary>Örnek doğrulama ucunun API anahtarını okuduğu başlık.</summary>
    public const string ApiKeyHeader = "X-Api-Key";

    public static void MapIntegrations(this IEndpointRouteBuilder app)
    {
        // Sır sunucuda üretilir (sk_…), yalnızca oluşturma ve döndürme yanıtında görünür; veritabanında özeti durur.
        // Döndürmeden sonra eski sır bir saat daha geçerlidir: POST /api-keys/{id}/rotate
        app.MapCrud<ApiKey>()
            .MultiTenant()
            .Without(CrudOperation.Update, CrudOperation.Patch)
            .SecretOnce(prefix: "sk")
            .Rotatable(overlap: TimeSpan.FromHours(1));

        // Sunulan bir anahtarın kime ait olduğu böyle bulunur. Kimlik doğrulama sırasında kiracı henüz bilinmez;
        // bu yüzden arama kiracı filtresi olmadan yapılır ve kiracıyı bulunan kayıt söyler.
        app.MapGet("/whoami", async (HttpContext http, AppDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        {
            var key = await db.ApiKeys
                .IgnoreQueryFilters([HaldusFilters.Tenant])
                .FindBySecretAsync(http.Request.Headers[ApiKeyHeader], HaldusTime.UtcNow(time), cancellationToken);

            return key is null ? Results.Unauthorized() : Results.Ok(new WhoAmI(key.Id, key.Name, key.TenantId));
        })

            // Bu ucu oturum değil sunulan anahtar tanıtır.
            .AllowAnonymous();

        // İmza sırrını sunucunun kendisi kullanır; recoverable: true onu şifreleyerek de saklar.
        // Döndürmede eski sır hemen geçersiz olur.
        var webhooks = app.MapCrud<WebhookEndpoint>()
            .MultiTenant()
            .SecretOnce(prefix: "whsec", recoverable: true)
            .Rotatable();

        webhooks.MapAction<SignPayload, PayloadSignature>();

        // Davet edilen kişinin oturumu yoktur: daveti X-Access-Token başlığındaki token ile okur
        // (GET /invitations/by-token) ve kabul eder (POST /invitations/by-token/accept). Token ile yalnızca burada
        // sayılan geçiş çalışır; iptal etmek için oturum gerekir.
        var invitations = app.MapCrud<Invitation>()
            .MultiTenant()
            .Without(CrudOperation.Update, CrudOperation.Patch, CrudOperation.Delete)
            .Workflow<InvitationStatus>(initial: InvitationStatus.Pending)
            .Expiring(TimeSpan.FromDays(7))
            .TokenAccess(prefix: "inv", "accept");

        invitations.MapTransition("accept", from: InvitationStatus.Pending, to: InvitationStatus.Accepted);
        invitations.MapTransition("revoke", from: InvitationStatus.Pending, to: InvitationStatus.Revoked);
    }
}

public sealed record WhoAmI(Guid Id, string Name, Guid TenantId);
