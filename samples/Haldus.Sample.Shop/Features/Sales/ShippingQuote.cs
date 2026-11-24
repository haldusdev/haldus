using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Haldus.Sample.Shop.Features.Sales;

/// <summary><c>GET /shipping-quote?country=TR&amp;weight=2.5</c> isteğinin sorgu parametreleri.</summary>
public sealed class ShippingQuoteRequest
{
    [Required, StringLength(8, MinimumLength = 2)]
    public string? Country { get; set; }

    [Range(0.01, 1000.0)]
    public decimal Weight { get; set; } = 1m;

    public bool Express { get; set; }
}

public sealed record ShippingQuote(decimal Price, bool CountryKnown);

/// <summary>
/// Hesabın kodu. Haldus bu sınıfı kendisi bulur ve kaydeder; bağımlılıkları constructor'dan gelir.
/// </summary>
public sealed class ShippingQuoteHandler(AppDbContext db) : IQueryHandler<ShippingQuoteRequest, ShippingQuote>
{
    public async Task<ShippingQuote> HandleAsync(ShippingQuoteRequest request, CancellationToken cancellationToken)
    {
        var countryKnown = await db.Countries.AnyAsync(country => country.Code == request.Country, cancellationToken);
        var price = 10m + (request.Weight * (countryKnown ? 2m : 5m));

        return new ShippingQuote(request.Express ? price * 2 : price, countryKnown);
    }
}
