using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus.Sample.Modular.Catalog;

/// <summary>
/// Katalog modülü kendi bağlamına sahiptir: tabloları <c>catalog</c> şemasında durur ve başka hiçbir modülün
/// bağlamında yer almaz. partial: Haldus, bu projede üretilen entity'lerin DbSet özelliklerini ekler.
/// </summary>
public partial class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.HasDefaultSchema("catalog");
}

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [Range(0.0, 1_000_000.0)]
    public decimal Price { get; set; }
}

/// <summary><c>GET /price-check</c>: bir ürünün verilen adetteki tutarı.</summary>
public sealed class PriceCheckRequest
{
    public Guid ProductId { get; set; }

    [Range(1, 1000)]
    public int Quantity { get; set; } = 1;
}

public sealed record PriceCheck(bool Found, decimal Total);

/// <summary>Handler modülün içindedir; Haldus onu, modül henüz hiç kullanılmamışken de bulup kaydeder.</summary>
public sealed class PriceCheckHandler(CatalogDbContext db) : IQueryHandler<PriceCheckRequest, PriceCheck>
{
    public async Task<PriceCheck> HandleAsync(PriceCheckRequest request, CancellationToken cancellationToken)
    {
        var price = await db.Products
            .Where(product => product.Id == request.ProductId)
            .Select(product => (decimal?)product.Price)
            .FirstOrDefaultAsync(cancellationToken);

        return new PriceCheck(price is not null, (price ?? 0) * request.Quantity);
    }
}

public static class CatalogModule
{
    public static IServiceCollection AddCatalog(this IServiceCollection services)
    {
        services.AddDbContext<CatalogDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("Default")));

        // Bu assembly'de üretilen entity'ler yalnızca CatalogDbContext'in modeline girer.
        return services.AddHaldusModule<CatalogDbContext>();
    }

    public static void MapCatalog(this IEndpointRouteBuilder app)
    {
        app.MapCrud<Product>()
            .Idempotent();

        app.MapQuery<PriceCheckRequest, PriceCheck>();
    }
}