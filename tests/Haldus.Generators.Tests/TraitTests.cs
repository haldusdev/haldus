namespace Haldus.Generators.Tests;

public sealed class TraitTests
{
    private const string Product = """
        namespace Shop
        {
            public partial class Product
            {
                public string Name { get; set; } = "";
            }
        }
        """;

    private static GeneratorRun RunWithChain(string chain, string entity = Product) =>
        GeneratorHarness.Run($$"""
            {{entity}}

            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app) => app.MapCrud<Shop.Product>(){{chain}};
            }
            """);

    [Fact]
    public void NoTraits_AddsNoInterfacesOrTraitMembers()
    {
        var run = RunWithChain(string.Empty).AssertCompiles();

        var entity = run.Sources["Shop.Product.Entity.g.cs"];
        Assert.Contains("partial class Product\n", entity);
        Assert.DoesNotContain("global::Haldus.I", entity);
    }

    [Theory]
    [InlineData(".MultiTenant()", "global::Haldus.IMultiTenant", "public global::System.Guid TenantId { get; set; }")]
    [InlineData(".SoftDeletable()", "global::Haldus.ISoftDeletable", "public global::System.DateTimeOffset? DeletedAt { get; set; }")]
    [InlineData(".ConcurrencyChecked()", "global::Haldus.IConcurrencyChecked", "public uint Version { get; set; }")]
    [InlineData(".Archivable()", "global::Haldus.IArchivable", "public global::System.DateTimeOffset? ArchivedAt { get; set; }")]
    [InlineData(".Auditable()", "global::Haldus.IAuditable", "public global::System.DateTimeOffset CreatedAt { get; set; }")]
    public void Trait_AddsItsInterfaceAndMember(string chain, string expectedInterface, string expectedMember)
    {
        var run = RunWithChain(chain).AssertCompiles();

        var entity = run.Sources["Shop.Product.Entity.g.cs"];
        Assert.Contains(expectedInterface, entity);
        Assert.Contains(expectedMember, entity);
    }

    [Fact]
    public void Auditable_TracksUserOnlyWhenAsked()
    {
        var withoutUser = RunWithChain(".Auditable()").AssertCompiles().Sources["Shop.Product.Entity.g.cs"];
        var named = RunWithChain(".Auditable(trackUser: true)").AssertCompiles().Sources["Shop.Product.Entity.g.cs"];
        var positional = RunWithChain(".Auditable(true)").AssertCompiles().Sources["Shop.Product.Entity.g.cs"];
        var explicitFalse = RunWithChain(".Auditable(trackUser: false)").AssertCompiles().Sources["Shop.Product.Entity.g.cs"];

        Assert.DoesNotContain("CreatedBy", withoutUser);
        Assert.DoesNotContain("CreatedBy", explicitFalse);
        Assert.Contains("global::Haldus.IUserAuditable", named);
        Assert.Contains("public string? CreatedBy { get; set; }", named);
        Assert.Contains("public string? UpdatedBy { get; set; }", positional);
    }

    [Fact]
    public void TraitMembers_StayOutOfRequestsAndTenantAndVersionStayOutOfResponses()
    {
        var run = RunWithChain(".MultiTenant().Auditable(true).SoftDeletable().ConcurrencyChecked().Archivable()")
            .AssertCompiles();

        var dtos = run.Sources["Shop.Product.Dtos.g.cs"];
        var response = dtos[..dtos.IndexOf("CreateProductRequest", StringComparison.Ordinal)];
        var requests = dtos[response.Length..];

        foreach (var member in new[] { "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "DeletedAt", "ArchivedAt" })
        {
            Assert.Contains($" {member} {{ get; init; }}", response);
            Assert.DoesNotContain(member, requests);
        }

        Assert.DoesNotContain("TenantId", dtos);
        Assert.DoesNotContain("Version", dtos);
    }

    [Fact]
    public void TraitMemberDeclaredByUser_IsNotGeneratedAgainAndStaysOutOfRequests()
    {
        var run = RunWithChain(
            ".MultiTenant().SoftDeletable()",
            """
            namespace Shop
            {
                public partial class Product
                {
                    public string Name { get; set; } = "";
                    public Guid TenantId { get; set; }
                }
            }
            """).AssertCompiles();

        var entity = run.Sources["Shop.Product.Entity.g.cs"];
        Assert.Contains("global::Haldus.IMultiTenant", entity);
        Assert.DoesNotContain("TenantId", entity);
        Assert.Contains("DeletedAt", entity);
        Assert.DoesNotContain("TenantId", run.Sources["Shop.Product.Dtos.g.cs"]);
    }

    [Fact]
    public void Traits_AreReadThroughAspNetCallsInTheChain()
    {
        var run = RunWithChain(".RequireAuthorization().SoftDeletable().WithTags(\"x\").Archivable()").AssertCompiles();

        var entity = run.Sources["Shop.Product.Entity.g.cs"];
        Assert.Contains("global::Haldus.ISoftDeletable", entity);
        Assert.Contains("global::Haldus.IArchivable", entity);
    }

    [Fact]
    public void MultiTenant_AddsTenantFilterIndexAndGuard()
    {
        var run = RunWithChain(".MultiTenant()").AssertCompiles();

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains(
            "entity.HasQueryFilter(global::Haldus.HaldusFilters.Tenant, item => item.TenantId == global::Haldus.HaldusScope.TenantId(context));",
            module);
        Assert.Contains("entity.HasIndex(item => item.TenantId);", module);
        Assert.Contains(
            "group.AddEndpointFilter<global::Haldus.HaldusTenantRequiredFilter>();",
            run.Sources["Shop.Product.Endpoints.g.cs"]);
    }

    [Fact]
    public void ConcurrencyChecked_RequiresIfMatchOnWritesOnly()
    {
        var run = RunWithChain(".ConcurrencyChecked()").AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Equal(3, CountOccurrences(endpoints, "global::Haldus.HaldusETag.ReadIfMatch(httpContext)"));
        Assert.Equal(4, CountOccurrences(endpoints, "global::Haldus.HaldusETag.Write(httpContext, entity.Version)"));
        Assert.Contains("entity.Property(item => item.Version).IsRowVersion();", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void ConcurrencyChecked_GuardsRestorePurgeAndArchiveToo()
    {
        var run = RunWithChain(".ConcurrencyChecked().SoftDeletable().Archivable()").AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];

        // PUT, PATCH, DELETE, restore, purge, archive, unarchive
        Assert.Equal(7, CountOccurrences(endpoints, "global::Haldus.HaldusETag.ReadIfMatch(httpContext)"));
        Assert.Contains("query.Where(item => item.Version == expectedVersion).ExecuteDeleteAsync", endpoints);
    }

    [Fact]
    public void Purge_OnlyTargetsSoftDeletedRecords()
    {
        var run = RunWithChain(".SoftDeletable()").AssertCompiles();

        Assert.Contains(
            ".Where(item => item.Id == id && item.DeletedAt != null);",
            run.Sources["Shop.Product.Endpoints.g.cs"]);
    }

    [Fact]
    public void PatchRequest_KeepsTheSemanticsOfTheOriginalRequiredRule()
    {
        var run = RunWithChain(
            string.Empty,
            """
            namespace Shop
            {
                public partial class Product
                {
                    [Required]
                    public string Strict { get; set; } = "";

                    [Required(AllowEmptyStrings = true, ErrorMessage = "Give a code")]
                    public string Code { get; set; } = "";

                    public string Implicit { get; set; } = "";

                    [Required]
                    public string? NullableButRequired { get; set; }

                    public int Count { get; set; }

                    public string? Optional { get; set; }
                }
            }
            """).AssertCompiles();

        var dtos = run.Sources["Shop.Product.Dtos.g.cs"];
        var patch = dtos[dtos.IndexOf("PatchProductRequest", StringComparison.Ordinal)..];

        // Kullanıcının Required'ı boş metni reddeder; PATCH'teki karşılığı da reddetmelidir.
        Assert.Contains("[global::Haldus.NotNullWhenSetAttribute]\n    public string? Strict", patch);
        Assert.Contains(
            "[global::Haldus.NotNullWhenSetAttribute(AllowEmptyStrings = true, ErrorMessage = \"Give a code\")]\n    public string? Code",
            patch);
        Assert.Contains("[global::Haldus.NotNullWhenSetAttribute(AllowEmptyStrings = true)]\n    public string? Implicit", patch);
        Assert.Contains("[global::Haldus.NotNullWhenSetAttribute]\n    public string? NullableButRequired", patch);
        Assert.Contains("[global::Haldus.NotNullWhenSetAttribute(AllowEmptyStrings = true)]\n    public int? Count", patch);
        Assert.Contains("    }\n\n    public string? Optional", patch);
        Assert.DoesNotContain("RequiredAttribute", patch);
    }

    [Fact]
    public void SoftDeletable_AddsRestoreAndPurgeAndFilter()
    {
        var run = RunWithChain(".SoftDeletable()").AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Contains("""MapPost("/{id:guid}/restore", RestoreAsync)""", endpoints);
        Assert.Contains("""MapPost("/{id:guid}/purge", PurgeAsync)""", endpoints);
        Assert.Contains("bool? includeDeleted,", endpoints);
        Assert.Contains(
            "entity.HasQueryFilter(global::Haldus.HaldusFilters.SoftDelete, item => item.DeletedAt == null);",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Without_RemovesOperationsAndTheirDtos()
    {
        var run = RunWithChain(".Without(CrudOperation.Delete, CrudOperation.Patch)").AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Contains("ListAsync", endpoints);
        Assert.Contains("UpdateAsync", endpoints);
        Assert.DoesNotContain("DeleteAsync", endpoints);
        Assert.DoesNotContain("PatchAsync", endpoints);
        Assert.DoesNotContain("PatchProductRequest", run.Sources["Shop.Product.Dtos.g.cs"]);
    }

    [Fact]
    public void Only_KeepsJustTheListedOperations()
    {
        var run = RunWithChain(".Only(CrudOperation.List, CrudOperation.Get)").AssertCompiles();

        var endpoints = run.Sources["Shop.Product.Endpoints.g.cs"];
        Assert.Contains("ListAsync", endpoints);
        Assert.Contains("GetAsync", endpoints);
        Assert.DoesNotContain("CreateAsync", endpoints);
        Assert.DoesNotContain("UpdateAsync", endpoints);
        Assert.DoesNotContain("DeleteAsync", endpoints);

        var dtos = run.Sources["Shop.Product.Dtos.g.cs"];
        Assert.Contains("ProductResponse", dtos);
        Assert.DoesNotContain("Request", dtos);
    }

    [Fact]
    public void DeclarationOnAVariable_ReportsHaldus004()
    {
        var run = GeneratorHarness.Run($$"""
            {{Product}}

            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    var products = app.MapCrud<Shop.Product>();
                    products.SoftDeletable();
                }
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("HALDUS004", diagnostic.Id);
        Assert.Contains("SoftDeletable", diagnostic.GetMessage());
        Assert.DoesNotContain("ISoftDeletable", run.Sources["Shop.Product.Entity.g.cs"]);
    }

    [Fact]
    public void SameNamedMethodFromAnotherLibrary_IsNotReportedAsDetached()
    {
        var run = GeneratorHarness.Run($$"""
            {{Product}}

            public static class OtherExtensions
            {
                public static string Archivable(this string value) => value;
            }

            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapCrud<Shop.Product>();
                    _ = "x".Archivable();
                }
            }
            """).AssertCompiles();

        Assert.DoesNotContain("IArchivable", run.Sources["Shop.Product.Entity.g.cs"]);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
