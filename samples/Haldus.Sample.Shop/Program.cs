using Haldus;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Catalog;
using Haldus.Sample.Shop.Features.Platform;
using Haldus.Sample.Shop.Features.Sales;
using Haldus.Sample.Shop.Features.Stock;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Bağlantı dizesi commit'lenmez; user-secrets veya ConnectionStrings__Default ortam değişkeniyle verilir.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// Örnek, kiracıyı ve kullanıcıyı başlıklardan okur; AddHaldus kendi claim tabanlı çözümleyicisini
// yalnızca başka bir uygulama kayıtlı değilse ekler.
builder.Services.AddSingleton<DemoIdentity>();
builder.Services.AddSingleton<ITenantProvider>(services => services.GetRequiredService<DemoIdentity>());
builder.Services.AddSingleton<ICurrentUser>(services => services.GetRequiredService<DemoIdentity>());

builder.Services.AddHaldus<AppDbContext>();

// İşler (MapJob) uygulamanın kendi veritabanındaki bir kuyrukta bekler; iş kaydıyla aynı transaction'da yazılır.
builder.Services.AddHaldusPostgreSqlJobs<AppDbContext>();

// Uygulamanın kendi son kullanıcı metinleri (ör. bir geçişin reddedilme nedeni) Resources altından okunur.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseHaldus();
app.MapOpenApi();
app.MapCatalog();
app.MapSales();
app.MapStock();
app.MapPlatform();
app.MapDiagnostics();

// Entity'ye bağlı olmayan işlemler: iş mantığı ShippingQuoteHandler ve ApplyDiscountHandler sınıflarında.
app.MapQuery<ShippingQuoteRequest, ShippingQuote>();
app.MapAction<ApplyDiscountRequest, ApplyDiscountResult>();

app.Run();

public partial class Program;
