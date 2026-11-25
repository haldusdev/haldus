# Haldus nasıl çalışır

Haldus bir kod üreticisidir. `Program.cs` dosyanızdaki `Map*` çağrılarını derleme sırasında okur ve bir uygulamanın
hemen her kaynakta yeniden yazdığı kodu üretir: entity'nin geri kalanı, istek ve yanıt tipleri, Minimal API uçları,
doğrulama, durum kuralları ve EF Core yapılandırması. Bu sayfa neyin ne zaman olduğunu ve bunun yazdığınız koda
getirdiği kuralları anlatır.

## İçindekiler

- [Derleme ve çalışma zamanı](#derleme-ve-çalışma-zamanı)
- [Bir kaynağın dört katmanı](#bir-kaynağın-dört-katmanı)
- [Entity'nin iki yarısı](#entitynin-iki-yarısı)
- [Zincir derleme zamanında okunur](#zincir-derleme-zamanında-okunur)
- [Kurallar nerede uygulanır](#kurallar-nerede-uygulanır)
- [Kendi kodunuzla birlikte](#kendi-kodunuzla-birlikte)
- [Haldus'un yapmadıkları](#haldusun-yapmadıkları)

## Derleme ve çalışma zamanı

Haldus iki parçadır ve ikisi de `Haldus.AspNetCore` paketindedir:

| Parça | Ne zaman çalışır | Ne yapar |
|---|---|---|
| Kaynak üreticisi (Roslyn incremental source generator) | Her derlemede, IDE'de yazarken de | `Map*` çağrılarını ve zincirlerini okur, kodu üretir, yanlış bildirimleri derleme hatası (`HALDUS001`–`HALDUS010`) olarak bildirir. |
| Çalışma zamanı kütüphanesi | Uygulama çalışırken | `AddHaldus` ile kaydedilen servisler: EF Core model katkıları, kaydetme sırasında alanları dolduran ve kuralları uygulayan bir interceptor, sorgu filtreleri, doğrulama, hata yanıtları, mesajlar, bakım ve olay servisleri. |

Bir `MapCrud<Product>()` satırı iki kez iş görür. Derleme sırasında generator onu görür ve `Product` için uç sınıfını
üretir. Uygulama çalışırken aynı satır, üretilmiş uç sınıfını bulur ve uçları eşler. Çalışma zamanında yansıma
(reflection) ile kod kurulmaz; yansıma yalnızca doğrulama filtresinde kullanılır.

Üretilen kod uygulamanın bir parçasıdır: IDE onu görür, içine adım atabilirsiniz ve derleyici onu sizin kodunuzla
birlikte denetler. Dosyaları diske yazdırmak için proje dosyasına `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>`
ekleyin; bkz. [kurulum](../getting-started/installation.md#üretilen-kodu-görmek).

## Bir kaynağın dört katmanı

Her kaynak dört katmanla tanımlanır:

| Katman | Kaç tane | Örnek |
|---|---|---|
| **Archetype**: kaynağın hangi işlemleri olduğu | Tam olarak bir | `MapCrud`, `MapAppendOnly`, `MapStore`, `MapJob`... |
| **İlişkiler**: başka kaynaklarla bağı | Sıfır veya daha çok | `.References<Category>()`, `.Under<Customer>()` |
| **Durum modeli**: kaydın hangi durumlardan geçtiği | En fazla bir | `.Workflow<OrderStatus>()`, `.Document()` |
| **Trait'ler**: serbestçe birleşen davranışlar | Sıfır veya daha çok | `.MultiTenant()`, `.Auditable()`, `.SoftDeletable()` |

<!-- example: layers -->
```csharp
var orders = app.MapCrud<Order>()                       // archetype
    .References<Customer>()                             // ilişki
    .Workflow<OrderStatus>(initial: OrderStatus.Draft)  // durum modeli
    .EditableIn(OrderStatus.Draft)
    .MultiTenant()                                      // trait'ler
    .Auditable()
    .ConcurrencyChecked();

orders.MapTransition("place", from: OrderStatus.Draft, to: OrderStatus.Placed);

app.MapCrud<Customer>().MultiTenant();

public enum OrderStatus { Draft, Placed }

public partial class Order
{
    public decimal Total { get; set; }
}

public partial class Customer
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

Archetype'lar ve hangisinin ne zaman seçileceği: [archetype'lar](../guides/archetypes.md). Bütün bildirimlerin listesi:
[bildirim dizini](../reference/declarations.md).

## Entity'nin iki yarısı

Entity'yi Haldus ile birlikte yazarsınız. Siz kendi alanlarınızı bir `partial class` olarak yazarsınız; Haldus öteki
yarısını üretir:

| Kim yazar | Ne |
|---|---|
| Siz | Kendi alanlarınız ve onların doğrulama kuralları (`[Required]`, `[MaxLength]`, `[Range]`...), kendi metotlarınız, iş mantığı sözleşmeleri (`IComputed`...). |
| Haldus | Anahtar (`Id`, `Key` veya `Code`), ilişkilerin yabancı anahtarları (`CategoryId`, `ParentId`), durum (`Status`), numara (`Number`) ve trait'lerin alanları (`TenantId`, `CreatedAt`, `DeletedAt`...). |

Bir alanın isteklerde ve yanıtlarda görünüp görünmeyeceği nasıl yazıldığına bağlıdır:

<!-- example: entity-halves -->
```csharp
app.MapCrud<Invoice>().Auditable();

public partial class Invoice
{
    // Herkese açık setter: oluşturma, PUT ve PATCH isteklerinde ve yanıtta.
    [Required]
    [MaxLength(200)]
    public string Customer { get; set; } = "";

    public decimal Amount { get; set; }

    // Setter'ı herkese açık değil: yalnızca yanıtta. Onu kendi kodunuz yazar (ör. bir geçiş veya Compute()).
    public DateTimeOffset? PaidAt { get; private set; }

    public void MarkPaid(DateTimeOffset at) => PaidAt = at;
}
```

Bu örnekten Haldus `Invoice` sınıfının öteki yarısına `Id`, `CreatedAt` ve `UpdatedAt` alanlarını; ayrıca
`InvoiceResponse`, `CreateInvoiceRequest`, `UpdateInvoiceRequest` ve `PatchInvoiceRequest` tiplerini üretir. İstek
tiplerinde yalnızca `Customer` ve `Amount` vardır ve doğrulama kuralları onlara kopyalanır.

Entity'yi hiç yazmazsanız Haldus onu boş olarak, `Map*` çağrısının bulunduğu ad alanına üretir. Bu, yalnızca Haldus'un
alanlarından oluşan kaynaklar için kullanışlıdır (bir etiket listesi gibi).

> [!IMPORTANT]
> Anahtar dışında Haldus'un ürettiği alanları (`Status`, `TenantId`, `CreatedAt`, yabancı anahtarlar...) kendiniz
> yazmayın. Anahtarı yazabilirsiniz: `public int Id { get; set; }` yazarsanız Haldus anahtarı sizinkinden okur; tipi
> `Guid`, `int` veya `long` olmalıdır (`HALDUS003`).

## Zincir derleme zamanında okunur

Generator zinciri kaynak koddan okur; kodunuzu çalıştırmaz. Bu yüzden zincir **statik** olmalıdır: bildirimler
doğrudan `Map*` çağrısına zincirlenir, argümanları derleme zamanında okunabilen sabitlerdir.

<!-- example: chain-static-wrong, error: HALDUS004 -->
```csharp
var products = app.MapCrud<Product>();

if (app.Environment.IsDevelopment())
{
    products.Auditable();   // HALDUS004: generator bu çağrıyı göremez
}

public partial class Product
{
    public string Name { get; set; } = "";
}
```

Generator bu satırı derleme hatası olarak bildirir; çalışma zamanında sessizce yok sayılan bir bildirim olmaz. Doğrusu,
bildirimi koşulsuz olarak zincire yazmaktır:

<!-- example: chain-static-right -->
```csharp
app.MapCrud<Product>().Auditable();

public partial class Product
{
    public string Name { get; set; } = "";
}
```

Builder bir değişkende tutulabilir. Ama değişken üzerinden yalnızca **uç ekleyen** bildirimler çağrılır: `MapDetail`,
`MapTransition` ve `MapAction`. Trait'ler, ilişkiler ve durum modeli zincirin kendisinde yazılır.

Argümanlar da derleme zamanında okunur: durumlar enum üyesi (`OrderStatus.Draft`), adlar metin sabiti, süreler sabit
argümanlı bir ifade (`TimeSpan.FromMinutes(15)`), alanlar `item => item.Name` biçiminde bir lambda olmalıdır.
Okunamayan bir argüman `HALDUS010` olur. Tanıların hepsi: [tanılar](../reference/diagnostics.md).

## Kurallar nerede uygulanır

Haldus'un kuralları üç yerde yaşar. Bunu bilmek, kendi kodunuzun hangi kurallardan geçtiğini anlamanızı sağlar.

| Yer | Hangi kurallar | Kendi kodunuz için |
|---|---|---|
| **Üretilen uçlar** | İsteğin doğrulanması, durum kontrolü (`EditableIn`, geçişin `from:` durumu), `If-Match`, referansların varlığı, doğal anahtarın benzersizliği, `Compute()` çağrısı. | Bunlar yalnızca uçlardan geçen isteklerde çalışır. |
| **Kaydetme** (EF Core interceptor) | Denetim alanları, kiracı ve sahibin atanması, geri alınabilir silmenin güncellemeye çevrilmesi, sürümler, değişiklik günlüğü, olaylar, toplanan alanlar, onayın düşmesi. | Kaydı kendi kodunuz `SaveChangesAsync` ile yazsa da çalışır. |
| **Sorgu filtreleri** (EF Core adlandırılmış filtreleri) | Kiracı, sahip, geri alınabilir silme, süre dolumu, ebeveynin görünürlüğü, yayın kopyaları. | Bağlamdan yaptığınız her sorguya uygulanır. |

Bir sonucu: görünürlük tek tek uçlara değil modele konur. Bir kaynağa referans veren, onun altına yerleşen veya onu
listeleyen her sorgu kendiliğinden korunur. Bir filtreyi kaldırmanız gerekirse yalnızca onu, adıyla kaldırın:

<!-- example: ignore-filter -->
```csharp
builder.Services.AddHaldus<AppDbContext>();

app.MapCrud<Product>().MultiTenant().SoftDeletable();

// Silinmiş ürünleri de görmek: yalnızca geri alınabilir silmenin filtresi kalkar, kiracı filtresi kalır.
app.MapGet("/reports/all-products", (AppDbContext db) =>
    db.Products.IgnoreQueryFilters([HaldusFilters.SoftDelete]).CountAsync());

public partial class Product
{
    public string Name { get; set; } = "";
}
```

> [!WARNING]
> Parametresiz `IgnoreQueryFilters()` kiracı ve sahiplik filtrelerini de kaldırır; bir kiracının verisini öteki
> kiracılara açabilir.

## Kendi kodunuzla birlikte

Haldus uygulamanın tamamını üstlenmez; sıradan ASP.NET Core ve EF Core kodunun yanında çalışır.

- **Kendi uçlarınız.** `app.MapGet`, `app.MapPost`, controller'lar: hepsi Haldus'un uçlarıyla birlikte çalışır.
- **Bağlam.** Haldus'un eklediği `DbSet` özellikleriyle (`db.Products`) veya `db.Set<Product>()` ile bildiğiniz gibi
  sorgulayıp yazarsınız. Kaydetmeye bağlı kurallar sizin yazmalarınızda da işler.
- **Uçların ayarları.** `Map*` çağrısının döndürdüğü builder bir `IEndpointConventionBuilder`'dır:
  `.RequireAuthorization()`, `.RequireRateLimiting(...)`, `.WithTags(...)` kaynağın bütün uçlarına uygulanır.
- **Model.** `OnModelCreating` içinde kendi entity'lerinizi yapılandırırsınız; Haldus'un katkısı ona eklenir.

<!-- example: own-endpoint -->
```csharp
builder.Services.AddHaldus<AppDbContext>();

app.MapCrud<Product>().RequireAuthorization();

// Haldus'un yanında sıradan bir uç.
app.MapGet("/products/cheapest", async (AppDbContext db, CancellationToken cancellationToken) =>
    await db.Products.OrderBy(product => product.Price).FirstOrDefaultAsync(cancellationToken));

public partial class Product
{
    public string Name { get; set; } = "";

    public decimal Price { get; set; }
}
```

## Haldus'un yapmadıkları

| Konu | Ne yaparsınız |
|---|---|
| Veritabanı şeması | EF Core migration'ları; bkz. [veritabanı ve migration'lar](../guides/database.md). |
| Kimlik doğrulama | ASP.NET Core'un kimlik doğrulaması. Haldus kullanıcıyı ve kiracıyı claim'lerden okur; bkz. [kiracılar, kullanıcılar ve yetkilendirme](../guides/tenancy-and-ownership.md). |
| Frontend ve mobil kod | OpenAPI şemasından üretilen istemciler; bkz. [frontend ve mobil istemciler](../guides/frontend.md). |
| Veritabanı seçimi | Yalnızca PostgreSQL desteklenir. |

## Ayrıca bakın

- [Ortak kurallar](conventions.md): adresler, anahtarlar, `PUT` ile `PATCH`, hata yanıtları.
- [Eğitim: ilk API'niz](../getting-started/tutorial.md)
- [Bildirim dizini](../reference/declarations.md)
