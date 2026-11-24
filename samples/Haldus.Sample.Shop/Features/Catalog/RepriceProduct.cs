using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Catalog;

/// <summary><c>POST /products/{id}/reprice</c>: ürünün fiyatını yüzdeyle değiştirir ve eski ile yeni fiyatı döndürür.</summary>
public sealed record RepriceProduct
{
    [Range(-90, 200)]
    public int Percent { get; init; }
}

public sealed record RepriceResult(decimal OldPrice, decimal NewPrice);

/// <summary>
/// Handler ürünü değiştirir ve sonucu üretir; ürünü bulmak ve kaydetmek Haldus'un işidir. Bağımlılıklarını
/// constructor'dan alır.
/// </summary>
public sealed class RepriceProductHandler(ILogger<RepriceProductHandler> logger)
    : IEntityActionHandler<Product, RepriceProduct, RepriceResult>
{
    public Task<RepriceResult> HandleAsync(Product entity, RepriceProduct request, CancellationToken cancellationToken)
    {
        var oldPrice = entity.Price;
        entity.Price = Math.Round(oldPrice * (100 + request.Percent) / 100m, 2);

        logger.LogInformation("Product {ProductId} was repriced by {Percent} percent.", entity.Id, request.Percent);
        return Task.FromResult(new RepriceResult(oldPrice, entity.Price));
    }
}