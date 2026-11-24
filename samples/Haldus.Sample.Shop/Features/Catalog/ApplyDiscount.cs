using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Haldus.Sample.Shop.Features.Catalog;

/// <summary><c>POST /apply-discount</c>: adı verilen önekle başlayan ürünlere indirim uygular.</summary>
public sealed record ApplyDiscountRequest
{
    [Required, MinLength(3)]
    public string NamePrefix { get; init; } = "";

    [Range(1, 90)]
    public int Percent { get; init; }
}

public sealed record ApplyDiscountResult(int ProductsChanged);

public sealed class ApplyDiscountHandler(AppDbContext db) : IActionHandler<ApplyDiscountRequest, ApplyDiscountResult>
{
    public async Task<ApplyDiscountResult> HandleAsync(ApplyDiscountRequest request, CancellationToken cancellationToken)
    {
        var factor = 1m - (request.Percent / 100m);

        var changed = await db.Products
            .Where(product => product.Name.StartsWith(request.NamePrefix))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(product => product.Price, product => product.Price * factor),
                cancellationToken);

        return new ApplyDiscountResult(changed);
    }
}
