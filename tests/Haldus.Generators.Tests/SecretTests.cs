namespace Haldus.Generators.Tests;

/// <summary>Sırlar: yalnızca bir kez gösterilen sır, döndürme ve token ile oturumsuz erişim.</summary>
public sealed class SecretTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum InvitationStatus { Pending, Accepted, Revoked }

            public partial class ApiKey
            {
                public string Name { get; set; } = "";
            }

            public partial class Invitation
            {
                public string Email { get; set; } = "";
            }

            public partial class Board
            {
                public string Name { get; set; } = "";
            }

            public partial class Settings
            {
                public string Theme { get; set; } = "";
            }

            public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
        }
        """;

    private const string Invitations = """
        var invitations = app.MapCrud<Invitation>()
            .MultiTenant()
            .Workflow<InvitationStatus>(initial: InvitationStatus.Pending)
            .TokenAccess(prefix: "inv", "accept");
        invitations.MapTransition("accept", InvitationStatus.Pending, InvitationStatus.Accepted);
        invitations.MapTransition("revoke", InvitationStatus.Pending, InvitationStatus.Revoked);
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

    private static string Record(string source, string name)
    {
        var start = source.IndexOf($"record {name}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{name}' was not generated.");

        var end = source.IndexOf("\npublic ", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

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

    // ── Yalnızca bir kez gösterilen sır ────────────────────────────────────────────────────────────────

    [Fact]
    public void SecretOnce_StoresADigestAndAHintButNeverTheSecret()
    {
        var run = Run("""app.MapCrud<ApiKey>().SecretOnce(prefix: "sk");""").AssertCompiles();

        var entity = run.Sources["Shop.ApiKey.Entity.g.cs"];
        Assert.Contains("global::Haldus.IHasSecret", entity);
        Assert.Contains("public string SecretHash { get; set; } = \"\";", entity);
        Assert.Contains("public string SecretHint { get; set; } = \"\";", entity);
        Assert.DoesNotContain("string Secret {", entity);
        Assert.DoesNotContain("SecretCipher", entity);

        // Yanıtta ipucu her zaman, sır yalnızca üretildiği yanıtta yer alır; özet hiç yer almaz.
        var dtos = run.Sources["Shop.ApiKey.Dtos.g.cs"];
        var response = Record(dtos, "ApiKeyResponse");
        Assert.Contains("public required string SecretHint", response);
        Assert.Contains("JsonIgnoreCondition.WhenWritingNull)]\n    public string? Secret { get; init; }", response);
        Assert.DoesNotContain("SecretHash", response);

        foreach (var request in new[] { "CreateApiKeyRequest", "UpdateApiKeyRequest", "PatchApiKeyRequest" })
        {
            Assert.DoesNotContain("Secret", Record(dtos, request));
        }

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.HasIndex(item => item.SecretHash).IsUnique();", module);
    }

    [Fact]
    public void SecretOnce_IsGeneratedByTheServerAndShownOnlyInTheCreateResponse()
    {
        var endpoints = Run("""app.MapCrud<ApiKey>().SecretOnce(prefix: "sk");""").AssertCompiles().Sources["Shop.ApiKey.Endpoints.g.cs"];

        var create = Handler(endpoints, "CreateAsync");
        Assert.Contains("""var secret = global::Haldus.HaldusSecrets.Generate("sk");""", create);
        Assert.Contains("SecretHash = global::Haldus.HaldusSecrets.Hash(secret), SecretHint = global::Haldus.HaldusSecrets.Hint(secret)", create);
        Assert.Contains("ToResponse(entity) with { Secret = secret }", create);

        // Başka hiçbir yanıt sırrı taşımaz.
        Assert.Equal(1, endpoints.Split("Secret = secret").Length - 1);
        Assert.DoesNotContain("rotate", endpoints);
    }

    [Fact]
    public void SecretOnce_Recoverable_AlsoStoresTheSecretEncrypted()
    {
        var run = Run("""app.MapCrud<ApiKey>().SecretOnce(prefix: "whsec", recoverable: true);""").AssertCompiles();

        var entity = run.Sources["Shop.ApiKey.Entity.g.cs"];
        Assert.Contains("global::Haldus.IHasRecoverableSecret", entity);
        Assert.Contains("public string SecretCipher { get; set; } = \"\";", entity);

        Assert.Contains(
            "SecretCipher = global::Haldus.HaldusSecretProtection.Protect<ApiKey>(httpContext.RequestServices, secret)",
            Handler(run.Sources["Shop.ApiKey.Endpoints.g.cs"], "CreateAsync"));
        Assert.DoesNotContain("SecretCipher", Record(run.Sources["Shop.ApiKey.Dtos.g.cs"], "ApiKeyResponse"));
    }

    // ── Döndürme ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rotatable_ReplacesTheSecretAtOnceUnlessAnOverlapIsDeclared()
    {
        var immediate = Run("""app.MapCrud<ApiKey>().ConcurrencyChecked().SecretOnce(prefix: "sk").Rotatable();""").AssertCompiles();
        var endpoints = immediate.Sources["Shop.ApiKey.Endpoints.g.cs"];
        Assert.Contains("""group.MapPost("/{id:guid}/rotate", RotateAsync)""", endpoints);

        var rotate = Handler(endpoints, "RotateAsync");
        Assert.Contains("""var secret = global::Haldus.HaldusSecrets.Generate("sk");""", rotate);
        Assert.Contains("entity.SecretHash = global::Haldus.HaldusSecrets.Hash(secret);", rotate);
        Assert.Contains("ToResponse(entity) with { Secret = secret }", rotate);
        Assert.DoesNotContain("PreviousSecretHash", rotate);
        Assert.DoesNotContain("PreviousSecretHash", immediate.Sources["Shop.ApiKey.Entity.g.cs"]);

        // Döndürme bir yazmadır; sürüm kontrollü kaynakta If-Match ister.
        Assert.Contains("global::Haldus.HaldusETag.ReadIfMatch(httpContext)", rotate);

        var overlapping = Run("""app.MapCrud<ApiKey>().SecretOnce(prefix: "sk").Rotatable(overlap: TimeSpan.FromHours(24));""").AssertCompiles();
        Assert.Contains("global::Haldus.IHasRotatingSecret", overlapping.Sources["Shop.ApiKey.Entity.g.cs"]);
        Assert.Contains("public string? PreviousSecretHash { get; set; }", overlapping.Sources["Shop.ApiKey.Entity.g.cs"]);

        var keeping = Handler(overlapping.Sources["Shop.ApiKey.Endpoints.g.cs"], "RotateAsync");
        Assert.Contains("entity.PreviousSecretHash = entity.SecretHash;", keeping);
        Assert.Contains($"global::System.TimeSpan.FromTicks({TimeSpan.FromHours(24).Ticks}L)", keeping);
        Assert.True(keeping.IndexOf("entity.PreviousSecretHash = entity.SecretHash;", StringComparison.Ordinal) < keeping.IndexOf("entity.SecretHash = ", StringComparison.Ordinal));
        Assert.DoesNotContain("PreviousSecret", Record(overlapping.Sources["Shop.ApiKey.Dtos.g.cs"], "ApiKeyResponse"));
    }

    // ── Token ile erişim ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TokenAccess_ReadsTheRecordByItsTokenWithoutASession()
    {
        var run = Run(Invitations).AssertCompiles();

        Assert.Contains("global::Haldus.IHasAccessToken", run.Sources["Shop.Invitation.Entity.g.cs"]);
        Assert.Contains("entity.HasIndex(item => item.TokenHash).IsUnique();", run.Sources["HaldusModule.g.cs"]);

        var response = Record(run.Sources["Shop.Invitation.Dtos.g.cs"], "InvitationResponse");
        Assert.Contains("public string? Token { get; init; }", response);
        Assert.DoesNotContain("TokenHash", response);

        var endpoints = run.Sources["Shop.Invitation.Endpoints.g.cs"];
        Assert.Contains(
            "group.MapGet(\"/by-token\", GetByTokenAsync)\n            .WithName(\"GetInvitationByToken\")\n            .WithMetadata(global::Haldus.HaldusTokenAccess.Instance)\n            .AllowAnonymous()",
            endpoints);

        // Yalnızca kiracı filtresi kaldırılır: silinmiş veya süresi dolmuş kayıt token ile de bulunamaz.
        var read = Handler(endpoints, "GetByTokenAsync");
        Assert.Contains("global::Haldus.HaldusTokenAccess.ReadHash(httpContext)", read);
        Assert.Contains(".IgnoreQueryFilters(new[] { global::Haldus.HaldusFilters.Tenant })", read);
        Assert.DoesNotContain("IgnoreQueryFilters()", read);
        Assert.Contains("TokenInvalid(messages)", read);

        Assert.Contains("ToResponse(entity) with { Token = token }", Handler(endpoints, "CreateAsync"));
    }

    [Fact]
    public void TokenAccess_RunsOnlyTheListedTransitionsThroughTheRealOnes()
    {
        var endpoints = Run(Invitations).AssertCompiles().Sources["Shop.Invitation.Endpoints.g.cs"];

        Assert.Contains("""group.MapPost("/by-token/accept", (""", endpoints);
        Assert.DoesNotContain("by-token/revoke", endpoints);

        // Token'dan kayıt bulunur ve asıl geçiş çağrılır; kuralları token ile gelene de aynen uygulanır.
        var accept = Handler(endpoints, "TokenTransitionAcceptAsync");
        Assert.Contains("return await TransitionAcceptAsync(found.Id, apply, httpContext, contexts, messages, cancellationToken);", accept);

        // Geçiş, kaydın kiracısı adına çalışır.
        Assert.Contains("global::Haldus.HaldusAmbient.Use(\n            found.TenantId,", accept);
    }

    [Fact]
    public void TokenAccess_OnAResourceWithoutATenant_KeepsTheRequestsIdentity()
    {
        var endpoints = Run("""
            var invitations = app.MapCrud<Invitation>().TokenAccess("inv", "accept");
            invitations.MapAction("accept", invitation => { });
            """).AssertCompiles().Sources["Shop.Invitation.Endpoints.g.cs"];

        Assert.DoesNotContain("IgnoreQueryFilters", Handler(endpoints, "GetByTokenAsync"));
        Assert.DoesNotContain("HaldusAmbient", Handler(endpoints, "TokenActionAcceptAsync"));
    }

    [Theory]
    [InlineData("app.MapCrud<ApiKey>().SecretOnce(prefix: \"SK\");", "HALDUS010", "SecretOnce needs a prefix")]
    [InlineData("var prefix = \"sk\"; app.MapCrud<ApiKey>().SecretOnce(prefix);", "HALDUS010", "The arguments of SecretOnce must be constants")]
    [InlineData("app.MapCrud<ApiKey>().TokenAccess(\"\");", "HALDUS010", "TokenAccess needs a prefix")]
    [InlineData(
        "var hours = 3; app.MapCrud<ApiKey>().SecretOnce(\"sk\").Rotatable(TimeSpan.FromHours(hours));",
        "HALDUS010",
        "The overlap of Rotatable must be readable at compile time")]
    [InlineData("app.MapCrud<ApiKey>().Rotatable();", "HALDUS007", "declare SecretOnce(prefix: ...) as well")]
    [InlineData("app.MapStore<Settings>().SecretOnce(\"sk\");", "HALDUS007", "SecretOnce cannot be combined with MapStore")]
    [InlineData("app.MapCrud<ApiKey>().SecretOnce(\"sk\").Batch();", "HALDUS007", "SecretOnce cannot be combined with Batch")]
    [InlineData("app.MapCrud<ApiKey>().SecretOnce(\"sk\").Cloneable();", "HALDUS007", "SecretOnce cannot be combined with Cloneable")]
    [InlineData("app.MapCrud<ApiKey>().Without(CrudOperation.Create).SecretOnce(\"sk\");", "HALDUS007", "SecretOnce requires the Create operation")]
    [InlineData("app.MapCrud<Board>(); app.MapCrud<Invitation>().Under<Board>().TokenAccess(\"inv\");", "HALDUS007", "TokenAccess cannot be combined with Under<T>()")]
    [InlineData("app.MapCrud<Invitation>().OwnedByCurrentUser().TokenAccess(\"inv\");", "HALDUS007", "TokenAccess cannot be combined with OwnedByCurrentUser")]
    [InlineData("app.MapCrud<Invitation>().TokenAccess(\"inv\", \"accept\");", "HALDUS007", "TokenAccess lists 'accept'")]
    [InlineData(
        "var keys = app.MapCrud<ApiKey>().SecretOnce(\"sk\").Rotatable(); keys.MapAction(\"rotate\", key => { });",
        "HALDUS007",
        "Rotatable")]
    public void InvalidDeclarations_AreReported(string body, string id, string message) =>
        AssertSingle(Run(body), id, message);

    [Fact]
    public void SecretsSnapshot()
    {
        var run = Run($$"""
            app.MapCrud<ApiKey>().SecretOnce(prefix: "sk", recoverable: true).Rotatable(overlap: TimeSpan.FromHours(1));
            {{Invitations}}
            """).AssertCompiles();

        Snapshot.Verify(run);
    }
}
