namespace Haldus.Generators.Tests;

public sealed class OwnershipTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum OrderStatus { Draft, Placed }

            public partial class Product
            {
                public string Name { get; set; } = "";
            }

            public partial class Cart
            {
                public string? Note { get; set; }
            }

            public partial class CartLine
            {
                public int Quantity { get; set; }
            }

            public partial class Note
            {
                public string Text { get; set; } = "";
            }

            public partial class Order
            {
                public string? Remark { get; set; }
            }

            public partial class OrderItem
            {
                public int Quantity { get; set; }
            }

            public partial class Tag
            {
                public string Name { get; set; } = "";
            }

            public partial class Currency;

            public partial class Settings
            {
                public string Theme { get; set; } = "";
            }

            public partial class Export
            {
                public int Year { get; set; }
            }

            public sealed class ExportHandler : IJobHandler<Export>
            {
                public System.Threading.Tasks.Task HandleAsync(Export job, JobContext context, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.Task.CompletedTask;
            }

            public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
        }
        """;

    private static GeneratorRun Run(string body) =>
        GeneratorHarness.Run($$"""
            {{Entities}}

            namespace Shop
            {
                public static class Startup
                {
                    public static void Configure(IServiceCollection services) => services.AddHaldus<AppDbContext>();

                    public static void Map(IEndpointRouteBuilder app)
                    {
                        {{body}}
                    }
                }
            }
            """);

    [Fact]
    public void Endpoints_CarryTheScopeOfTheirResourceAsMetadata()
    {
        var run = Run("""
            app.MapCrud<Product>();
            app.MapCrud<Tag>();
            app.MapCrud<Order>().MultiTenant();
            app.MapCrud<Note>().Under<Order>();
            app.MapCrud<Currency>().Under<Product>();
            app.MapCrud<Cart>().OwnedByCurrentUser();
            app.MapCrud<CartLine>().Under<Cart>();
            app.MapCrud<Draft>().MultiTenant().OwnedByCurrentUser();
            app.MapAssociation<Product, Tag>();
            app.MapAssociation<Favorite, Product>().ForCurrentUser();
            """).AssertCompiles();

        // Uygulama yetki kurallarını bu metadataya göre yazabilir (ör. ortak kaynakları yalnızca personel yazar).
        // Kapsam kaynağın kendisinden veya ebeveyninden gelir: (kiracıya ait, kullanıcıya ait).
        static string Scope(bool tenant, bool owner) =>
            $"new global::Haldus.HaldusResourceScope({(tenant ? "true" : "false")}, {(owner ? "true" : "false")})";

        Assert.Contains(Scope(tenant: false, owner: false), run.Sources["Shop.Product.Endpoints.g.cs"]);
        Assert.Contains(Scope(tenant: true, owner: false), run.Sources["Shop.Order.Endpoints.g.cs"]);
        Assert.Contains(Scope(tenant: true, owner: false), run.Sources["Shop.Note.Endpoints.g.cs"]);
        Assert.Contains(Scope(tenant: false, owner: false), run.Sources["Shop.Currency.Endpoints.g.cs"]);
        Assert.Contains(Scope(tenant: false, owner: true), run.Sources["Shop.Cart.Endpoints.g.cs"]);
        Assert.Contains(Scope(tenant: false, owner: true), run.Sources["Shop.CartLine.Endpoints.g.cs"]);
        Assert.Contains(Scope(tenant: true, owner: true), run.Sources["Shop.Draft.Endpoints.g.cs"]);
        Assert.Contains(Scope(tenant: false, owner: true), run.Sources["Shop.Favorite.Endpoints.g.cs"]);
        Assert.Contains(Scope(tenant: false, owner: false), run.Sources.Single(source => source.Key.Contains("ProductTag") && source.Key.Contains("Endpoints")).Value);
    }
    [Fact]
    public void Owned_AddsAnOwnerThatIsNeitherRequestedNorReturned()
    {
        var run = Run("app.MapCrud<Cart>().OwnedByCurrentUser();").AssertCompiles();

        var entity = run.Sources["Shop.Cart.Entity.g.cs"];
        Assert.Contains("partial class Cart : global::Haldus.IOwned", entity);
        Assert.Contains("public string OwnerId { get; set; } = \"\";", entity);

        // Sahip isteği yapanın kendisidir: ne istekte verilir ne yanıtta yinelenir.
        Assert.DoesNotContain("OwnerId", run.Sources["Shop.Cart.Dtos.g.cs"]);
        Assert.DoesNotContain("OwnerId", run.Sources["Shop.Cart.Endpoints.g.cs"]);
    }

    [Fact]
    public void Owned_OwnerWrittenByTheUser_IsStillNotWritableThroughTheApi()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop
            {
                public partial class Draft
                {
                    public string OwnerId { get; set; } = "";
                    public string Text { get; set; } = "";
                }

                public static class Startup
                {
                    public static void Map(IEndpointRouteBuilder app) => app.MapCrud<Draft>().OwnedByCurrentUser();
                }
            }
            """).AssertCompiles();

        Assert.DoesNotContain("OwnerId", run.Sources["Shop.Draft.Dtos.g.cs"]);
        Assert.DoesNotContain("OwnerId", run.Sources["Shop.Draft.Entity.g.cs"]);
    }

    [Fact]
    public void Owned_IsFilteredInTheModelSoEveryQueryIsCovered()
    {
        var module = Run("app.MapCrud<Cart>().OwnedByCurrentUser();").AssertCompiles().Sources["HaldusModule.g.cs"];

        Assert.Contains(
            "entity.HasQueryFilter(global::Haldus.HaldusFilters.Owner, item => item.OwnerId == global::Haldus.HaldusScope.UserId(context));",
            module);
        Assert.Contains("entity.Property(item => item.OwnerId).HasMaxLength(200);", module);
        Assert.Contains("entity.HasIndex(item => item.OwnerId);", module);
    }

    [Fact]
    public void Owned_LivesUnderMeAndRejectsRequestsWithoutAUser()
    {
        var endpoints = Run("app.MapCrud<Cart>().OwnedByCurrentUser();").AssertCompiles().Sources["Shop.Cart.Endpoints.g.cs"];

        Assert.Contains("""endpoints.MapGroup("/me/carts")""", endpoints);
        Assert.Contains("group.AddEndpointFilter<global::Haldus.HaldusUserRequiredFilter>();", endpoints);
    }

    [Fact]
    public void Owned_WithUnderMeOff_KeepsTheAddress()
    {
        var endpoints = Run("app.MapCrud<Note>().OwnedByCurrentUser(underMe: false);").AssertCompiles().Sources["Shop.Note.Endpoints.g.cs"];

        Assert.Contains("""endpoints.MapGroup("/notes")""", endpoints);

        // Adres değişmese de kayıtlar bir kullanıcıya aittir; kullanıcısız istek yine reddedilir.
        Assert.Contains("group.AddEndpointFilter<global::Haldus.HaldusUserRequiredFilter>();", endpoints);
    }

    [Fact]
    public void Owned_CombinesWithTenancy()
    {
        var run = Run("app.MapCrud<Cart>().MultiTenant().OwnedByCurrentUser();").AssertCompiles();

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.HasQueryFilter(global::Haldus.HaldusFilters.Tenant,", module);
        Assert.Contains("entity.HasQueryFilter(global::Haldus.HaldusFilters.Owner,", module);

        var endpoints = run.Sources["Shop.Cart.Endpoints.g.cs"];
        Assert.Contains("group.AddEndpointFilter<global::Haldus.HaldusTenantRequiredFilter>();", endpoints);
        Assert.Contains("group.AddEndpointFilter<global::Haldus.HaldusUserRequiredFilter>();", endpoints);
    }

    [Fact]
    public void ResourceUnderAnOwnedParent_FollowsItsAddress()
    {
        var run = Run("""
            app.MapCrud<Cart>().OwnedByCurrentUser();
            app.MapCrud<CartLine>().Under<Cart>();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.CartLine.Endpoints.g.cs"];
        Assert.Contains("""endpoints.MapGroup("/me/carts/{cartId:guid}/cart-lines")""", endpoints);
        Assert.Contains("group.AddEndpointFilter<global::Haldus.HaldusUserRequiredFilter>();", endpoints);

        // Alt kaydın kendi sahibi yoktur; görünürlüğü ebeveyninden gelir.
        Assert.DoesNotContain("IOwned", run.Sources["Shop.CartLine.Entity.g.cs"]);
        Assert.Contains("entity.HasQueryFilter(global::Haldus.HaldusFilters.Parent,", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Owned_UnderAParent_NeedsThePlainAddress()
    {
        var plain = Run("""
            app.MapCrud<Product>();
            app.MapCrud<Note>().Under<Product>().OwnedByCurrentUser(underMe: false);
            """).AssertCompiles();

        Assert.Contains("""endpoints.MapGroup("/products/{productId:guid}/notes")""", plain.Sources["Shop.Note.Endpoints.g.cs"]);

        var underMe = Run("""
            app.MapCrud<Product>();
            app.MapCrud<Note>().Under<Product>().OwnedByCurrentUser();
            """);

        var diagnostic = Assert.Single(underMe.GeneratorDiagnostics);
        Assert.Equal("HALDUS007", diagnostic.Id);
        Assert.Contains("pass underMe: false", diagnostic.GetMessage());
    }

    [Fact]
    public void Owned_Job_IsOwnedLikeAnyOtherRecord()
    {
        var run = Run("app.MapJob<Export>().OwnedByCurrentUser();").AssertCompiles();

        Assert.Contains("""endpoints.MapGroup("/me/exports")""", run.Sources["Shop.Export.Endpoints.g.cs"]);
        Assert.Contains("global::Haldus.IOwned", run.Sources["Shop.Export.Entity.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapSingleton<Settings>().OwnedByCurrentUser();", "MapSingleton")]
    [InlineData("app.MapStore<Settings>().OwnedByCurrentUser();", "MapStore")]
    [InlineData("app.MapLookup<Currency>().OwnedByCurrentUser();", "MapLookup")]
    public void Owned_IsOnlyForArchetypesWhoseKeyTheServerGenerates(string mapCall, string archetype)
    {
        var diagnostic = Assert.Single(Run(mapCall).GeneratorDiagnostics);

        Assert.Equal("HALDUS007", diagnostic.Id);
        Assert.Contains($"OwnedByCurrentUser cannot be combined with {archetype}", diagnostic.GetMessage());
    }

    [Fact]
    public void ResourceUnderMe_CannotBeAnEndOfAnAssociation()
    {
        var run = Run("""
            app.MapCrud<Cart>().OwnedByCurrentUser();
            app.MapCrud<Tag>();
            app.MapAssociation<Cart, Tag>();
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("HALDUS007", diagnostic.Id);
        Assert.Contains("'Cart' lives under /me", diagnostic.GetMessage());
    }

    [Fact]
    public void ForCurrentUser_GeneratesALinkKeyedByTheUserAndTheRecord()
    {
        var run = Run("""
            app.MapCrud<Product>();
            app.MapAssociation<Favorite, Product>().ForCurrentUser();
            """).AssertCompiles();

        var entity = run.Sources["Shop.Favorite.Entity.g.cs"];
        Assert.Contains("public partial class Favorite : global::Haldus.IOwned", entity);
        Assert.Contains("public string OwnerId { get; set; } = \"\";", entity);
        Assert.Contains("public global::System.Guid ProductId { get; set; }", entity);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.ToTable(\"Favorites\");", module);
        Assert.Contains("entity.HasKey(item => new { item.OwnerId, item.ProductId });", module);
        Assert.Contains(
            "global::Haldus.HaldusEndpointRegistry.RegisterAssociation<global::Shop.Favorite, global::Shop.Product>(global::Shop.FavoriteEndpoints.Map);",
            module);

        Assert.Contains("DbSet<global::Shop.Favorite> Favorites", run.Sources["AppDbContext.DbSets.g.cs"]);
    }

    [Fact]
    public void ForCurrentUser_MapsTheLinkUnderMeWithoutTheUserInTheAddress()
    {
        var endpoints = Run("""
            app.MapCrud<Product>();
            app.MapAssociation<Favorite, Product>().ForCurrentUser();
            """).AssertCompiles().Sources["Shop.Favorite.Endpoints.g.cs"];

        Assert.Contains("""endpoints.MapGroup("/me/favorites")""", endpoints);
        Assert.Contains("group.AddEndpointFilter<global::Haldus.HaldusUserRequiredFilter>();", endpoints);
        Assert.Contains("""MapGet("/", ListAsync)""", endpoints);
        Assert.Contains("""MapGet("/{productId:guid}", ExistsAsync)""", endpoints);
        Assert.Contains("""MapPut("/{productId:guid}", LinkAsync)""", endpoints);
        Assert.Contains("""MapDelete("/{productId:guid}", UnlinkAsync)""", endpoints);

        // Liste bağları değil, bağlı kayıtları döner.
        Assert.Contains("global::Haldus.PagedResult<global::Shop.ProductResponse>", endpoints);
    }

    [Fact]
    public void ForCurrentUser_IsVisibleOnlyToItsOwnerAndOnlyWhileTheRecordIs()
    {
        var run = Run("""
            app.MapCrud<Product>().MultiTenant().SoftDeletable();
            app.MapAssociation<Favorite, Product>().ForCurrentUser();
            """).AssertCompiles();

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains(
            "entity.HasQueryFilter(global::Haldus.HaldusFilters.Owner, item => item.OwnerId == global::Haldus.HaldusScope.UserId(context));",
            module);
        Assert.Contains("context.Set<global::Shop.Product>().Any(right => right.Id == item.ProductId)", module);

        // Bağ bir işarettir; kaydın silinmesini engellemez, onunla birlikte gider.
        Assert.Contains(
            "HasForeignKey(item => item.ProductId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade);",
            module);

        // Karşı uç kiracıya bağlıysa kiracısız istek de reddedilir.
        var endpoints = run.Sources["Shop.Favorite.Endpoints.g.cs"];
        Assert.Contains("group.AddEndpointFilter<global::Haldus.HaldusTenantRequiredFilter>();", endpoints);
        Assert.Contains("NotFound(messages, \"Product\", productId)", endpoints);
    }

    [Fact]
    public void ForCurrentUser_UsesTheKeyOfTheOtherEnd()
    {
        var run = Run("""
            app.MapLookup<Currency>();
            app.MapAssociation<PinnedCurrency, Currency>().ForCurrentUser();
            """).AssertCompiles();

        Assert.Contains("public string CurrencyCode { get; set; } = \"\";", run.Sources["Shop.PinnedCurrency.Entity.g.cs"]);
        Assert.Contains("""MapPut("/{currencyCode}", LinkAsync)""", run.Sources["Shop.PinnedCurrency.Endpoints.g.cs"]);
        Assert.Contains("""endpoints.MapGroup("/me/pinned-currencies")""", run.Sources["Shop.PinnedCurrency.Endpoints.g.cs"]);
    }

    [Fact]
    public void ForCurrentUser_LinkWrittenByTheUser_IsExtendedWhereItIsDeclared()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop.Catalog
            {
                public partial class Product
                {
                    public string Name { get; set; } = "";
                }
            }

            namespace Shop.Account
            {
                internal partial class Star;
            }

            namespace Shop
            {
                public static class Startup
                {
                    public static void Map(IEndpointRouteBuilder app)
                    {
                        app.MapCrud<Catalog.Product>();
                        app.MapAssociation<Account.Star, Catalog.Product>().ForCurrentUser();
                    }
                }
            }
            """).AssertCompiles();

        // Erişim belirleyicisi kullanıcının bildiriminden gelir.
        var entity = run.Sources["Shop.Account.Star.Entity.g.cs"];
        Assert.Contains("namespace Shop.Account;", entity);
        Assert.Contains("\npartial class Star : global::Haldus.IOwned", entity);
    }

    [Fact]
    public void ForCurrentUser_UnwrittenLink_IsGeneratedWhereItIsMapped()
    {
        var run = GeneratorHarness.Run("""
            namespace Shop.Catalog
            {
                public partial class Product
                {
                    public string Name { get; set; } = "";
                }
            }

            namespace Shop.Account
            {
                public static class Startup
                {
                    public static void Map(IEndpointRouteBuilder app)
                    {
                        app.MapCrud<Catalog.Product>();
                        app.MapAssociation<Favorite, Catalog.Product>().ForCurrentUser();
                    }
                }
            }
            """).AssertCompiles();

        // Çağrıdaki ad, karşı ucun namespace'i içe aktarılmadan da çözülebilmelidir.
        Assert.Contains("namespace Shop.Account;", run.Sources["Shop.Account.Favorite.Entity.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapAssociation<Favorite, Product>().ForCurrentUser();", "HALDUS009", "Product")]
    [InlineData(
        "app.MapCrud<Product>(); app.MapSingleton<Settings>(); app.MapAssociation<Favorite, Settings>().ForCurrentUser();",
        "HALDUS007",
        "'Settings' cannot take part in an association")]
    [InlineData(
        "app.MapCrud<Product>(); app.MapCrud<Tag>(); app.MapAssociation<Tag, Product>().ForCurrentUser();",
        "HALDUS007",
        "'Tag' names the link of a ForCurrentUser association")]
    public void ForCurrentUser_InvalidDeclarations_AreReported(string body, string id, string expectedMessage)
    {
        var diagnostic = Assert.Single(Run(body).GeneratorDiagnostics);

        Assert.Equal(id, diagnostic.Id);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public void ForCurrentUser_OnAVariable_IsNotSeenAndIsReported()
    {
        var run = Run("""
            app.MapCrud<Product>();
            var favorites = app.MapAssociation<Product, Product>();
            favorites.ForCurrentUser();
            """);

        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "HALDUS004");
    }

    /// <summary>
    /// Bağ listeleri karşı ucun yanıtını döndürür. Yanıtın zorunlu alanları (durum, numara, gömülü satırlar)
    /// burada da doldurulmalıdır; eksik kalsalar üretilen kod derlenmezdi.
    /// </summary>
    [Fact]
    public void LinkLists_ProjectEveryRequiredFieldOfTheResponse()
    {
        var run = Run("""
            var orders = app.MapCrud<Order>()
                .Workflow<OrderStatus>(initial: OrderStatus.Draft)
                .Numbered("ORD-{seq:6}");
            orders.MapDetail<OrderItem>();
            app.MapCrud<Tag>();
            app.MapAssociation<Order, Tag>();
            app.MapAssociation<Bookmark, Order>().ForCurrentUser();
            """).AssertCompiles();

        foreach (var endpoints in new[] { run.Sources["Shop.OrderTag.Endpoints.g.cs"], run.Sources["Shop.Bookmark.Endpoints.g.cs"] })
        {
            Assert.Contains("Status = item.Status", endpoints);
            Assert.Contains("Number = item.Number", endpoints);
            Assert.Contains("OrderItems = item.OrderItems", endpoints);
        }
    }

    [Fact]
    public void OwnershipSnapshot()
    {
        var run = Run("""
            app.MapCrud<Product>();
            app.MapCrud<Cart>().MultiTenant().OwnedByCurrentUser();
            app.MapCrud<CartLine>().Under<Cart>();
            app.MapCrud<Note>().Under<Product>().OwnedByCurrentUser(underMe: false);
            app.MapAssociation<Favorite, Product>().ForCurrentUser();
            """).AssertCompiles();

        Snapshot.Verify(run);
    }
}
