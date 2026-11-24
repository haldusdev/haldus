namespace Haldus.Generators.Tests;

/// <summary>Üretilen kodun tamamını onaylı kopyalarla karşılaştırır; biçimdeki istenmeyen değişiklikleri yakalar.</summary>
public sealed class SnapshotTests
{
    [Fact]
    public void PlainCrud()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop.Catalog
            {
                public partial class Product
                {
                    [MaxLength(200)]
                    public string Name { get; set; } = "";

                    [Range(0.0, 1_000_000.0)]
                    public decimal Price { get; set; }

                    public string? Description { get; set; }

                    public int Stock { get; private set; }
                }

                public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);

                public static class Startup
                {
                    public static void Configure(IServiceCollection services) => services.AddHaldus<AppDbContext>();

                    public static void Map(IEndpointRouteBuilder app) => app.MapCrud<Product>();
                }
            }
            """).AssertCompiles();

        Snapshot.Verify(run);
    }

    [Fact]
    public void CrudWithAllTraits()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop.Sales
            {
                public partial class Customer
                {
                    [MaxLength(200)]
                    public string Name { get; set; } = "";

                    [EmailAddress]
                    public string? Email { get; set; }

                    public int LoyaltyPoints { get; set; }
                }

                public static class Endpoints
                {
                    public static void Map(IEndpointRouteBuilder app) =>
                        app.MapCrud<Customer>()
                            .MultiTenant()
                            .Auditable(trackUser: true)
                            .SoftDeletable()
                            .ConcurrencyChecked()
                            .Archivable();
                }
            }
            """).AssertCompiles();

        Snapshot.Verify(run);
    }
}
