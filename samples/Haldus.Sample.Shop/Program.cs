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
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseHaldus();
app.MapOpenApi();
app.MapCatalog();
app.MapSales();
app.MapStock();
app.MapPlatform();

app.Run();

public partial class Program;
