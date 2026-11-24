using Haldus;
using Haldus.Sample.Modular.Catalog;
using Haldus.Sample.Modular.Host;
using Haldus.Sample.Modular.Sales;
using Haldus.Sample.Modular.Support;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Bağlantı dizesi commit'lenmez; user-secrets veya ConnectionStrings__Default ortam değişkeniyle verilir.
builder.Services.AddDbContext<HostDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// Ortak bağlam: kendi bağlamı olmayan modüllerin (destek modülü ve bu projenin kendisi) entity'leri buraya girer.
builder.Services.AddHaldus<HostDbContext>();

// Kendi bağlamı olan modüller.
builder.Services.AddCatalog();
builder.Services.AddSales();

var app = builder.Build();

app.UseHaldus();
app.MapCatalog();
app.MapSales();
app.MapSupport();
app.MapHost();

app.Run();