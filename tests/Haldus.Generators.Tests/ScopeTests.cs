namespace Haldus.Generators.Tests;

/// <summary>
/// Bağlar ve kapsam: bağların topluca değiştirilmesi, yük ve durum taşıyan bağ, ebeveyn altında tek kayıt, kısa
/// adres ve komutla yazılan satırlar.
/// </summary>
public sealed class ScopeTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum SupplyStatus { Proposed, Approved, Discontinued }

            public enum OtherStatus { One, Two }

            public partial class Product
            {
                public string Name { get; set; } = "";
            }

            public partial class Tag
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }

            public partial class Supplier
            {
                public string Name { get; set; } = "";
            }

            public partial class SupplierCertificate
            {
                [Required]
                public string Name { get; set; } = "";
            }

            public partial class SupplierTerms
            {
                public int PaymentDays { get; set; } = 30;
            }

            public partial class SupplierContact
            {
                public string Name { get; set; } = "";
            }

            // Bağ sınıfına yazılan alanlar bağın yüküdür.
            public partial class SupplierProduct
            {
                [Range(0.0, 1000.0)]
                public decimal Cost { get; set; }

                public int LeadTimeDays { get; set; }
            }

            public partial class Favorite;

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

    // ── Bağların topluca değiştirilmesi ────────────────────────────────────────────────────────────────

    [Fact]
    public void PlainLinks_CanBeReplacedFromBothEnds()
    {
        var run = Run("""
            app.MapCrud<Product>();
            app.MapCrud<Tag>();
            app.MapAssociation<Product, Tag>();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.ProductTag.Endpoints.g.cs"];
        Assert.Contains("""MapPut("/products/{productId:guid}/tags", ReplaceTagsAsync)""", endpoints);
        Assert.Contains("""MapPut("/tags/{tagId:int}/products", ReplaceProductsAsync)""", endpoints);
        Assert.Contains("global::Haldus.BatchIdsRequest<int> request", endpoints);
        Assert.Contains("global::Haldus.BatchIdsRequest<global::System.Guid> request", endpoints);

        // Listedeki her kayıt isteği yapana görünür olmalıdır.
        Assert.Contains("ReferenceNotFound(messages, \"ids\", \"Tag\", missing!)", endpoints);

        // Yük taşımayan bağın kendi istek ve yanıt tipi yoktur.
        Assert.DoesNotContain(run.Sources.Keys, key => key.Contains("ProductTag.Dtos", StringComparison.Ordinal));
    }

    // ── Yük taşıyan bağ ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LinkWithPayload_IsWrittenAndReadWithItsOwnFields()
    {
        var run = Run("""
            app.MapCrud<Supplier>();
            app.MapCrud<Product>();
            app.MapAssociation<Supplier, Product>();
            """).AssertCompiles();

        // Sınıfı kullanıcı yazdı; üretilen yarı yalnızca anahtarları ekler ve erişim belirleyicisini ona bırakır.
        var entity = run.Sources["Shop.SupplierProduct.Entity.g.cs"];
        Assert.Contains("\npartial class SupplierProduct", entity);
        Assert.Contains("public global::System.Guid SupplierId { get; set; }", entity);
        Assert.DoesNotContain("Cost", entity);

        var dtos = run.Sources["Shop.SupplierProduct.Dtos.g.cs"];
        Assert.Contains("public sealed partial record SupplierProductResponse", dtos);
        Assert.Contains("public required decimal Cost { get; init; }", dtos);
        Assert.Contains("public sealed partial record SupplierProductRequest", dtos);
        Assert.Contains("[global::System.ComponentModel.DataAnnotations.RangeAttribute(0D, 1000D)]", dtos);

        var endpoints = run.Sources["Shop.SupplierProduct.Endpoints.g.cs"];
        Assert.Contains("SupplierProductRequest request,", endpoints);
        Assert.Contains("global::Haldus.HaldusValidationFilter<SupplierProductRequest>", endpoints);
        Assert.Contains("existing.Cost = request.Cost;", endpoints);
        Assert.Contains("""MapGet("/suppliers/{supplierId:guid}/products/links", ListSupplierLinksAsync)""", endpoints);
        Assert.Contains("""MapGet("/products/{productId:guid}/suppliers/links", ListProductLinksAsync)""", endpoints);

        // Yükü olan bağ topluca değiştirilemez: listedeki kimlikler yükü taşıyamaz.
        Assert.DoesNotContain("Replace", endpoints);
    }

    // ── Durumu olan bağ ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LinkWithState_GetsAStatusThatOnlyTransitionsChange()
    {
        var run = Run("""
            app.MapCrud<Supplier>();
            app.MapCrud<Product>();
            var supplies = app.MapAssociation<Supplier, Product>().Workflow<SupplyStatus>(initial: SupplyStatus.Proposed);
            supplies.MapTransition("approve", from: SupplyStatus.Proposed, to: SupplyStatus.Approved);
            supplies.MapTransition("discontinue", [SupplyStatus.Proposed, SupplyStatus.Approved], SupplyStatus.Discontinued);
            """).AssertCompiles();

        Assert.Contains("public global::Shop.SupplyStatus Status { get; set; }", run.Sources["Shop.SupplierProduct.Entity.g.cs"]);

        var dtos = run.Sources["Shop.SupplierProduct.Dtos.g.cs"];
        Assert.Contains("JsonStringEnumConverter<global::Shop.SupplyStatus>", dtos);
        Assert.Contains("public required global::Shop.SupplyStatus Status { get; init; }", dtos);

        // Durum istek tipinde yer almaz.
        var request = dtos.Substring(dtos.IndexOf("record SupplierProductRequest", StringComparison.Ordinal));
        Assert.DoesNotContain("Status", request);

        var endpoints = run.Sources["Shop.SupplierProduct.Endpoints.g.cs"];
        Assert.Contains("Status = global::Shop.SupplyStatus.Proposed }", endpoints);
        Assert.Contains("""MapPost("/suppliers/{supplierId:guid}/products/{productId:guid}/approve", ApproveAsync)""", endpoints);
        Assert.Contains("""MapPost("/suppliers/{supplierId:guid}/products/{productId:guid}/discontinue", DiscontinueAsync)""", endpoints);
        Assert.Contains("existing.Status is not (global::Shop.SupplyStatus.Proposed or global::Shop.SupplyStatus.Approved)", endpoints);
        Assert.Contains("StateConflict(messages, \"discontinue\", existing.Status)", endpoints);
        Assert.Contains("existing.Status = global::Shop.SupplyStatus.Discontinued;", endpoints);
        Assert.Contains("StateChanged(messages)", endpoints);
        Assert.Contains("global::Shop.SupplyStatus? status,", endpoints);

        // Durum adıyla saklanır ve yazmayla birlikte yeniden doğrulanır.
        Assert.Contains(
            "entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(64).IsConcurrencyToken();",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void LinkWithState_NeedsNoWrittenClass()
    {
        var run = Run("""
            app.MapCrud<Product>();
            app.MapCrud<Tag>();
            var links = app.MapAssociation<Product, Tag>().Workflow<SupplyStatus>();
            links.MapTransition("approve", SupplyStatus.Proposed, SupplyStatus.Approved);
            """).AssertCompiles();

        var entity = run.Sources["Shop.ProductTag.Entity.g.cs"];
        Assert.Contains("public partial class ProductTag", entity);
        Assert.Contains("public global::Shop.SupplyStatus Status { get; set; }", entity);

        var dtos = run.Sources["Shop.ProductTag.Dtos.g.cs"];
        Assert.Contains("record ProductTagResponse", dtos);
        Assert.DoesNotContain("record ProductTagRequest", dtos);

        // Yükü olmayan bağ gövdesiz kurulur, ama durumu olduğu için yanıtını döndürür.
        var endpoints = run.Sources["Shop.ProductTag.Endpoints.g.cs"];
        Assert.DoesNotContain("ProductTagRequest", endpoints);
        Assert.Contains("TypedResults.Ok(ToLinkResponse(existing))", endpoints);
        Assert.DoesNotContain("Replace", endpoints);
    }

    [Theory]
    [InlineData(
        """
        var links = app.MapAssociation<Product, Tag>();
        links.MapTransition("approve", SupplyStatus.Proposed, SupplyStatus.Approved);
        """,
        "HALDUS007",
        "MapTransition needs a state machine")]
    [InlineData(
        """
        var links = app.MapAssociation<Product, Tag>().Workflow<SupplyStatus>();
        links.MapTransition("approve", OtherStatus.One, SupplyStatus.Approved);
        """,
        "HALDUS010",
        "'One' is not a state of 'ProductTag'")]
    [InlineData(
        """
        var links = app.MapAssociation<Product, Tag>().Workflow<SupplyStatus>();
        links.MapTransition("Approve It", SupplyStatus.Proposed, SupplyStatus.Approved);
        """,
        "HALDUS010",
        "is not a valid transition name")]
    [InlineData(
        """
        var links = app.MapAssociation<Product, Tag>().Workflow<SupplyStatus>();
        links.MapTransition("approve", SupplyStatus.Proposed, SupplyStatus.Approved);
        links.MapTransition("approve", SupplyStatus.Approved, SupplyStatus.Discontinued);
        """,
        "HALDUS007",
        "already has a transition named 'approve'")]
    [InlineData(
        """
        app.MapAssociation<Favorite, Tag>().ForCurrentUser().Workflow<SupplyStatus>();
        """,
        "HALDUS007",
        "Workflow cannot be combined with ForCurrentUser()")]
    public void LinkWithState_RejectsDeclarationsThatCannotWork(string links, string expectedId, string expectedMessage)
    {
        var run = Run($"""
            app.MapCrud<Product>();
            app.MapCrud<Tag>();
            {links}
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public void LinkWithState_MustBeDeclaredInTheChain()
    {
        var run = Run("""
            app.MapCrud<Product>();
            app.MapCrud<Tag>();
            var links = app.MapAssociation<Product, Tag>();
            links.Workflow<SupplyStatus>();
            """);

        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == "HALDUS004");
    }

    // ── Ebeveyn altında tek kayıt ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void SingletonUnderAParent_IsKeyedByItsParent()
    {
        var run = Run("""
            app.MapCrud<Supplier>();
            app.MapSingleton<SupplierTerms>().Under<Supplier>();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.SupplierTerms.Endpoints.g.cs"];
        Assert.Contains("""MapGroup("/suppliers/{supplierId:guid}/supplier-terms")""", endpoints);
        Assert.Contains("""MapGet("/", GetAsync)""", endpoints);
        Assert.Contains("""MapPut("/", UpdateAsync)""", endpoints);
        Assert.DoesNotContain("MapPost", endpoints);
        Assert.DoesNotContain("MapDelete", endpoints);

        // Ebeveyn yoksa kayıt da yoktur; varsa ve hiç yazılmadıysa varsayılan değerler döner.
        Assert.Contains("NotFound(messages, \"Supplier\", supplierId)", endpoints);
        Assert.Contains("new SupplierTerms { SupplierId = supplierId }", endpoints);

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.HasKey(item => item.SupplierId);", module);
        Assert.Contains("entity.Property(item => item.SupplierId).ValueGeneratedNever();", module);
        Assert.Contains(
            "HasForeignKey(item => item.SupplierId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade)",
            module);
        Assert.Contains("entity.HasQueryFilter(global::Haldus.HaldusFilters.Parent, item => context.Set<global::Shop.Supplier>()", module);
    }

    [Theory]
    [InlineData("app.MapSingleton<SupplierTerms>().Under<Supplier>().MultiTenant();")]
    [InlineData("app.MapSingleton<SupplierTerms>().Under<Supplier>(shallow: true);")]
    public void SingletonUnderAParent_RejectsCombinationsThatHaveNoMeaning(string declaration)
    {
        var run = Run($"""
            app.MapCrud<Supplier>();
            {declaration}
            """);

        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == "HALDUS007");
    }

    // ── Kısa adres ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShallowChild_KeepsItsOwnAddressAndTakesItsParentFromTheRequest()
    {
        var run = Run("""
            app.MapCrud<Supplier>();
            app.MapCrud<SupplierContact>().Under<Supplier>(shallow: true).Orderable();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.SupplierContact.Endpoints.g.cs"];
        Assert.Contains("""MapGroup("/supplier-contacts")""", endpoints);
        Assert.Contains("""MapGet("/{id:guid}", GetAsync)""", endpoints);

        // Liste ebeveynler arasında döner ve ebeveyne göre süzülür.
        Assert.Contains("global::System.Guid? supplierId,", endpoints);

        // Ebeveyn oluşturma isteğinde gelir, doğrulanır ve sonradan değişmez.
        var dtos = run.Sources["Shop.SupplierContact.Dtos.g.cs"];
        var create = Between(dtos, "record CreateSupplierContactRequest", "record UpdateSupplierContactRequest");
        var update = Between(dtos, "record UpdateSupplierContactRequest", "record PatchSupplierContactRequest");
        Assert.Contains("public global::System.Guid SupplierId { get; init; }", create);
        Assert.DoesNotContain("SupplierId", update);
        Assert.Contains("ReferenceNotFound(messages, \"supplierId\", \"Supplier\"", endpoints);

        // Ebeveynin anlamı aynen sürer: ebeveyn silinince altındakiler gider, görünürlüğü ebeveyninden gelir.
        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains(
            "HasForeignKey(item => item.SupplierId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade)",
            module);
        Assert.Contains("entity.HasQueryFilter(global::Haldus.HaldusFilters.Parent, item => context.Set<global::Shop.Supplier>()", module);
    }

    // ── Komutla yazılan satırlar ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommandWrittenDetail_IsWrittenRowByRowThroughItsOwner()
    {
        var run = Run("""
            var suppliers = app.MapCrud<Supplier>().ConcurrencyChecked();
            suppliers.MapDetail<SupplierCertificate>().AsCommands();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Supplier.Endpoints.g.cs"];
        Assert.Contains("""MapPost("/{id:guid}/supplier-certificates", AddSupplierCertificateAsync)""", endpoints);
        Assert.Contains("""MapPut("/{id:guid}/supplier-certificates/{rowId:guid}", ChangeSupplierCertificateAsync)""", endpoints);
        Assert.Contains("""MapDelete("/{id:guid}/supplier-certificates/{rowId:guid}", RemoveSupplierCertificateAsync)""", endpoints);

        // Komut üst kaydın bir yazmasıdır: sürüm ister ve üst kaydı döndürür.
        var add = Between(endpoints, " AddSupplierCertificateAsync(", " ChangeSupplierCertificateAsync(");
        Assert.Contains("PreconditionRequired(messages)", add);
        Assert.Contains("TypedResults.Ok(ToResponse(entity))", add);

        // Satırlar yanıtta ve oluşturma isteğinde gömülüdür, ama güncelleme isteklerinde taşınmaz.
        var dtos = run.Sources["Shop.Supplier.Dtos.g.cs"];
        Assert.Contains("SupplierCertificates", Between(dtos, "record SupplierResponse", "record CreateSupplierRequest"));
        Assert.Contains("SupplierCertificates", Between(dtos, "record CreateSupplierRequest", "record UpdateSupplierRequest"));
        Assert.DoesNotContain("SupplierCertificates", dtos.Substring(dtos.IndexOf("record UpdateSupplierRequest", StringComparison.Ordinal)));
        Assert.DoesNotContain("SyncSupplierCertificates", endpoints);
    }

    [Fact]
    public void CommandWrittenDetail_CannotAlsoBeASubresource()
    {
        var run = Run("""
            app.MapCrud<Supplier>().MapDetail<SupplierCertificate>().AsCommands().AsSubresource();
            """);

        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == "HALDUS007");
    }

    [Fact]
    public void CommandWrittenDetail_ReservesItsRouteOnTheOwner()
    {
        var run = Run("""
            var suppliers = app.MapCrud<Supplier>();
            suppliers.MapDetail<SupplierCertificate>().AsCommands();
            suppliers.MapAction("supplier-certificates", supplier => supplier.Name = supplier.Name.Trim());
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics, candidate => candidate.Id == "HALDUS007");
        Assert.Contains("collides with the 'supplier-certificates' endpoint", diagnostic.GetMessage());
    }

    [Fact]
    public void ScopeSnapshot()
    {
        var run = Run("""
            var suppliers = app.MapCrud<Supplier>().ConcurrencyChecked();
            suppliers.MapDetail<SupplierCertificate>().AsCommands();
            app.MapSingleton<SupplierTerms>().Under<Supplier>();
            app.MapCrud<SupplierContact>().Under<Supplier>(shallow: true);
            app.MapCrud<Product>();
            var supplies = app.MapAssociation<Supplier, Product>().Workflow<SupplyStatus>(initial: SupplyStatus.Proposed);
            supplies.MapTransition("approve", from: SupplyStatus.Proposed, to: SupplyStatus.Approved);
            """).AssertCompiles();

        Snapshot.Verify(run);
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);

        Assert.True(from >= 0 && to > from, $"'{start}' … '{end}' not found");
        return text.Substring(from, to - from);
    }
}
