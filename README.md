# Haldus

**ASP.NET Core ve EF Core için tekrarlayan backend kodunu derleme zamanında üreten .NET 10 framework'ü.**

Bir `partial class` ve tek satırlık bir bildirim yazarsınız; Haldus entity'nin geri kalanını, istek ve yanıt tiplerini,
Minimal API uçlarını, doğrulamayı, durum kurallarını ve EF Core yapılandırmasını üretir.

> [!NOTE]
> Haldus bir ön sürümdür (`0.1.0-preview.1`). API değişebilir ve paketler henüz bir NuGet kaynağında yayınlanmadı;
> depodan üretilir. Bkz. [kurulum](docs/getting-started/installation.md).

<!-- example: readme -->
```csharp
var orders = app.MapCrud<Order>()
    .MultiTenant()
    .Auditable()
    .ConcurrencyChecked()
    .Workflow<OrderStatus>(initial: OrderStatus.Draft)
    .EditableIn(OrderStatus.Draft)
    .Numbered("ORD-{yyyy}-{seq:6}", assignOn: OrderStatus.Placed);

orders.MapDetail<OrderItem>();
orders.MapTransition("place", from: OrderStatus.Draft, to: OrderStatus.Placed);
orders.MapTransition("cancel", from: [OrderStatus.Draft, OrderStatus.Placed], to: OrderStatus.Cancelled);

public enum OrderStatus { Draft, Placed, Cancelled }

public partial class Order
{
    [Required]
    [MaxLength(200)]
    public string CustomerName { get; set; } = "";
}

public partial class OrderItem
{
    [MaxLength(100)]
    public string Sku { get; set; } = "";

    [Range(1, 1000)]
    public int Quantity { get; set; }
}
```

Bu bildirimden üretilenler:

| | |
|---|---|
| Uçlar | `GET /orders`, `GET /orders/{id}`, `POST /orders`, `PUT /orders/{id}`, `PATCH /orders/{id}`, `DELETE /orders/{id}`, `POST /orders/{id}/place`, `POST /orders/{id}/cancel` |
| Tipler | `OrderResponse`, `CreateOrderRequest`, `UpdateOrderRequest`, `PatchOrderRequest`; satırlar istek ve yanıtlarda gömülü |
| Alanlar | `Id`, `TenantId`, `CreatedAt`, `UpdatedAt`, `Status`, `Number`; satırların `Id`'si ve sahibine giden yabancı anahtar |
| Kurallar | Doğrulama, kiracıya göre süzme, `ETag` / `If-Match`, taslak dışında düzenlemenin reddi (409), sipariş verilince boşluksuz numara, satırların eşitlenmesi |
| Model | Tablolar, anahtarlar, indeksler, sorgu filtreleri: EF Core migration'larınıza girer |

## Neden Haldus

Bir iş uygulamasının kaynaklarının çoğu aynı kalıpları tekrarlar: listelenir, süzülür, sayfalanır; kiracıya veya
kullanıcıya aittir; durumlardan geçer, numara alır, onaylanır, sürümlenir, geri alınabilir biçimde silinir. Haldus bu
kalıpları bir ad ve bir anlamla bildirmenizi sağlar ve kodu sizin yerinize, tutarlı biçimde üretir.

- **Derleme zamanında.** Kod bir Roslyn kaynak üreticisiyle üretilir: IDE'de görünür, içine adım atılır, derleyiciyle
  denetlenir. Çalışma zamanında yansımayla kod kurulmaz.
- **Yanlış bildirim derlenmez.** Birbiriyle çelişen bildirimler sessizce yok sayılmaz; derleme hatası olur
  (`HALDUS001`–`HALDUS010`).
- **Kurallar modelde.** Kiracı, sahiplik ve görünürlük sorgu filtreleridir; denetim alanları, sürümler, olaylar ve
  toplanan alanlar kaydetme sırasında işler. Kendi yazdığınız kod da bu kurallardan geçer.
- **Sıradan bir uygulama.** Haldus'un yanında kendi uçlarınızı, `DbContext`'inizi ve EF Core migration'larınızı
  bildiğiniz gibi yazarsınız.

## Özellikler

| Alan | Bildirimler |
|---|---|
| [Archetype'lar](docs/guides/archetypes.md) | `MapCrud`, `MapReadOnly`, `MapAppendOnly`, `MapStore`, `MapLookup`, `MapSingleton`, `MapDetail`, `MapFile`, `MapJob`, `MapBalance`, `MapAssociation`, `MapAction`, `MapQuery` |
| [İlişkiler](docs/guides/relationships.md) | Referans, ebeveyn altında, ağaç, gömülü değer, çoktan-çoğa bağ (yük ve durum taşıyabilir), kullanıcıya bağlı bağ, tip, rol, alt tip |
| [Durum](docs/guides/state-machines.md) ve [iş mantığı](docs/guides/business-logic.md) | Durum makinesi, geçişler, ek durum eksenleri, belge durumu, entity aksiyonları, handler'lar |
| [Okuma](docs/guides/querying.md) | Sayfalama, kendi filtre sınıfınız, sıralama, tipli arama, doğal anahtar |
| [Kapsam](docs/guides/tenancy-and-ownership.md) | Çok kiracılı ve kullanıcıya ait kaynaklar |
| [Alanlar](docs/guides/fields.md) | Denetim alanları, anahtar biçimleri, boşluksuz numaralar, metadata, kiracının tanımladığı özel alanlar, çeviriler, [hassas ve şifreli alanlar](docs/guides/sensitive-data.md) |
| [Yaşam döngüsü](docs/guides/lifecycle.md) | Geri alınabilir silme, arşiv, eşzamanlılık denetimi, süre dolumu, saklama süresi, tekrara karşı koruma, geçerlilik dönemleri, tekrar serisi |
| [Kayıt işlemleri](docs/guides/record-operations.md) | Toplu işlemler, kopya, sıra, önizleme, ölçüte göre silme, değişiklik kümeleri |
| [Kayıtlar arası zincir](docs/guides/record-chains.md) | Ters kayıt, yerine geçen kayıt, başka kayıttan üretme, türetilmiş kaynak |
| [Onay](docs/guides/approvals.md) ve [geçmiş](docs/guides/history-and-publishing.md) | Çok düzeyli onay, sürümler ve geri yükleme, taslak ve yayın, değişiklik akışı, okundu bilgisi |
| [Altyapı](docs/README.md#rehberler) | [Dosyalar](docs/guides/files.md) (veritabanı, dosya sistemi, S3), [arka plan işleri](docs/guides/jobs.md) (bellek, PostgreSQL, Hangfire), [bakiyeler](docs/guides/balances.md), [olaylar](docs/guides/events.md) (webhook, SQS, RabbitMQ, Kafka), [sırlar](docs/guides/secrets.md), [modüller](docs/guides/modules.md) |

Bütün bildirimler: [bildirim dizini](docs/reference/declarations.md).

## Hızlı başlangıç

Gereksinimler: .NET 10 SDK, PostgreSQL.

```bash
dotnet add package Haldus.AspNetCore --version 0.1.0-preview.1
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
```

<!-- example: quickstart -->
```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();

var app = builder.Build();

app.UseHaldus();
app.MapCrud<Product>();

app.Run();

public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public decimal Price { get; set; }
}
```

```bash
dotnet ef migrations add Initial
dotnet ef database update
dotnet run
```

Adım adım anlatım: [eğitim: ilk API'niz](docs/getting-started/tutorial.md).

## Belgeler

| | |
|---|---|
| [Başlarken](docs/getting-started/installation.md) | Kurulum ve proje ve iş takibi API'sini adım adım kuran eğitim. |
| [Kavramlar](docs/concepts/how-it-works.md) | Haldus nasıl çalışır; adresler, anahtarlar, `PUT` ile `PATCH`, hatalar. |
| [Rehberler](docs/README.md#rehberler) | Her özellik alanı için bildirimler, üretilen uçlar, örnek istek ve yanıtlar, kurallar ve sınırlar. |
| [Başvuru](docs/reference/declarations.md) | Bildirim dizini, yapılandırma, HTTP sözleşmesi, tanılar, paketler. |
| [SSS](docs/faq.md) | Sık sorulan sorular ve sorun giderme. |
| [docs/guide.md](docs/guide.md) | Kodlama ajanları için İngilizce, tek dosyalık başvuru (paketle birlikte gelir). |

Belgelerin tamamı: [docs/README.md](docs/README.md).

## Örnekler

| Örnek | |
|---|---|
| [Proje ve iş takibi](samples/Haldus.Sample.Tracker) | Eğitimin bitmiş uygulaması; küçük, migration'larıyla. |
| [Örnek mağaza](samples/Haldus.Sample.Shop) | Bildirimlerin hemen hepsi, gerçek bir alanda. |
| [Modüler örnek](samples/Haldus.Sample.Modular) | Ayrı sınıf kütüphaneleri ve bağlamlar. |
| [Yönetim paneli](samples/Haldus.Sample.Shop.Web), [mobil uygulama](samples/Haldus.Sample.Shop.Mobile), [TypeScript istemcisi](samples/Haldus.Sample.Shop.Client) | Mağazanın OpenAPI şemasından üretilen istemcilerle React ve Expo. |

## Katkıda bulunmak

Geliştirme ortamı, testler, snapshot'lar ve belge yazma kuralları [CONTRIBUTING.md](CONTRIBUTING.md) dosyasındadır.
Tasarım kararlarının ayrıntılı kaydı [CLAUDE.md](CLAUDE.md), dayandıkları araştırma [reports](reports/) klasöründedir.
Değişiklikler: [CHANGELOG.md](CHANGELOG.md). Güvenlik açıkları: [SECURITY.md](SECURITY.md).

## Lisans

[MIT](LICENSE).
