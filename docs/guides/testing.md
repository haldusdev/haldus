# Uygulamanızı test etmek

Haldus'un ürettiği kod sıradan Minimal API uçlarıdır; onları ASP.NET Core'un her uygulamada kullandığı yoldan test
edersiniz: uygulamayı bellekte başlatan `WebApplicationFactory` ve gerçek bir veritabanı. Haldus kilit, sayaç ve kuyruk
için PostgreSQL'e özgü SQL çalıştırdığı için testlerde de gerçek bir PostgreSQL kullanın; bu sayfa onu
[Testcontainers](https://dotnet.testcontainers.org) ile bir Docker kapsayıcısında başlatır. Haldus'un kendi
entegrasyon testleri ([tests/Haldus.IntegrationTests](../../tests/Haldus.IntegrationTests)) aynı yoldan yazılmıştır.

Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır.

## İçindekiler

- [Test projesi](#test-projesi)
- [Test edilen uygulama](#test-edilen-uygulama)
- [Uygulamayı ve veritabanını başlatmak](#uygulamayı-ve-veritabanını-başlatmak)
- [Kimliği testte vermek](#kimliği-testte-vermek)
- [Testler](#testler)
- [Veritabanına doğrudan yazmak](#veritabanına-doğrudan-yazmak)
- [Arka plan işlerini ve zamanı sınamak](#arka-plan-işlerini-ve-zamanı-sınamak)
- [Üretilen kodu görmek](#üretilen-kodu-görmek)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Test projesi

Test projesi uygulamanın projesine başvurur ve iki pakete ihtiyaç duyar: `Microsoft.AspNetCore.Mvc.Testing`
(`WebApplicationFactory`) ve `Testcontainers.PostgreSql`. Bu sayfadaki örnekler xUnit v3 ile yazılmıştır:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <PackageReference Include="Testcontainers.PostgreSql" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\MyApp\MyApp.csproj" />
  </ItemGroup>

</Project>
```

Testler çalışırken Docker'ın açık olması gerekir. Test projesi Haldus'a ayrıca başvurmaz; generator uygulamanın
projesinde çalışır ve test projesi üretilen tipleri (`NoteResponse`, `CreateNoteRequest`...) uygulamanın derlemesinden
kullanır.

## Test edilen uygulama

Aşağıdaki testler bu uygulamayı sınar: kiracıya ait notlar.

<!-- example: app-tests -->
```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();

// Gerçek uygulamada burada bir şema eklenir (ör. AddJwtBearer); testler kendi şemalarını ekler.
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseHaldus();
app.UseAuthentication();
app.UseAuthorization();
app.MapCrud<Note>().MultiTenant();

app.Run();

// WebApplicationFactory<Program> testten bu tipe ulaşır.
public partial class Program;

public partial class Note
{
    [MaxLength(200)]
    public string Text { get; set; } = "";
}
```

Bağlantı dizesi yapılandırmadan okunur; test onu kapsayıcının adresiyle değiştirir.

## Uygulamayı ve veritabanını başlatmak

Kapsayıcıyı başlatmak birkaç saniye sürer. Bu yüzden onu test başına değil, bir fixture'da bir kez başlatın ve
testler arasında paylaşın:

<!-- example: app-tests -->
```csharp
using Haldus;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class AppFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();

    private WebApplicationFactory<Program>? _factory;

    public WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("The fixture has not been initialized.");

    /// <summary>Verilen kullanıcı (ve varsa kiracı) olarak istek yapan bir istemci.</summary>
    public HttpClient CreateClient(string user, Guid? tenant = null)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);

        if (tenant is { } tenantId)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenantId.ToString());
        }

        return client;
    }

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", _database.GetConnectionString())
            .ConfigureTestServices(services => services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { })));

        // Tablolar modelden kurulur; Haldus'un kendi tabloları (sayaçlar, idempotency kayıtları...) da modeldedir.
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _database.DisposeAsync();
    }
}
```

`EnsureCreatedAsync()` tabloları modelden kurar ve migration'ları kullanmaz. Migration'larınızı da sınamak
istiyorsanız onun yerine `MigrateAsync()` çağırın.

## Kimliği testte vermek

Haldus kiracıyı ve kullanıcıyı `HttpContext.User` içindeki claim'lerden okur (`tenant_id` ve
`ClaimTypes.NameIdentifier`; bkz. [kimlik nereden okunur](tenancy-and-ownership.md#kimlik-nereden-okunur)). Testte gerçek
bir kimlik sağlayıcısı yoktur; iki yoldan birini seçersiniz.

**Test kimlik doğrulama şeması.** Yukarıdaki fixture, istekteki başlıklardan claim kuran bir şema ekler ve onu
varsayılan şema yapar. Haldus claim'leri üretimdeki gibi okur; uygulamanın yetki kuralları (`RequireAuthorization`,
`FallbackPolicy`) da çalışır. Bu, ASP.NET Core'un entegrasyon testleri için önerdiği yoldur:

<!-- example: app-tests -->
```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string TenantHeader = "X-Test-Tenant";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        List<Claim> claims = [new(ClaimTypes.NameIdentifier, user.ToString())];

        if (Request.Headers.TryGetValue(TenantHeader, out var tenant))
        {
            claims.Add(new Claim("tenant_id", tenant.ToString()));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
```

**Kimlik servislerini değiştirmek.** Kimliği claim'ler yerine doğrudan Haldus'un servislerine verebilirsiniz:
`ITenantProvider` ve `ICurrentUser` başlıklardan okuyan bir uygulamayla değiştirilir. Haldus'un kendi entegrasyon
testleri böyle çalışır (`DemoIdentity`). Bu yol kimlik doğrulamayı atlar: uygulamanız giriş yapılmış bir kullanıcı
istiyorsa (`RequireAuthorization`, `FallbackPolicy`) istekler yetkilendirmeye takılır. O durumda test şemasını kullanın.

<!-- example: app-tests -->
```csharp
using Haldus;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

// Kimliği doğrulamadan başlıklardan okur. Yalnızca test projesinde durur; uygulamaya asla eklenmez.
public sealed class HeaderIdentity(IHttpContextAccessor accessor) : ITenantProvider, ICurrentUser
{
    public Guid? TenantId =>
        Guid.TryParse(accessor.HttpContext?.Request.Headers["X-Tenant-Id"], out var tenantId) ? tenantId : null;

    public string? UserId =>
        accessor.HttpContext?.Request.Headers["X-User-Id"] is { Count: > 0 } values ? values.ToString() : null;
}

public static class HeaderIdentityApp
{
    public static WebApplicationFactory<Program> Create(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", connectionString)
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<HeaderIdentity>();
                services.AddSingleton<ITenantProvider>(provider => provider.GetRequiredService<HeaderIdentity>());
                services.AddSingleton<ICurrentUser>(provider => provider.GetRequiredService<HeaderIdentity>());
            }));
}
```

`ConfigureTestServices` uygulamanın kendi kayıtlarından sonra çalışır; buradaki kayıt Haldus'un varsayılanının yerine
geçer.

## Testler

Her test kendi kiracısını ve kullanıcısını üretirse testler aynı veritabanını paylaşabilir ve birbirini etkilemez;
veritabanını testler arasında temizlemeniz gerekmez. Haldus'un entegrasyon testleri de böyle yazılmıştır.

<!-- example: app-tests -->
```csharp
using System.Net;
using System.Net.Http.Json;
using Haldus;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class NoteTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Notes_AreVisibleOnlyToTheirTenant()
    {
        var owner = app.CreateClient("ada", Guid.NewGuid());
        var stranger = app.CreateClient("bob", Guid.NewGuid());

        var created = await owner.PostAsJsonAsync("/notes", new CreateNoteRequest { Text = "Hello" }, CancellationToken);
        var note = await created.Content.ReadFromJsonAsync<NoteResponse>(CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/notes/{note!.Id}", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/notes/{note.Id}", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Notes_WithoutATenant_AreForbidden()
    {
        var response = await app.CreateClient("ada").GetAsync("/notes", CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Notes_SeededForATenant_AreListedForIt()
    {
        var tenant = Guid.NewGuid();

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // İsteğin dışında yazan kod kimliği kendisi verir; TenantId bundan doldurulur.
            using (HaldusAmbient.Use(tenant, userId: null))
            {
                db.Notes.Add(new Note { Text = "Seeded" });
                await db.SaveChangesAsync(CancellationToken);
            }
        }

        var page = await app.CreateClient("ada", tenant)
            .GetFromJsonAsync<PagedResult<NoteResponse>>("/notes", CancellationToken);

        Assert.Equal(["Seeded"], page!.Items.Select(item => item.Text));
    }
}
```

Testlerde üretilen DTO'ları (`CreateNoteRequest`, `NoteResponse`) ve Haldus'un tiplerini (`PagedResult<T>`)
kullanabilirsiniz; zincir değişip bir alan kalkarsa test derlenmez. Hata yanıtları için
`Microsoft.AspNetCore.Mvc.ProblemDetails` veya `HttpValidationProblemDetails` okunur.

## Veritabanına doğrudan yazmak

Bazen istemcinin yazamadığı bir veriyi (salt-okunur bir kaynak, başka bir kullanıcının kaydı) testte hazırlamanız
gerekir. Bunun için uygulamanın servislerinden bir bağlam alırsınız (yukarıdaki son test). İki şeye dikkat edin:

- Bir isteğin dışında kiracı ve kullanıcı yoktur. `MultiTenant` veya `OwnedByCurrentUser` bir kaydı kaydederken
  kimlik yoksa ve alan elle de verilmediyse kaydetme hata verir. Kimliği `HaldusAmbient.Use(tenantId, userId)` ile
  verin.
- Sorgu filtreleri test kodunda da çalışır: kimlik yokken kiracıya ait kayıtlar görünmez. Bir doğrulamada bütün
  kayıtları görmek için filtreyi adıyla kaldırın:

<!-- fragment -->
```csharp
var stored = await db.Notes.IgnoreQueryFilters([HaldusFilters.Tenant]).SingleAsync(note => note.Id == id);
```

## Arka plan işlerini ve zamanı sınamak

Haldus'un arka plan servisleri aralıklarla çalışır; testte onları beklemek yerine doğrudan çağırın:

| Çağrı | Ne yapar |
|---|---|
| `HaldusMaintenance.RunAsync(services, cancellationToken)` | Bakımın bir turu: saklama süresi geçmiş idempotency yanıtları, `Retention`, zamanlanmış yayın, bakiye tutmaları ve (açıksa) süresi dolmuş kayıtlar. |
| `HaldusEventDispatcher.RunAsync(services, cancellationToken)` | Bekleyen olayların bir turunu kayıtlı yayıncılara teslim eder. Kayıtlı yayıncı yoksa hiçbir olaya dokunmaz. |

<!-- fragment -->
```csharp
await HaldusMaintenance.RunAsync(app.Factory.Services, CancellationToken);
```

Olay yayınını sınamak için kendi `IHaldusEventPublisher` uygulamanızı `ConfigureTestServices` ile kaydeden ikinci bir
uygulama açabilirsiniz (`Factory.WithWebHostBuilder(...)`); Haldus'un kendi testleri olayları böyle yakalar.

Haldus zamanı DI'daki `TimeProvider` servisinden okur (zaman damgaları, süre dolumu, bakımın kesme anı, süreli
bağlantılar). Zamana bağlı bir davranışı sınamak için `ConfigureTestServices` içinde kendi `TimeProvider`'ınızı
kaydedebilirsiniz.

## Üretilen kodu görmek

Bir testin neden beklemediğiniz bir yanıt aldığını anlamak için üretilen ucu okuyun. Uygulamanın proje dosyasına
şunu ekleyin:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
</PropertyGroup>
```

Derlemeden sonra dosyalar `obj/Generated/Haldus.Generators/Haldus.Generators.HaldusGenerator/` altındadır
(`Note.Endpoints.g.cs`, `Note.Dtos.g.cs`, `HaldusModule.g.cs`...). Uçların sırası, hangi kontrolün hangi durum kodunu
döndürdüğü ve sorgu filtreleri orada görünür. Dosyaları değiştirmeyin; her derlemede yeniden üretilirler.

Haldus'un kuralları derleme zamanında da sınanır: çelişen veya okunamayan bir bildirim `HALDUS0xx` koduyla derleme
hatasıdır. Bunlar için test yazmanız gerekmez; bkz. [tanılar](../reference/diagnostics.md).

## Sınırlar

- Haldus bir test yardımcı paketi taşımaz; fixture'ı, kimlik şemasını ve veritabanı kurulumunu siz yazarsınız.
- Testler gerçek PostgreSQL ile yazılmıştır. EF Core'un bellek içi sağlayıcısı ve SQLite ile çalışma denenmedi;
  Haldus'un kilit (`pg_advisory_xact_lock`), boşluksuz sayaç ve iş kuyruğu kodu PostgreSQL'e özgü SQL çalıştırır.
- `EnsureCreatedAsync()` migration'larınızı sınamaz.

## Ayrıca bakın

- [Kiracılar, kullanıcılar ve yetkilendirme](tenancy-and-ownership.md)
- [Veritabanı ve migration'lar](database.md)
- [Olay yayını](events.md)
- [Tanılar](../reference/diagnostics.md)
