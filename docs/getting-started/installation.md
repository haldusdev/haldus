# Kurulum

Bu sayfa Haldus'u yeni veya var olan bir ASP.NET Core projesine eklemeyi anlatır. Adım adım bir uygulama kurmak
isterseniz doğrudan [eğitime](tutorial.md) geçin.

## Gereksinimler

| Gereken | Sürüm |
|---|---|
| .NET SDK | 10.0 |
| Veritabanı | PostgreSQL (Haldus'un kilit, sayaç ve kuyruk kodu PostgreSQL'e özgüdür) |
| ORM | EF Core 10 ve Npgsql sağlayıcısı |
| Proje tipi | ASP.NET Core (Minimal API). Sınıf kütüphanesi de olabilir; bkz. [modüler yapı](../guides/modules.md). |

## Paketi eklemek

Paketler nuget.org'dadır. Projenize iki paket eklenir:

```bash
dotnet add package Haldus.AspNetCore --prerelease
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
```

`Haldus.AspNetCore` kaynak üreticisini (generator) de içerir; ayrıca bir generator paketi eklenmez. İhtiyaç
duyduğunuz özelliklere göre başka paketler de eklenir; bkz. [paketler](../reference/packages.md). Bütün `Haldus.*`
paketleri aynı sürümü taşır; paketleri karıştırmayın.

> [!NOTE]
> Sürüm bir ön sürümdür (`preview`); API değişebilir. `--prerelease` olmadan `dotnet add package` ön sürümü bulmaz.

### Depodan üretmek

Henüz yayınlanmamış bir değişikliği denemek için paketleri depodan üretip yerel bir klasörden kullanırsınız. Yerel
paketler, nuget.org'dakilerle karışmasın diye ayrı bir ön sürüm adıyla üretilir:

```bash
git clone https://github.com/haldusdev/haldus.git
cd haldus
dotnet pack -c Release -o artifacts/packages --version-suffix local.1
```

Uygulamanızın kökünde bir `nuget.config` dosyası bu klasörü paket kaynağı olarak tanıtır:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="haldus-local" value="../haldus/artifacts/packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

Paket o sürümle eklenir: `dotnet add package Haldus.AspNetCore --version 0.1.0-local.1`.

> [!NOTE]
> Aynı sürüm numarasıyla yeniden üretilen paket, NuGet'in önbelleği yüzünden projenize ulaşmaz. Paketi yeniden
> ürettiğinizde sürümü ilerletin (`--version-suffix local.2`) ya da önbellekteki `haldus.*` klasörlerini silin
> (`~/.nuget/packages`).

## Uygulamaya bağlamak

Üç şey gerekir: `partial` bir `DbContext`, `AddHaldus` ve `UseHaldus`.

<!-- example: setup -->
```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();

var app = builder.Build();

app.UseHaldus();
app.MapCrud<Product>();

app.Run();

// Haldus, eşlenen her entity'nin DbSet özelliğini bu sınıfın öteki yarısına ekler.
public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public decimal Price { get; set; }
}
```

| Satır | Ne yapar |
|---|---|
| `AddHaldus<AppDbContext>()` | Haldus'un servislerini kaydeder: model katkıları, interceptor'lar, doğrulama, hata yanıtları, mesajlar, bakım servisi. Bağlamın kendisini siz kaydedersiniz (`AddDbContext`). |
| `UseHaldus()` | İsteğin dilini `Accept-Language` başlığından çözer (`UseRequestLocalization`); Haldus'un hata ve doğrulama mesajları bu dilde döner (İngilizce ve Türkçe). Uçları eşleyen `Map*` çağrılarından önce yazın. |
| `MapCrud<Product>()` | Bir kaynağı eşler. Derleme sırasında generator bu satırı okur ve kodu üretir. |

`ConnectionStrings:Default` ayarı `appsettings.json` içinde verilir:

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Database=shop;Username=postgres;Password=postgres"
  }
}
```

Tabloları oluşturmak için EF Core migration'larını kullanırsınız; bkz. [veritabanı ve migration'lar](../guides/database.md).

## Üretilen kodu görmek

Generator'ın ürettiği dosyaları diske yazdırmak için proje dosyasına şunu ekleyin:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
</PropertyGroup>
```

Derlemeden sonra dosyalar `obj/Generated/Haldus.Generators/Haldus.Generators.HaldusGenerator/` altındadır:

| Dosya | İçeriği |
|---|---|
| `Product.Entity.g.cs` | Entity'nin öteki yarısı: `Id` ve bildirimlerin eklediği alanlar. |
| `Product.Dtos.g.cs` | `ProductResponse`, `CreateProductRequest`, `UpdateProductRequest`, `PatchProductRequest`. |
| `Product.Endpoints.g.cs` | Uçlar ve onların kuralları. |
| `HaldusModule.g.cs` | EF Core model yapılandırması ve çalışma zamanı kayıtları. |
| `AppDbContext.DbSets.g.cs` | Bağlamın `DbSet` özellikleri. |

Bu dosyaları elle değiştirmeyin; her derlemede yeniden üretilirler. Bir şeyi değiştirmek için zinciri veya entity'nin
kendi yarısını değiştirirsiniz.

## Sonraki adım

- [Eğitim: ilk API'niz](tutorial.md)
- [Haldus nasıl çalışır](../concepts/how-it-works.md)
