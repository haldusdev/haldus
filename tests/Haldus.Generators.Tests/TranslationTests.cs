namespace Haldus.Generators.Tests;

/// <summary>Başka dillere çevrilebilen alanlar (<c>Translatable</c>).</summary>
public sealed class TranslationTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum PageStatus { Draft, Live }

            public partial class Page
            {
                [System.ComponentModel.DataAnnotations.Required]
                [System.ComponentModel.DataAnnotations.MaxLength(200)]
                public string Title { get; set; } = "";

                [System.ComponentModel.DataAnnotations.MaxLength(5000)]
                public string? Body { get; set; }

                public int Views { get; set; }

                public string Slug { get; private set; } = "";
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
    public void Translatable_StoresTranslationsBesideTheSourceText()
    {
        var run = Run("app.MapCrud<Page>().Translatable(page => page.Title, page => page.Body);").AssertCompiles();

        var entity = run.Sources["Shop.Page.Entity.g.cs"];
        Assert.Contains("global::Haldus.ITranslatable", entity);
        Assert.Contains(
            "public global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.Dictionary<string, string>> Translations { get; set; } = new();",
            entity);

        // Çeviriler yanıtta görünür; oluşturma ve güncelleme isteklerinde yer almaz.
        var dtos = run.Sources["Shop.Page.Dtos.g.cs"];
        var response = dtos.Substring(0, dtos.IndexOf("record CreatePageRequest", StringComparison.Ordinal));
        var writes = dtos.Substring(response.Length, dtos.IndexOf("record TranslatePageRequest", StringComparison.Ordinal) - response.Length);
        Assert.Contains("Translations { get; init; }", response);
        Assert.DoesNotContain("Translations", writes);

        // Çeviri isteği yalnızca sayılan alanları taşır; alanın kuralları kopyalanır ama hiçbiri zorunlu değildir.
        var request = dtos.Substring(dtos.IndexOf("record TranslatePageRequest", StringComparison.Ordinal));
        Assert.Contains("MaxLengthAttribute(200)]\n    public string? Title { get; init; }", request);
        Assert.Contains("MaxLengthAttribute(5000)]\n    public string? Body { get; init; }", request);
        Assert.DoesNotContain("RequiredAttribute", request);
        Assert.DoesNotContain("Views", request);

        Assert.Contains(
            "global::Haldus.HaldusDb.ConfigureTranslations(entity.Property(item => item.Translations));",
            run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Translatable_WritesGoThroughTheGuardsOfAnUpdate()
    {
        var run = Run("""
            app.MapCrud<Page>().ConcurrencyChecked().Workflow<PageStatus>().EditableIn(PageStatus.Draft).Approvable()
                .Translatable(page => page.Title);
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.Page.Endpoints.g.cs"];
        Assert.Contains("""MapPut("/{id:guid}/translations/{locale}", TranslateAsync)""", endpoints);
        Assert.Contains("""MapDelete("/{id:guid}/translations/{locale}", RemoveTranslationAsync)""", endpoints);
        Assert.Contains("AddEndpointFilter<global::Haldus.HaldusValidationFilter<TranslatePageRequest>>()", endpoints);

        foreach (var name in new[] { "TranslateAsync", "RemoveTranslationAsync" })
        {
            var handler = Handler(endpoints, name);

            // Dil etiketi her şeyden önce doğrulanır.
            Assert.Contains("if (global::Haldus.HaldusTranslations.Normalize(locale) is not { } language)", handler);
            Assert.Contains("global::Haldus.HaldusETag.ReadIfMatch(httpContext)", handler);

            // Onaylanmış kayıt ve düzenlenemeyen durumdaki kayıt çevrilemez.
            Assert.Contains("ApprovalStatus.Approved", handler);
            Assert.Contains("PageStatus.Draft", handler);
            Assert.DoesNotContain("IgnoreQueryFilters", handler);
        }

        Assert.Contains("new(\"title\", request.Title)", Handler(endpoints, "TranslateAsync"));
        Assert.Contains("if (translations.Count > global::Haldus.HaldusTranslations.MaxLocales)", Handler(endpoints, "TranslateAsync"));

        // Çeviri içeriktir: değişince toplanan onaylar düşer.
        Assert.Contains("\"Translations\"", run.Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void Translatable_ReadsAreLocalizedOnlyWhenALocaleIsAsked()
    {
        var run = Run("app.MapCrud<Page>().Publishable().Cloneable().Translatable(page => page.Title, page => page.Body);").AssertCompiles();
        var endpoints = run.Sources["Shop.Page.Endpoints.g.cs"];

        Assert.Contains("string? locale,", Handler(endpoints, "ListAsync"));
        Assert.Contains("items = items.ConvertAll(item => Localize(item, locale));", Handler(endpoints, "ListAsync"));
        Assert.Contains("Ok(Localize(ToResponse(entity), locale))", Handler(endpoints, "GetAsync"));
        Assert.Contains("Localize(ToPublished(copy), locale)", Handler(endpoints, "GetPublishedAsync"));
        Assert.Contains(".Select(item => Localize(item, locale))", Handler(endpoints, "ListPublishedAsync"));

        // Yazma yanıtları her zaman kaynak dildedir.
        Assert.DoesNotContain("Localize", Handler(endpoints, "UpdateAsync"));
        Assert.DoesNotContain("Localize", Handler(endpoints, "CreateAsync"));

        Assert.Contains(
            "Title = global::Haldus.HaldusTranslations.Pick(response.Translations, language, \"title\") ?? response.Title",
            endpoints);

        // Aynı kaynağın başka bir kaydından doğan kayıt (yayın kopyası, kopya) çevirileri de taşır.
        Assert.Contains("Translations = global::Haldus.HaldusTranslations.Copy(source.Translations)", Handler(endpoints, "PublishAsync"));
        Assert.Contains("Translations = global::Haldus.HaldusTranslations.Copy(source.Translations)", Handler(endpoints, "CloneAsync"));

        // Çevrilemeyen bir kaynağın okuma uçları değişmez.
        var plain = Run("app.MapCrud<Page>();").AssertCompiles().Sources["Shop.Page.Endpoints.g.cs"];
        Assert.DoesNotContain("locale", plain);
    }

    [Theory]
    [InlineData("app.MapCrud<Page>().Translatable();", "HALDUS010", "Translatable needs at least one field")]
    [InlineData("app.MapCrud<Page>().Translatable(page => page.Views);", "HALDUS010", "cannot be translated")]
    [InlineData("app.MapCrud<Page>().Translatable(page => page.Slug);", "HALDUS010", "cannot be translated")]
    [InlineData("app.MapCrud<Page>().Translatable(page => page.Title.Length);", "HALDUS010", "page => page.Title.Length")]
    [InlineData("app.MapReadOnly<Page>().Translatable(page => page.Title);", "HALDUS007", "Translatable")]
    [InlineData("app.MapCrud<Page>().Without(Operations.Update, Operations.Patch).Translatable(page => page.Title);", "HALDUS007", "Translatable requires the Update or Patch operation")]
    [InlineData("var pages = app.MapCrud<Page>().Translatable(page => page.Title); pages.MapAction(\"translations\", page => { });", "HALDUS007", "translations")]
    public void Translatable_RejectsDeclarationsThatCannotWork(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void TranslationSnapshot()
    {
        var run = Run("app.MapCrud<Page>().Translatable(page => page.Title, page => page.Body);").AssertCompiles();

        Snapshot.Verify(run);
    }

    /// <summary>Bir handler metodunun gövdesi. Adın çağrıldığı yerler değil tanımlandığı yer aranır.</summary>
    private static string Handler(string endpoints, string name)
    {
        var from = endpoints.IndexOf($" {name}(\n", StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{name}' is not declared");

        var to = endpoints.IndexOf("\n    }\n", from, StringComparison.Ordinal);
        return endpoints.Substring(from, to - from);
    }
}
