using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Haldus.Sample.Shop.Features.Platform;

public sealed class EchoRequest
{
    [MaxLength(50)]
    public string? Message { get; set; }

    public int[] Numbers { get; set; } = [];
}

public sealed record EchoResponse(string Message, int Sum);

/// <summary>Gövdesi boş bir komut: bakım duyurusunu kapatır.</summary>
public sealed record ClearMaintenanceNotice;

public static class DiagnosticsEndpoints
{
    public static void MapDiagnostics(this IEndpointRouteBuilder app)
    {
        // Kısa işler için handler sınıfı yazmak yerine lambda verilebilir.
        app.MapQuery<EchoRequest, EchoResponse>((request, _) =>
            Task.FromResult(new EchoResponse(request.Message ?? "pong", request.Numbers.Sum())));

        // Bağımlılık gerekiyorsa lambda onu isteğin servis sağlayıcısından alır.
        app.MapAction<ClearMaintenanceNotice>(async (_, services, cancellationToken) =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            await db.MaintenanceNotice.ExecuteUpdateAsync(
                setters => setters.SetProperty(notice => notice.Enabled, false), cancellationToken);
        });
    }
}
