namespace Haldus.Generators.Tests;

/// <summary>
/// Alan eklentileri: yalnızca oluştururken yazılan alan, doğal anahtar, liste sıralaması, anahtar biçimi ve
/// metadata çantası.
/// </summary>
public sealed class FieldExtensionTests
{
    private const string Entities = """
        namespace Shop
        {
            public partial class Coupon
            {
                [MaxLength(50)]
                public string Code { get; set; } = "";

                public int Percent { get; set; }

                public int? MaxUses { get; set; }

                public string? Label { get; set; }

                public int Used { get; private set; }
            }

            public partial class Product
            {
                public string Name { get; set; } = "";
                public decimal Price { get; set; }
            }

            public partial class Tag
            {
                public string Name { get; set; } = "";
            }

            public partial class Ticket
            {
                public int Number { get; set; }
            }

            public partial class Counted
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }

            public partial class Texted
            {
                public string Id { get; set; } = "";
                public string Name { get; set; } = "";
            }

            public partial class Settings
            {
                public string Theme { get; set; } = "";
            }

            public partial class Currency;

            public partial class Export
            {
                public int Year { get; set; }
            }

            public sealed class SortingFilter : IFilter<Product>
            {
                public string? Sort { get; set; }

                public System.Linq.IQueryable<Product> Apply(System.Linq.IQueryable<Product> query) => query;
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

    /// <summary>Üretilen DTO dosyasından tek bir record'un metnini ayırır.</summary>
    private static string Record(string source, string name)
    {
        var start = source.IndexOf($"record {name}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{name}' was not generated.");

        var end = source.IndexOf("\npublic ", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    /// <summary>Üretilen uç dosyasından tek bir handler'ın metnini ayırır.</summary>
    private static string Handler(string source, string name)
    {
        // Bildirimde parantezden sonra satır biter; çağrılarda bitmez.
        var start = source.IndexOf($" {name}(\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{name}' was not generated.");

        var end = source.IndexOf("\n    private static ", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static void AssertSingle(GeneratorRun run, string id, string message)
    {
        var diagnostic = Assert.Single(run.GeneratorDiagnostics);

        Assert.Equal(id, diagnostic.Id);
        Assert.Contains(message, diagnostic.GetMessage());
    }

    // ── Yalnızca oluştururken yazılan alan ─────────────────────────────────────────────────────────────

    [Fact]
    public void CreateOnly_KeepsTheFieldOutOfEveryUpdateRequest()
    {
        var run = Run("""
            app.MapCrud<Coupon>()
                .CreateOnly(coupon => coupon.Code, coupon => coupon.Percent)
                .Batch();
            """).AssertCompiles();

        var dtos = run.Sources["Shop.Coupon.Dtos.g.cs"];
        Assert.Contains("string Code", Record(dtos, "CreateCouponRequest"));
        Assert.Contains("int Percent", Record(dtos, "CreateCouponRequest"));

        foreach (var request in new[] { "UpdateCouponRequest", "PatchCouponRequest", "BatchUpdateCouponItem" })
        {
            Assert.DoesNotContain("Code", Record(dtos, request));
            Assert.DoesNotContain("Percent", Record(dtos, request));
            Assert.Contains("MaxUses", Record(dtos, request));
        }

        // Alan yanıtta kalır; yalnızca yazılamaz.
        Assert.Contains("string Code", Record(dtos, "CouponResponse"));

        var endpoints = run.Sources["Shop.Coupon.Endpoints.g.cs"];
        Assert.DoesNotContain("entity.Code = request.Code", endpoints);
        Assert.Contains("entity.MaxUses = request.MaxUses", endpoints);
    }

    [Fact]
    public void CreateOnly_OnAGeneratedForeignKey_FixesTheReferenceAtCreation()
    {
        var run = Run("""
            app.MapCrud<Tag>();
            app.MapCrud<Product>()
                .References<Tag>()
                .Hierarchical()
                .CreateOnly(product => product.TagId, product => product.ParentId);
            """).AssertCompiles();

        var dtos = run.Sources["Shop.Product.Dtos.g.cs"];
        Assert.Contains("TagId", Record(dtos, "CreateProductRequest"));
        Assert.DoesNotContain("TagId", Record(dtos, "UpdateProductRequest"));
        Assert.DoesNotContain("ParentId", Record(dtos, "PatchProductRequest"));

        // Referans yalnızca oluştururken doğrulanır; güncellemede istekte yer almaz.
        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Contains("ReferenceNotFound(messages, \"tagId\"", Handler(endpoints, "CreateAsync"));
        Assert.DoesNotContain("ReferenceNotFound", Handler(endpoints, "UpdateAsync"));
        Assert.DoesNotContain("CreatesCycleAsync(contexts", Handler(endpoints, "UpdateAsync"));
    }

    [Theory]
    [InlineData("app.MapCrud<Coupon>().CreateOnly(coupon => coupon.Used);", "HALDUS010", "cannot be create-only")]
    [InlineData("app.MapCrud<Coupon>().CreateOnly(coupon => coupon.Code.Length);", "HALDUS010", "cannot be create-only")]
    [InlineData("app.MapCrud<Coupon>().CreateOnly();", "HALDUS010", "CreateOnly needs at least one field")]
    [InlineData("app.MapStore<Settings>().CreateOnly(settings => settings.Theme);", "HALDUS007", "CreateOnly cannot be combined with MapStore")]
    [InlineData("app.MapAppendOnly<Coupon>().CreateOnly(coupon => coupon.Code);", "HALDUS007", "CreateOnly cannot be combined with MapAppendOnly")]
    [InlineData(
        "app.MapCrud<Coupon>().Without(CrudOperation.Update, CrudOperation.Patch).CreateOnly(coupon => coupon.Code);",
        "HALDUS007",
        "CreateOnly has no effect")]
    public void CreateOnly_InvalidDeclarations_AreReported(string body, string id, string message) =>
        AssertSingle(Run(body), id, message);

    // ── Sıralama ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sortable_AddsASortParameterLimitedToTheDeclaredFields()
    {
        var endpoints = Run("""
            app.MapCrud<Product>()
                .Auditable()
                .Sortable(product => product.Name, product => product.CreatedAt);
            """).AssertCompiles().Sources["Shop.Product.Endpoints.g.cs"];

        var list = Handler(endpoints, "ListAsync");
        Assert.Contains("string? sort,", list);
        Assert.Contains("if (!TrySort(query, sort, out var sorted, out var unknownSortField))", list);
        Assert.Contains("SortInvalid(messages, unknownSortField, SortableFields)", list);

        Assert.Contains("""private const string SortableFields = "name, createdAt";""", endpoints);
        Assert.Contains("""case "name":""", endpoints);
        Assert.Contains("""case "createdAt":""", endpoints);
        Assert.DoesNotContain("""case "price":""", endpoints);

        // Sayfalama kararlı olsun diye sıralama anahtarla tamamlanır; terim sayısı da sınırlıdır.
        Assert.Contains("OrderBy(query, ordered, item => item.Id, descending: false)", endpoints);
        Assert.Contains("if (terms.Length > 2)", endpoints);
    }

    [Fact]
    public void Sortable_IsOptIn()
    {
        var endpoints = Run("app.MapCrud<Product>();").AssertCompiles().Sources["Shop.Product.Endpoints.g.cs"];

        Assert.DoesNotContain("string? sort", endpoints);
        Assert.DoesNotContain("TrySort", endpoints);
    }

    [Theory]
    [InlineData("app.MapCrud<Product>().Sortable(product => product.Missing);", "HALDUS010", "cannot be sorted")]
    [InlineData("app.MapCrud<Product>().Sortable();", "HALDUS010", "Sortable needs at least one field")]
    [InlineData("app.MapLookup<Currency>().Sortable(currency => currency.Name);", "HALDUS007", "Sortable cannot be combined with MapLookup")]
    [InlineData(
        "app.MapCrud<Product>().Without(CrudOperation.List).Sortable(product => product.Name);",
        "HALDUS007",
        "Sortable requires the List operation")]
    [InlineData(
        "app.MapCrud<Product>().Filterable<SortingFilter>().Sortable(product => product.Name);",
        "HALDUS007",
        "rename the filter's Sort property")]
    public void Sortable_InvalidDeclarations_AreReported(string body, string id, string message) =>
        AssertSingle(Run(body), id, message);

    // ── Doğal anahtar ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NaturalKey_MakesTheFieldUniqueAndReadable()
    {
        var run = Run("app.MapCrud<Coupon>().NaturalKey(coupon => coupon.Code);").AssertCompiles();

        var endpoints = run.Sources["Shop.Coupon.Endpoints.g.cs"];
        Assert.Contains("""group.MapGet("/by-code/{code}", GetByCodeAsync)""", endpoints);
        Assert.Contains("""WithName("GetCouponByCode")""", endpoints);
        Assert.Contains("FirstOrDefaultAsync(item => item.Code == code, cancellationToken)", Handler(endpoints, "GetByCodeAsync"));

        // Diğer uçlar Id ile adreslenmeye devam eder.
        Assert.Contains("""group.MapGet("/{id:guid}", GetAsync)""", endpoints);

        Assert.Contains(
            """entity.HasIndex(item => item.Code).IsUnique().HasDatabaseName("UX_Coupons_Code");""",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void NaturalKey_IsCheckedBeforeEveryWriteAndAgainWhenSaving()
    {
        var endpoints = Run("app.MapCrud<Coupon>().NaturalKey(coupon => coupon.Code).Batch();")
            .AssertCompiles().Sources["Shop.Coupon.Endpoints.g.cs"];

        var create = Handler(endpoints, "CreateAsync");
        Assert.Contains("AnyAsync(item => item.Code == request.Code, cancellationToken)", create);
        Assert.Contains("""Duplicate(messages, "Coupon", "code", request.Code)""", create);

        // Ön kontrolü geçen eşzamanlı istek indekse takılır; o da çakışma olarak bildirilir.
        Assert.Contains("""IsUniqueViolation(duplicate, "UX_Coupons_Code")""", create);

        // Güncellemede kayıt kendi değeriyle çakışmaz; PATCH yalnızca alan gönderildiyse bakar.
        Assert.Contains("item.Code == request.Code && item.Id != id", Handler(endpoints, "UpdateAsync"));
        Assert.Contains("""IsSet("Code")""", Handler(endpoints, "PatchAsync"));
        Assert.Contains("IsUniqueViolation", Handler(endpoints, "UpdateAsync"));

        // Toplu yol aynı kontrollerden geçer.
        Assert.Contains("Duplicate(messages", Handler(endpoints, "BatchCreateItemAsync"));
        Assert.Contains("Duplicate(messages", Handler(endpoints, "BatchUpdateItemAsync"));
    }

    [Fact]
    public void NaturalKey_IsUniqueWithinTheScopeOfTheRecord()
    {
        var run = Run("""
            app.MapCrud<Product>();
            app.MapCrud<Tag>()
                .MultiTenant()
                .OwnedByCurrentUser(underMe: false)
                .Under<Product>()
                .NaturalKey(tag => tag.Name);
            """).AssertCompiles();

        // Aynı değer başka bir kiracıda, kullanıcıda veya ebeveynin altında yeniden kullanılabilir.
        Assert.Contains(
            """entity.HasIndex(item => new { item.TenantId, item.OwnerId, item.ProductId, item.Name }).IsUnique().HasDatabaseName("UX_Tags_Name");""",
            run.Sources["HaldusModule.g.cs"]);

        var endpoints = run.Sources["Shop.Tag.Endpoints.g.cs"];
        Assert.Contains("""group.MapGet("/by-name/{name}", GetByNameAsync)""", endpoints);
        Assert.Contains("item.Name == name && item.ProductId == productId", Handler(endpoints, "GetByNameAsync"));
        Assert.Contains("item.Name == request.Name && item.ProductId == productId", Handler(endpoints, "CreateAsync"));
    }

    [Fact]
    public void NaturalKey_IsStillHeldByHiddenRecords()
    {
        var endpoints = Run("app.MapCrud<Coupon>().SoftDeletable().Expiring().NaturalKey(coupon => coupon.Code);")
            .AssertCompiles().Sources["Shop.Coupon.Endpoints.g.cs"];

        // Silinmiş ve süresi dolmuş kayıtlar da değeri tutar; kontrol onları da görür. Yalnızca bu iki filtre
        // kaldırılır, kiracı ve sahip filtreleri kalır.
        Assert.Contains(
            ".IgnoreQueryFilters(new[] { global::Haldus.HaldusFilters.SoftDelete, global::Haldus.HaldusFilters.Expiry })",
            Handler(endpoints, "CreateAsync"));
    }

    [Fact]
    public void NaturalKey_ThatIsCreateOnly_IsNotCheckedOnUpdate()
    {
        var endpoints = Run("app.MapCrud<Coupon>().NaturalKey(coupon => coupon.Code).CreateOnly(coupon => coupon.Code);")
            .AssertCompiles().Sources["Shop.Coupon.Endpoints.g.cs"];

        Assert.Contains("Duplicate(messages", Handler(endpoints, "CreateAsync"));
        Assert.DoesNotContain("request.Code", Handler(endpoints, "UpdateAsync"));
        Assert.DoesNotContain("request.Code", Handler(endpoints, "PatchAsync"));
    }

    [Fact]
    public void NaturalKey_CanBeANumberAndCanBelongToAReadOnlyResource()
    {
        var run = Run("app.MapReadOnly<Ticket>().NaturalKey(ticket => ticket.Number);").AssertCompiles();

        var endpoints = run.Sources["Shop.Ticket.Endpoints.g.cs"];
        Assert.Contains("""group.MapGet("/by-number/{number:int}", GetByNumberAsync)""", endpoints);
        Assert.DoesNotContain("Duplicate(messages", endpoints);
        Assert.Contains("UX_Tickets_Number", run.Sources["HaldusModule.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapCrud<Coupon>().NaturalKey(coupon => coupon.Label);", "HALDUS010", "cannot be a natural key")]
    [InlineData("app.MapCrud<Product>().NaturalKey(product => product.Price);", "HALDUS010", "cannot be a natural key")]
    [InlineData("app.MapCrud<Product>().NaturalKey(product => product.Missing);", "HALDUS010", "cannot be a natural key")]
    [InlineData(
        "app.MapCrud<Coupon>().NaturalKey(coupon => coupon.Code).NaturalKey(coupon => coupon.Percent);",
        "HALDUS007",
        "can have only one natural key")]
    [InlineData("app.MapLookup<Currency>().NaturalKey(currency => currency.Name);", "HALDUS007", "NaturalKey cannot be combined with MapLookup")]
    public void NaturalKey_InvalidDeclarations_AreReported(string body, string id, string message) =>
        AssertSingle(Run(body), id, message);

    // ── Anahtar biçimi ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void KeyStrategy_Prefixed_MakesTheKeyAStringEverywhere()
    {
        var run = Run("""
            app.MapCrud<Coupon>().KeyStrategy(KeyStrategy.Prefixed, "cpn");
            app.MapCrud<Tag>().Under<Coupon>();
            """).AssertCompiles();

        Assert.Contains("public string Id { get; set; } = \"\";", run.Sources["Shop.Coupon.Entity.g.cs"]);
        Assert.Contains("public required string Id { get; init; }", run.Sources["Shop.Coupon.Dtos.g.cs"]);

        var endpoints = run.Sources["Shop.Coupon.Endpoints.g.cs"];
        Assert.Contains("""Id = global::Haldus.HaldusKeys.NewPrefixed("cpn")""", endpoints);
        Assert.Contains("""group.MapGet("/{id}", GetAsync)""", endpoints);

        // Uygulamanın kendi eklediği kayıtlar için de aynı biçim üretilir.
        Assert.Contains(
            """global::Haldus.HaldusDb.ConfigurePrefixedKey(entity.Property(item => item.Id), "cpn");""",
            run.Sources["HaldusModule.g.cs"]);

        // Altındaki kaynağın yabancı anahtarı ve adresi de metindir.
        Assert.Contains("public string CouponId { get; set; }", run.Sources["Shop.Tag.Entity.g.cs"]);
        Assert.Contains("""endpoints.MapGroup("/coupons/{couponId}/tags")""", run.Sources["Shop.Tag.Endpoints.g.cs"]);
    }

    [Fact]
    public void KeyStrategy_Prefixed_AcceptsAKeyWrittenByTheUser()
    {
        var run = Run("""app.MapCrud<Texted>().KeyStrategy(KeyStrategy.Prefixed, prefix: "txt");""").AssertCompiles();

        Assert.Contains("""Id = global::Haldus.HaldusKeys.NewPrefixed("txt")""", run.Sources["Shop.Texted.Endpoints.g.cs"]);
    }

    [Fact]
    public void KeyStrategy_Ulid_MakesTheKeyASortableString()
    {
        var run = Run("app.MapCrud<Product>().KeyStrategy(KeyStrategy.Ulid);").AssertCompiles();

        Assert.Contains("public string Id { get; set; }", run.Sources["Shop.Product.Entity.g.cs"]);

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Contains("Id = global::Haldus.HaldusKeys.NewUlid()", Handler(endpoints, "CreateAsync"));
        Assert.Contains("""MapGet("/{id}", GetAsync)""", endpoints);

        // Uygulamanın kendi eklediği kayıtlar da aynı biçimi alır.
        Assert.Contains("global::Haldus.HaldusDb.ConfigureUlidKey(entity.Property(item => item.Id));", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void KeyStrategy_Client_TakesTheKeyFromTheCreateRequest()
    {
        var run = Run("app.MapCrud<Product>().KeyStrategy(KeyStrategy.Client).Previewable();").AssertCompiles();
        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        var create = Handler(endpoints, "CreateAsync");

        // Kimlik adresin sorgusundan gelir (gövdede değil); boş olamaz ve alınmışsa çakışmadır.
        Assert.Contains("global::System.Guid id,\n        CreateProductRequest request,", create);
        Assert.Contains("if (id == global::System.Guid.Empty)", create);
        Assert.Contains("Id = id,", create);
        Assert.Contains("IsUniqueViolation(taken, \"PK_Products\")", create);
        Assert.Contains("Duplicate(messages, \"Product\", \"id\", entity.Id)", create);
        Assert.DoesNotContain("Id", run.Sources["Shop.Product.Dtos.g.cs"].Substring(run.Sources["Shop.Product.Dtos.g.cs"].IndexOf("record CreateProductRequest", StringComparison.Ordinal)).Split("record UpdateProductRequest")[0].Replace("CategoryId", string.Empty));

        // Önizlenen kayıt var olmayacak; kimliği de yoktur.
        Assert.DoesNotContain("Id = id", Handler(endpoints, "PreviewAsync"));

        // Anahtarı ne veritabanı ne EF üretir.
        Assert.Contains("entity.Property(item => item.Id).ValueGeneratedNever();", run.Sources["HaldusModule.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapCrud<Product>().KeyStrategy(KeyStrategy.Client).Batch();", "KeyStrategy.Client cannot be combined with Batch")]
    [InlineData("app.MapCrud<Product>().KeyStrategy(KeyStrategy.Client).Cloneable();", "KeyStrategy.Client cannot be combined with Cloneable")]
    [InlineData("app.MapCrud<Product>().KeyStrategy(KeyStrategy.Client).Without(CrudOperation.Create);", "KeyStrategy.Client needs the Create operation")]
    [InlineData("app.MapCrud<Product>().KeyStrategy(KeyStrategy.Ulid, \"ord\");", "A key prefix has no effect")]
    public void KeyStrategy_RejectsCombinationsThatCannotWork(string body, string message)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == "HALDUS007" && candidate.GetMessage().Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void KeyStrategy_ChoosesBetweenSequentialAndRandomGuids()
    {
        var random = Run("app.MapCrud<Product>().KeyStrategy(KeyStrategy.Random);").AssertCompiles();
        var sequential = Run("app.MapCrud<Product>().KeyStrategy(KeyStrategy.Sequential);").AssertCompiles();
        var unspecified = Run("app.MapCrud<Product>();").AssertCompiles();

        Assert.Contains("Id = global::System.Guid.NewGuid()", random.Sources["Shop.Product.Endpoints.g.cs"]);
        Assert.Contains("Id = global::System.Guid.CreateVersion7()", sequential.Sources["Shop.Product.Endpoints.g.cs"]);
        Assert.Contains("Id = global::System.Guid.CreateVersion7()", unspecified.Sources["Shop.Product.Endpoints.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapCrud<Product>().KeyStrategy(KeyStrategy.Prefixed);", "HALDUS010", "needs a prefix")]
    [InlineData("""app.MapCrud<Product>().KeyStrategy(KeyStrategy.Prefixed, "Ord");""", "HALDUS010", "needs a prefix")]
    [InlineData("""app.MapCrud<Product>().KeyStrategy(KeyStrategy.Prefixed, "9x");""", "HALDUS010", "needs a prefix")]
    [InlineData("""app.MapCrud<Product>().KeyStrategy(KeyStrategy.Prefixed, "a_b");""", "HALDUS010", "needs a prefix")]
    [InlineData("""app.MapCrud<Product>().KeyStrategy(KeyStrategy.Prefixed, "abcdefghijklm");""", "HALDUS010", "needs a prefix")]
    [InlineData("""var prefix = "ord"; app.MapCrud<Product>().KeyStrategy(KeyStrategy.Prefixed, prefix);""", "HALDUS010", "needs a prefix")]
    [InlineData("""app.MapCrud<Product>().KeyStrategy(KeyStrategy.Random, "ord");""", "HALDUS007", "A key prefix has no effect")]
    [InlineData("app.MapCrud<Counted>().KeyStrategy(KeyStrategy.Random);", "HALDUS007", "produces a Guid key, but 'Counted.Id' is declared as 'int'")]
    [InlineData(
        """app.MapCrud<Counted>().KeyStrategy(KeyStrategy.Prefixed, "cnt");""",
        "HALDUS007",
        "produces a string key, but 'Counted.Id' is declared as 'int'")]
    [InlineData("""app.MapStore<Settings>().KeyStrategy(KeyStrategy.Random);""", "HALDUS007", "KeyStrategy cannot be combined with MapStore")]
    [InlineData(
        "app.MapJob<Export>((job, context, cancellationToken) => System.Threading.Tasks.Task.CompletedTask).KeyStrategy(KeyStrategy.Random);",
        "HALDUS007",
        "KeyStrategy cannot be combined with MapJob")]
    public void KeyStrategy_InvalidDeclarations_AreReported(string body, string id, string message) =>
        AssertSingle(Run(body), id, message);

    // ── Metadata ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Metadata_AddsABagThatEveryRequestCanCarry()
    {
        var run = Run("app.MapCrud<Product>().Metadata().Batch();").AssertCompiles();

        var entity = run.Sources["Shop.Product.Entity.g.cs"];
        Assert.Contains("global::Haldus.IHasMetadata", entity);
        Assert.Contains("public global::System.Collections.Generic.Dictionary<string, string> Metadata { get; set; } = new();", entity);

        var dtos = run.Sources["Shop.Product.Dtos.g.cs"];
        Assert.Contains("public required global::System.Collections.Generic.Dictionary<string, string> Metadata", Record(dtos, "ProductResponse"));

        foreach (var request in new[] { "CreateProductRequest", "UpdateProductRequest", "BatchUpdateProductItem" })
        {
            Assert.Contains("[global::Haldus.MetadataAttribute]", Record(dtos, request));
            Assert.Contains("global::System.Collections.Generic.Dictionary<string, string>? Metadata { get; init; }", Record(dtos, request));
        }

        // PATCH'te değeri null olan anahtar silinir; bu yüzden değerler null olabilir.
        Assert.Contains("global::System.Collections.Generic.Dictionary<string, string?>? Metadata", Record(dtos, "PatchProductRequest"));
        Assert.Contains("[global::Haldus.MetadataAttribute]", Record(dtos, "PatchProductRequest"));

        Assert.Contains(
            "global::Haldus.HaldusDb.ConfigureMetadata(entity.Property(item => item.Metadata));",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Metadata_IsReplacedByPutAndMergedByPatch()
    {
        var endpoints = Run("app.MapCrud<Product>().Metadata();").AssertCompiles().Sources["Shop.Product.Endpoints.g.cs"];

        Assert.Contains("Metadata = global::Haldus.HaldusMetadata.Create(request.Metadata)", Handler(endpoints, "CreateAsync"));
        Assert.Contains("entity.Metadata = global::Haldus.HaldusMetadata.Create(request.Metadata);", Handler(endpoints, "UpdateAsync"));

        var patch = Handler(endpoints, "PatchAsync");
        Assert.Contains("""IsSet("Metadata")""", patch);
        Assert.Contains("entity.Metadata = global::Haldus.HaldusMetadata.Merge(entity.Metadata, request.Metadata);", patch);

        // Sınır isteğe değil, birleşmiş çantaya uygulanır.
        Assert.Contains("if (entity.Metadata.Count > global::Haldus.HaldusMetadata.MaxEntries)", patch);
    }

    [Fact]
    public void Metadata_FollowsTheArchetype()
    {
        var appendOnly = Run("app.MapAppendOnly<Product>().Metadata();").AssertCompiles();
        var readOnly = Run("app.MapReadOnly<Product>().Metadata();").AssertCompiles();

        // Yalnızca eklenen kayıtta çanta oluştururken yazılır; salt-okunur kayıtta yalnızca okunur.
        Assert.Contains("Metadata", Record(appendOnly.Sources["Shop.Product.Dtos.g.cs"], "CreateProductRequest"));
        Assert.DoesNotContain("HaldusMetadata.Merge", appendOnly.Sources["Shop.Product.Endpoints.g.cs"]);
        Assert.DoesNotContain("Request", readOnly.Sources["Shop.Product.Dtos.g.cs"]);
        Assert.Contains("Metadata = item.Metadata", readOnly.Sources["Shop.Product.Endpoints.g.cs"]);
    }

    [Fact]
    public void Metadata_IsPartOfTheResponseWhereverTheResourceIsReturned()
    {
        var run = Run("""
            app.MapCrud<Product>().Metadata();
            app.MapCrud<Tag>();
            app.MapAssociation<Product, Tag>();
            """).AssertCompiles();

        Assert.Contains("Metadata = item.Metadata", run.Sources["Shop.ProductTag.Endpoints.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapStore<Settings>().Metadata();", "Metadata cannot be combined with MapStore")]
    [InlineData("app.MapSingleton<Settings>().Metadata();", "Metadata cannot be combined with MapSingleton")]
    [InlineData("app.MapLookup<Currency>().Metadata();", "Metadata cannot be combined with MapLookup")]
    public void Metadata_IsOnlyForCollectionsWhoseKeyTheServerGenerates(string body, string message) =>
        AssertSingle(Run(body), "HALDUS007", message);

    [Fact]
    public void FieldExtensionsSnapshot()
    {
        var run = Run("""
            app.MapCrud<Coupon>()
                .Auditable()
                .KeyStrategy(KeyStrategy.Prefixed, "cpn")
                .NaturalKey(coupon => coupon.Code)
                .CreateOnly(coupon => coupon.Code, coupon => coupon.Percent)
                .Sortable(coupon => coupon.Code, coupon => coupon.CreatedAt)
                .Metadata();
            """).AssertCompiles();

        Snapshot.Verify(run);
    }
}
