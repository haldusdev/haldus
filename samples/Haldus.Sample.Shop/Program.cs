using Haldus;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Catalog;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Bağlantı dizesi commit'lenmez; user-secrets veya ConnectionStrings__Default ortam değişkeniyle verilir.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddHaldus<AppDbContext>();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseHaldus();
app.MapOpenApi();
app.MapCatalog();

app.Run();

public partial class Program;
