using System.ComponentModel.DataAnnotations;
using Haldus.Sample.Modular.Catalog;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus.Sample.Modular.Sales;

/// <summary>Satış modülünün kendi bağlamı; tabloları <c>sales</c> şemasındadır.</summary>
public partial class SalesDbContext(DbContextOptions<SalesDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.HasDefaultSchema("sales");
}

public partial class Order
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";
}

public static class SalesModule
{
    public static IServiceCollection AddSales(this IServiceCollection services)
    {
        services.AddDbContext<SalesDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("Default")));

        return services.AddHaldusModule<SalesDbContext>();
    }

    public static void MapSales(this IEndpointRouteBuilder app)
    {
        // Ürün başka bir modülün, başka bir bağlamın entity'sidir. Sipariş ona yalnızca kimliğiyle bağlanır:
        // veritabanında yabancı anahtar kısıtı olmaz, ama ürünün varlığı yazarken katalog bağlamında doğrulanır.
        // Numara sayacı bu modülün kendi şemasında tutulur.
        app.MapCrud<Order>()
            .References<Product>()
            .Numbered("S-{seq:4}");
    }
}