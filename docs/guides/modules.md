# Modüler yapı

Bir uygulamanın özellikleri ayrı sınıf kütüphanelerine (modüllere) bölünebilir. Her modül kendi entity'lerini ve
`Map*` çağrılarını taşır; Haldus her modülde kendi kodunu üretir. Veritabanı bağlamı modül başına seçilir: bir modül
ana projenin ortak bağlamını kullanabilir ya da kendi bağlamına (ve şemasına, migration'larına) sahip olabilir. İki yol
aynı uygulamada birlikte kullanılabilir. Tek projeli bir uygulama, tek modüllü haldir; bu sayfadaki hiçbir şeyi
yapmanız gerekmez.

Bu sayfa depodaki [modüler örneğe](../../samples/Haldus.Sample.Modular) dayanır. Örneklerde web SDK'nın örtük
`using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve `System.ComponentModel.DataAnnotations` yazılmış sayılır.

## İçindekiler

- [Örneğin yapısı](#örneğin-yapısı)
- [Bir modül projesi](#bir-modül-projesi)
- [Kendi bağlamı olan modül](#kendi-bağlamı-olan-modül)
- [Ortak bağlamı kullanan modül](#ortak-bağlamı-kullanan-modül)
- [Ana proje](#ana-proje)
- [Modüllerin bulunması](#modüllerin-bulunması)
- [Modüller arası ilişkiler](#modüller-arası-ilişkiler)
- [Haldus'un kendi tabloları](#haldusun-kendi-tabloları)
- [Migration'lar](#migrationlar)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Örneğin yapısı

| Proje | Bağlam | İçindekiler |
|---|---|---|
| `Haldus.Sample.Modular.Catalog` | Kendi bağlamı: `CatalogDbContext` (`catalog` şeması) | `Product`, `GET /price-check` sorgusu ve handler'ı |
| `Haldus.Sample.Modular.Sales` | Kendi bağlamı: `SalesDbContext` (`sales` şeması) | `Order`; `Product`'a referans verir |
| `Haldus.Sample.Modular.Support` | Ortak bağlam | `Ticket`; `Order`'a referans verir |
| `Haldus.Sample.Modular.Host` | Ortak bağlam: `HostDbContext` | Web uygulaması; `Feedback` (`Ticket`'a referans) ve `TicketAttachment` (`Ticket`'ın altında) |

| Bildirim | Ne yapar |
|---|---|
| `AddHaldus<TContext>()` | Haldus'un servislerini kaydeder ve `TContext`'i ortak bağlam yapar: kendi bağlamı olmayan bütün modüllerin entity'leri onun modeline girer. |
| `AddHaldusModule<TContext>()` | `TContext` ile aynı derlemede (assembly) üretilen entity'leri yalnızca bu bağlamın modeline koyar. Modül böylece kendi şemasına ve migration'larına sahip olur. |

Bir uygulamada `AddHaldus<TContext>()` bir kez çağrılır. `AddHaldusModule<TContext>()` kendi bağlamı olan her modül
için bir kez çağrılır. İkisi de Haldus'un servislerini kaydeder; birlikte çağrılmaları zararsızdır.

## Bir modül projesi

Modül sıradan bir sınıf kütüphanesidir. Ana proje gibi `Haldus.AspNetCore` paketine başvurur; generator bu paketle
gelir ve modülün içinde de çalışır:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Haldus.AspNetCore" Version="0.1.0-preview.1" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
  </ItemGroup>

</Project>
```

`Microsoft.AspNetCore.App` başvurusu `IEndpointRouteBuilder` gibi ASP.NET Core tipleri içindir. Kendi bağlamı olmayan
bir modülün Npgsql paketine ihtiyacı yoktur.

> [!IMPORTANT]
> Modül yalnızca `Haldus.Abstractions` veya `Haldus.EntityFrameworkCore` paketine başvurursa generator çalışmaz:
> `MapCrud` derlenir ama hiçbir şey üretilmez. Modüller de `Haldus.AspNetCore` paketine başvurur.

## Kendi bağlamı olan modül

Katalog modülü kendi bağlamını taşır. Bağlam `partial` yazılır; Haldus bu derlemede üretilen entity'lerin `DbSet`
özelliklerini onun öteki yarısına ekler. Modül, ana projenin çağıracağı iki uzantı metodu sunar: servisleri kaydeden ve
uçları eşleyen.

<!-- example: catalog-module -->
```csharp
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog;

// Tabloları "catalog" şemasında durur ve başka hiçbir bağlamın modelinde yer almaz.
public partial class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.HasDefaultSchema("catalog");
}

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [Range(0.0, 1_000_000.0)]
    public decimal Price { get; set; }
}

// GET /price-check: bir ürünün verilen adetteki tutarı.
public sealed class PriceCheckRequest
{
    public Guid ProductId { get; set; }

    [Range(1, 1000)]
    public int Quantity { get; set; } = 1;
}

public sealed record PriceCheck(bool Found, decimal Total);

// Handler modülün içindedir ve modülün kendi bağlamını kullanır; Haldus onu bulur ve kaydeder.
public sealed class PriceCheckHandler(CatalogDbContext db) : IQueryHandler<PriceCheckRequest, PriceCheck>
{
    public async Task<PriceCheck> HandleAsync(PriceCheckRequest request, CancellationToken cancellationToken)
    {
        var price = await db.Products
            .Where(product => product.Id == request.ProductId)
            .Select(product => (decimal?)product.Price)
            .FirstOrDefaultAsync(cancellationToken);

        return new PriceCheck(price is not null, (price ?? 0) * request.Quantity);
    }
}

public static class CatalogModule
{
    public static IServiceCollection AddCatalog(this IServiceCollection services)
    {
        services.AddDbContext<CatalogDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("Default")));

        // Bu derlemede üretilen entity'ler yalnızca CatalogDbContext'in modeline girer.
        return services.AddHaldusModule<CatalogDbContext>();
    }

    public static void MapCatalog(this IEndpointRouteBuilder app)
    {
        app.MapCrud<Product>().Idempotent();
        app.MapQuery<PriceCheckRequest, PriceCheck>();
    }
}
```

`AddHaldusModule<CatalogDbContext>()` derlemeyi bağlamın tipinden bulur: `CatalogDbContext` hangi derlemedeyse, o
derlemede üretilen bütün entity'ler bu bağlama aittir. Bir derlemenin tek bir bağlamı olur.

Satış modülü de aynı biçimdedir (`SalesDbContext`, `sales` şeması), ama başka bir modülün entity'sine referans verir.
Bu yüzden tek başına derlenemez; katalog projesine başvurur:

<!-- fragment -->
```csharp
// Haldus.Sample.Modular.Sales; projeye Haldus.Sample.Modular.Catalog başvurusu eklenmiştir.
using Haldus.Sample.Modular.Catalog;

public partial class SalesDbContext(DbContextOptions<SalesDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.HasDefaultSchema("sales");
}

public partial class Order
{
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";
}

public static class SalesModule
{
    public static IServiceCollection AddSales(this IServiceCollection services)
    {
        services.AddDbContext<SalesDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("Default")));

        return services.AddHaldusModule<SalesDbContext>();
    }

    public static void MapSales(this IEndpointRouteBuilder app)
    {
        // Ürün başka bir bağlamdadır: sipariş ona yalnızca kimliğiyle bağlanır (aşağıda).
        app.MapCrud<Order>()
            .References<Product>()
            .Numbered("S-{seq:4}");
    }
}
```

## Ortak bağlamı kullanan modül

Kendi bağlamı olmayan modül hiçbir şey kaydetmez; yalnızca uçlarını eşler. Entity'leri ana projenin `AddHaldus` ile
bildirdiği ortak bağlama girer:

<!-- fragment -->
```csharp
// Haldus.Sample.Modular.Support; projeye Haldus.Sample.Modular.Sales başvurusu eklenmiştir.
using Haldus.Sample.Modular.Sales;

public partial class Ticket
{
    [MaxLength(200)]
    public string Subject { get; set; } = "";
}

public static class SupportModule
{
    public static void MapSupport(this IEndpointRouteBuilder app)
    {
        // Sipariş satış modülünün kendi bağlamındadır; bu referans da yalnızca kimlikle tutulur.
        app.MapCrud<Ticket>()
            .References<Order>(required: false);
    }
}
```

Bu iki blok derlenmeyen birer parçadır (`fragment`), çünkü başka projelerdeki tiplere başvururlar. Çalışan halleri
örneğin [Sales](../../samples/Haldus.Sample.Modular/Haldus.Sample.Modular.Sales) ve
[Support](../../samples/Haldus.Sample.Modular/Haldus.Sample.Modular.Support) projelerindedir.

## Ana proje

Ana proje web uygulamasıdır. Ortak bağlamı kaydeder, modüllerin kayıt metotlarını çağırır ve uçlarını eşler. Ortak
bağlamda kendi entity'leri de olabilir:

<!-- fragment -->
```csharp
// Haldus.Sample.Modular.Host; projeye üç modülün başvurusu eklenmiştir.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<HostDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// Ortak bağlam: kendi bağlamı olmayan modüllerin (Support) ve bu projenin entity'leri buraya girer.
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

public partial class HostDbContext(DbContextOptions<HostDbContext> options) : DbContext(options);

public partial class Feedback
{
    [Range(1, 5)]
    public int Rating { get; set; }
}

public partial class TicketAttachment
{
    [MaxLength(200)]
    public string FileName { get; set; } = "";
}

public static class HostModule
{
    public static void MapHost(this IEndpointRouteBuilder app)
    {
        // Ticket başka bir modülün entity'sidir ama aynı (ortak) bağlamdadır: gerçek bir yabancı anahtar olur.
        app.MapCrud<Feedback>().References<Ticket>();

        // Aynı bağlamda oldukları için başka modülün kaydı ebeveyn de olabilir: /tickets/{ticketId}/ticket-attachments
        app.MapCrud<TicketAttachment>().Under<Ticket>();
    }
}
```

Bu örnekte bağlamlar şöyle dağılır:

| Entity | Bağlam | Tablo |
|---|---|---|
| `Product` | `CatalogDbContext` | `catalog.Products` |
| `Order` | `SalesDbContext` | `sales.Orders` |
| `Ticket` | `HostDbContext` | `public.Tickets` |
| `Feedback`, `TicketAttachment` | `HostDbContext` | `public.Feedbacks`, `public.TicketAttachments` |

Bir entity yalnızca kendi bağlamının modelindedir: başka bir modüle referans vermek onun tablosunu sizin bağlamınıza
getirmez. Haldus'un ürettiği kod somut bir bağlam tipine bağlı değildir; her entity için doğru bağlamı
`IHaldusDbContextProvider` verir (`contexts.For<Product>()` bir `CatalogDbContext` döndürür).

## Modüllerin bulunması

Modülleri ayrıca kaydetmeniz gerekmez. `AddHaldus<TContext>()` ve `AddHaldusModule<TContext>()`, bağlamın bulunduğu
derlemeden başlayarak başvurulan derlemeleri dolaşır; Haldus'un çalışma zamanına (`Haldus.AspNetCore`,
`Haldus.EntityFrameworkCore`, `Haldus.Abstractions`) başvuran her derleme bir modül sayılır ve yüklenir. Yukarıdaki
`Support` modülünün `AddSupport` gibi bir metodu yoktur; ana proje ona başvurduğu ve onun bir tipini (`MapSupport`)
kullandığı için bulunur. Dolaşma derlenmiş derlemenin başvuru listesine bakar ve C# derleyicisi bu listeye yalnızca
kodda kullanılan derlemeleri yazar; modülden hiçbir şey kullanmayan bir proje başvurusu yetmez.

Bir modülün üretilen kayıt kodu (handler sınıfları, model katkıları) derleme ilk kullanıldığında çalışır. Haldus,
modül henüz hiç kullanılmamışken de bu kodu açılışta çalıştırır; bu yüzden `PriceCheckHandler` gibi handler'lar ilk
istekten önce DI'a kayıtlıdır ve eksik bir handler uygulama açılırken hata verir.

> [!NOTE]
> Dolaşma bağlamın derlemesinden başlar ve başvuruları izler. Ana proje bir modüle başvurmuyorsa (ör. modül yalnızca
> çalışma anında yükleniyorsa) o modül bulunmaz.

## Modüller arası ilişkiler

Bir modül başka bir modülün entity'sine bağlanabilir. Kurallar iki modülün aynı bağlamda olup olmamasına bağlıdır:

| Bildirim | Aynı bağlamda | Ayrı bağlamlarda |
|---|---|---|
| `.References<T>()` | Gerçek bir yabancı anahtar kısıtı. Silme davranışı (`Restrict`, `Cascade`...) uygulanır; referans verilen kaydı silmek `409` döner. | Yalnızca kimlik tutulur, veritabanında kısıt yoktur. Hedefin varlığı her yazmada, hedefin kendi bağlamında doğrulanır (yoksa alan düzeyinde `400`). Silme davranışı uygulanamaz: hedef, ona referans veren kayıtlar varken de silinebilir. |
| `.Under<T>()` | Desteklenir: kaynak ebeveynin adresi altına yerleşir ve onun görünürlüğünü izler. | Desteklenmez. Uygulama ilk istekte değil, model kurulurken bir `InvalidOperationException` ile durur. Başka modüldeki bir kayda bağlanmak için `References<T>()` kullanın. |
| `MapAssociation<TLeft, TRight>()` | İki uç da aynı modülde (aynı derlemede) eşlenmiş olmalıdır. | Aynı. Başka bir derlemedeki uç tanınmaz ve derleme `HALDUS009` ile durur. |

Örnekte:

- `Feedback` → `Ticket`: ikisi de ortak bağlamda. Geri bildirimi olan talep silinemez (`409`). Talebin modülü bu
  referanstan habersizdir; çakışmayı veritabanı bildirir, uç onu `409`'a çevirir.
- `Order` → `Product`: ayrı bağlamlarda. Var olmayan bir ürünle sipariş `400` alır; ama ürün, ona bağlı siparişler
  varken de silinebilir.
- `TicketAttachment` → `Ticket` (`Under`): aynı bağlamda. Var olmayan bir talebin altına ek yazmak `404` döner; talep
  silinince ekleri de silinir.

Generator başka bir derlemedeki entity'yi kaynak kodu olarak değil derlenmiş bir tip olarak görür. Anahtarını
`Id`, `Code` veya `Key` özelliğinden, kiracıya ait olup olmadığını `IMultiTenant` arayüzünden okur. Bu yüzden
referans verilen modülün projesine başvurmanız yeter; o modülün zincirini yeniden yazmazsınız.

## Haldus'un kendi tabloları

Haldus bazı özellikler için kendi tablolarını kullanır (numara sayaçları `HaldusCounters`, idempotency kayıtları
`HaldusIdempotencyRecords` gibi). Bu tablolar, onları kullanan kaynağın bağlamına eklenir. Örnekte `Numbered` bildiren
`Order` satış modülündedir, bu yüzden sayaç `sales.HaldusCounters` tablosundadır; `Idempotent` bildiren `Product`
katalog modülündedir ve idempotency kayıtları `catalog.HaldusIdempotencyRecords` tablosundadır. Bakım servisi bütün
bağlamları dolaşır.

İstisna PostgreSQL iş kuyruğudur: `AddHaldusPostgreSqlJobs<TContext>()` kuyruğu tek bir bağlama koyar ve iş entity'leri
o bağlamda olmalıdır, çünkü iş kaydı ile kuyruk kaydı aynı transaction'da yazılır. Başka bir bağlamda duran bir iş
oluşturulurken açık bir hata verilir. Ayrıntısı [uzun süren işler](jobs.md) sayfasındadır.

## Migration'lar

Birden çok bağlamı olan bir uygulamada her bağlamın kendi migration'ları olur ve EF Core araçlarına hangi bağlamı
kastettiğinizi söylersiniz (`--context CatalogDbContext`). Bu EF Core'un genel kuralıdır; Haldus'la çalışmanın
ayrıntısı [veritabanı ve migration'lar](database.md) sayfasındadır.

Depodaki modüler örnek migration kullanmaz. Entegrasyon testleri ortak bağlamı `EnsureCreatedAsync()` ile kurar, öteki
bağlamların yalnızca tablolarını ekler (`IRelationalDatabaseCreator.CreateTablesAsync()`), çünkü veritabanı o sırada
vardır:

<!-- fragment -->
```csharp
await services.GetRequiredService<HostDbContext>().Database.EnsureCreatedAsync();
await services.GetRequiredService<CatalogDbContext>().GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
await services.GetRequiredService<SalesDbContext>().GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
```

## Sınırlar

- Ayrı bağlamlar arasında ortak transaction yoktur. İki bağlama yazan bir işlem atomik değildir.
- Ayrı bağlamlardaki referansın silme davranışı yoktur; hedef silinince referans veren kayıt yetim kalır.
- `Under<T>()` ve `MapAssociation` bağlam veya modül sınırını aşmaz.
- PostgreSQL iş kuyruğu tek bir bağlamdadır.
- Bir derlemenin tek bir bağlamı vardır: aynı modülün entity'lerini iki bağlama bölemezsiniz.

## Ayrıca bakın

- [Kurulum](../getting-started/installation.md)
- [İlişkiler](relationships.md)
- [Veritabanı ve migration'lar](database.md)
- [Uzun süren işler](jobs.md)
- [Paketler](../reference/packages.md)
