using Haldus;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Account;
using Haldus.Sample.Shop.Features.Auth;
using Haldus.Sample.Shop.Features.Catalog;
using Haldus.Sample.Shop.Features.Integrations;
using Haldus.Sample.Shop.Features.Platform;
using Haldus.Sample.Shop.Features.Sales;
using Haldus.Sample.Shop.Features.Stock;
using Haldus.Sample.Shop.Features.Content;
using Haldus.Sample.Shop.Features.Engagement;
using Haldus.Sample.Shop.Features.Supply;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Bağlantı dizesi commit'lenmez; user-secrets veya ConnectionStrings__Default ortam değişkeniyle verilir.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// Kimlik doğrulama: parola ile giriş ve ASP.NET Core'un yerleşik taşıyıcı (bearer) token şeması; uçları
// Features/Auth altındadır. Haldus kullanıcıyı ve kiracıyı token'daki claim'lerden okur (varsayılanı budur).
builder.Services.AddAuthentication(BearerTokenDefaults.AuthenticationScheme).AddBearerToken();
builder.Services.AddAuthorization();

// Shop:DemoIdentity açıksa kimlik, doğrulanmadan, istek başlıklarından okunur (X-User-Id, X-Tenant-Id). Yalnızca
// entegrasyon testleri ve yerel denemeler içindir; varsayılan olarak kapalıdır. AddHaldus kendi claim tabanlı
// çözümleyicisini yalnızca başka bir uygulama kayıtlı değilse ekler.
if (builder.Configuration.GetValue<bool>("Shop:DemoIdentity"))
{
    builder.Services.AddSingleton<DemoIdentity>();
    builder.Services.AddSingleton<ITenantProvider>(services => services.GetRequiredService<DemoIdentity>());
    builder.Services.AddSingleton<ICurrentUser>(services => services.GetRequiredService<DemoIdentity>());
    builder.Services.AddSingleton<ICurrentUserRoles>(services => services.GetRequiredService<DemoIdentity>());
}

builder.Services.AddHaldus<AppDbContext>();

// İşler (MapJob) uygulamanın kendi veritabanındaki bir kuyrukta bekler; iş kaydıyla aynı transaction'da yazılır.
builder.Services.AddHaldusPostgreSqlJobs<AppDbContext>();

// Uygulamanın kendi son kullanıcı metinleri (ör. bir geçişin reddedilme nedeni) Resources altından okunur.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddOpenApi();

// Sayılar yalnızca sayı olarak okunur. Varsayılan ayar metin içindeki sayıyı da ("5") kabul eder ve bu, şemaya
// "integer | string" olarak yansır; istemci tipleri (orval) o zaman her sayıyı `number | string` görürdü.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict);

// Başka bir kaynaktan çalışan örnek arayüzler (ör. mobil uygulamanın tarayıcıdaki hali) çağırabilsin diye, yalnızca
// sayılan kaynaklara izin verilir (Shop:CorsOrigins). Varsayılan olarak hiçbir kaynağa izin yoktur; cihazda çalışan
// uygulama ve aynı kaynaktan vekil üzerinden çağıran panel buna ihtiyaç duymaz.
var corsOrigins = builder.Configuration.GetSection("Shop:CorsOrigins").Get<string[]>() ?? [];

if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders("ETag", "Location")));
}

var app = builder.Build();

// Örnek arayüzlerin (Haldus.Sample.Shop.Web, Haldus.Sample.Shop.Mobile) karşısında çalıştırmak için: istenirse
// (Shop:CreateDatabase) tablolar açılışta, yoksa oluşturulur. Varsayılan olarak kapalıdır ve build sırasındaki şema
// üretiminde hiç çalışmaz; gerçek bir uygulama şemasını migration'larla yönetir.
if (app.Configuration.GetValue<bool>("Shop:CreateDatabase") && !HaldusJobs.IsGeneratingOpenApiDocument)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
}

if (corsOrigins.Length > 0)
{
    app.UseCors();
}

app.UseAuthentication();
app.UseRejectedTokenChallenge();
app.UseAuthorization();
app.UseHaldus();
app.MapOpenApi();
app.MapAuth();
app.MapCatalog();
app.MapSales();
app.MapStock();
app.MapPlatform();
app.MapAccount();
app.MapIntegrations();
app.MapSupply();
app.MapContent();
app.MapEngagement();
app.MapDiagnostics();

// Entity'ye bağlı olmayan işlemler: iş mantığı ShippingQuoteHandler ve ApplyDiscountHandler sınıflarında.
app.MapQuery<ShippingQuoteRequest, ShippingQuote>();
app.MapAction<ApplyDiscountRequest, ApplyDiscountResult>();

app.Run();

public partial class Program;
