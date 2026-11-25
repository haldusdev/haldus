# Toplanan alanlar

Bir kaydın bazı alanları başka kayıtlardan gelir: ürünün yorum sayısı, faturanın ödenen tutarı, tartışmanın yanıt
sayısı. Bunları her okumada saymak pahalıdır; elle tutmak ise hataya açıktır (bir yazma unutulursa sayı kalıcı olarak
yanlış kalır). `.Counts<TChild>()` ve `.Sums<TChild>(...)` bu alanları Haldus'a bıraktırır: alan kayıtta saklanır ve
alt kayıt her yazıldığında, aynı transaction içinde baştan hesaplanır. Toplamdan türeyen bir durum (ödendi, kısmen
ödendi) ise üst kaydın `Compute()` metodunda yazılır.

> [!NOTE]
> Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
> `System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs` içindedir;
> `var builder = WebApplication.CreateBuilder(args);` ve `var app = builder.Build();` satırları yazılmamışsa yazılmış
> sayılır.

## İçindekiler

- [Sayım ve toplam](#sayım-ve-toplam)
- [Alanın adı](#alanın-adı)
- [Nasıl hesaplanır](#nasıl-hesaplanır)
- [Türetilmiş durum](#türetilmiş-durum)
- [Kullanıcılara ait kayıtları toplamak](#kullanıcılara-ait-kayıtları-toplamak)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Sayım ve toplam

`.Counts<TChild>()` kayda, kendisine bağlı `TChild` kayıtlarının sayısını tutan bir alan ekler. `.Sums<TChild>(...)`
bağlı kayıtların bir sayısal alanının toplamını tutan bir alan ekler. İkisi de üst kaydın zincirinde yazılır; alt
kaynak üst kaynağa `References<T>()`, `Under<T>()` veya `AttachedTo<T>()` ile bağlı olmalıdır.

<!-- example: counts-and-sums -->
```csharp
app.MapCrud<Product>()
    .Counts<Review>()
    .Sums<Review>(review => review.Rating, into: "RatingTotal")
    .Sortable(product => product.ReviewCount, product => product.AverageRating);

app.MapCrud<Review>().Under<Product>().SoftDeletable();

public partial class Product : IComputed
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public decimal Price { get; set; }

    public decimal AverageRating { get; private set; }

    // ReviewCount ve RatingTotal Haldus'un ürettiği alanlardır.
    public void Compute() =>
        AverageRating = ReviewCount == 0 ? 0 : decimal.Round((decimal)RatingTotal / ReviewCount, 1);
}

public partial class Review
{
    [MaxLength(2000)]
    public string Text { get; set; } = "";

    [Range(1, 5)]
    public int Rating { get; set; }
}
```

Haldus `Product` entity'sine `ReviewCount` (`int`) ve `RatingTotal` (`int`, toplanan alanın tipi) alanlarını ekler.
Alanlar yalnızca yanıtta görünür:

```http
POST /products/0199a3e0-0a00-7c10-8a10-0a1b2c3d4e50/reviews
Content-Type: application/json

{ "text": "Tuşları çok sessiz.", "rating": 5 }
```

```http
GET /products/0199a3e0-0a00-7c10-8a10-0a1b2c3d4e50
```

```http
HTTP/1.1 200 OK

{
  "id": "0199a3e0-0a00-7c10-8a10-0a1b2c3d4e50",
  "name": "Mekanik klavye",
  "price": 1450,
  "averageRating": 4.5,
  "reviewCount": 2,
  "ratingTotal": 9
}
```

**Kurallar**

- Alan istekle yazılamaz; `PUT` gövdesinde `"reviewCount": 99` gönderilse de yok sayılır.
- Alan veritabanında saklanır. `Sortable` ve `Searchable` içinde sayılırsa liste ona göre sıralanır ve aranır:
  `GET /products?sort=-reviewCount`.
- Yeni kayıt sıfırla başlar.

**Seçenekler**

| Bildirim | Parametre | Varsayılan | Anlamı |
|---|---|---|---|
| `Counts<TChild>` | `into` | `{Alt}Count` (`ReviewCount`) | Alanın adı. |
| `Counts<TChild>` | `acrossOwners` | `false` | Kullanıcılara ait kayıtların ortak bir kayıtta sayılmasına izin verir; bkz. [Kullanıcılara ait kayıtları toplamak](#kullanıcılara-ait-kayıtları-toplamak). |
| `Sums<TChild>` | `field` | — | Toplanan alan: `review => review.Rating` biçiminde, alt kaynağın null olamayan `decimal`, `int`, `long` veya `double` alanı. |
| `Sums<TChild>` | `into` | `{Alt}{Alan}Sum` (`ReviewRatingSum`) | Alanın adı. |
| `Sums<TChild>` | `acrossOwners` | `false` | `Counts` ile aynı. |

Argümanlar derleme zamanında okunur: `into` bir metin sabiti, `acrossOwners` bir `bool` sabiti olmalıdır. Okunamayan bir
argüman `HALDUS010` verir.

## Alanın adı

`into` verilmezse alanın adı alt kaynağın adından türetilir: `Counts<Reply>()` → `ReplyCount`,
`Sums<Payment>(payment => payment.Amount)` → `PaymentAmountSum`. Aynı kayıtta iki toplanan alan aynı adı taşıyamaz ve
ad geçerli bir C# tanımlayıcısı olmalıdır; değilse `HALDUS010` verilir. Alanı entity'ye siz yazmazsınız; `Compute()` ve
`Sortable` gibi yerlerde üretilen adıyla kullanırsınız.

## Nasıl hesaplanır

Alan artırılıp azaltılmaz; her seferinde **baştan sayılır**. Bu yüzden kaçırılmış bir yazma kalıcı bir yanlış bırakmaz:
bir sonraki izlenen yazma alanı düzeltir.

- **Ne zaman:** bir alt kayıt eklendiğinde, silindiğinde, başka bir üst kayda taşındığında (yabancı anahtarı
  değiştiğinde; eski ve yeni üst kaydın ikisi de yeniden sayılır), toplanan alanı değiştiğinde, geri alınabilir biçimde
  silinip geri yüklendiğinde ve süresi değiştiğinde. Alt kaydın toplamı etkilemeyen bir alanı değişirse hiçbir şey
  yapılmaz.
- **Nerede:** kural kaydetmeye bağlıdır, uçlara değil. Alt kayıt hangi koddan yazılırsa yazılsın (uç, toplu işlem, arka
  plan işi, uygulamanın kendi kodunun `SaveChangesAsync` çağrısı) üst kaydın alanı aynı transaction içinde yazılır.
  Alt kayıt ile üst kaydın alanı birlikte kalıcı olur.
- **Hangi kayıtlar sayılır:** üst kayda bağlı bütün alt kayıtlar, isteği yapanın neyi görebildiğinden bağımsız olarak.
  Kiracı, sahiplik ve ebeveyn filtreleri sayımda kaldırılır. Geri alınabilir biçimde silinmiş, süresi dolmuş ve yayın
  kopyası olan kayıtlar sayılmaz.
- **Eşzamanlılık:** sayım üst kayıt başına bir kilidin altında yapılır. Aynı üst kayda birlikte yazan istekler sıraya
  girer ve hiçbir yazma kaybolmaz.

Toplanan alan değişikliği üst kaydın bir içerik yazması değildir. Üst kaydın `updatedAt` alanı, sürümleri, onayı ve
değişiklik akışı etkilenmez. Üst kaynak `ConcurrencyChecked` ise satır sürümü yine de ilerler: alt kayıt eklendikten
sonra üst kaydın `ETag` değeri değişir ve eski değerle gelen bir yazma `412` alır.

## Türetilmiş durum

Başka kayıtlardan türeyen bir durum ayrı bir bildirim değildir. Toplanan alanı üst kaydın `Compute()` metodunda
kullanırsınız: toplam değiştikçe Haldus `Compute()` metodunu yeniden çağırır.

<!-- example: derived-state -->
```csharp
app.MapCrud<Bill>()
    .Sums<BillPayment>(payment => payment.Amount, into: "PaidAmount");

app.MapAppendOnly<BillPayment>().References<Bill>();

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<BillState>))]
public enum BillState
{
    Unpaid,
    PartiallyPaid,
    Paid,
}

public partial class Bill : IComputed
{
    [MaxLength(200)]
    public string Payee { get; set; } = "";

    [Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }

    public BillState State { get; private set; }

    // PaidAmount üretilen alandır: ödemelerin toplamı.
    public void Compute() =>
        State = PaidAmount >= Amount ? BillState.Paid
            : PaidAmount > 0 ? BillState.PartiallyPaid
            : BillState.Unpaid;
}

public partial class BillPayment
{
    [Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }
}
```

```http
POST /bill-payments
Content-Type: application/json

{ "billId": "0199a3e1-0b00-7c10-8a10-0a1b2c3d4e50", "amount": 40 }
```

```http
GET /bills/0199a3e1-0b00-7c10-8a10-0a1b2c3d4e50
```

```http
HTTP/1.1 200 OK

{
  "id": "0199a3e1-0b00-7c10-8a10-0a1b2c3d4e50",
  "payee": "Acme Kırtasiye",
  "amount": 100,
  "state": "PartiallyPaid",
  "paidAmount": 40
}
```

- Toplam değiştikten sonra üst kayıt satırlarıyla yüklenir, `Compute()` çağrılır ve sonuç değiştiyse kayıt sıradan bir
  kaydetmeyle yazılır. O kaydetmede üst kaydın bütün kuralları işler (`updatedAt`, sürümler, onayın düşmesi, olaylar).
- Bu kaydetme eşzamanlı bir yazmayla çakışırsa kayıt yeniden okunur ve üç kez daha denenir.
- Üst kaydın kendi alanı değişince de durum yeniden hesaplanır: yukarıda tutarı `150` yapan bir `PATCH`, ödenen tutar
  `100` iken durumu `PartiallyPaid` yapar.
- `State` herkese açık setter'ı olmadığı için istemci onu yazamaz. Hesaplanan alanlar
  [Detaylar ve hesaplanan alanlar](details-and-computed-fields.md) sayfasında anlatılır.

## Kullanıcılara ait kayıtları toplamak

Toplanan alan, üst kaydı görebilen herkese açıktır. Alt kayıtlar onu görenlerin göremediği kayıtlarsa, alan bir bilgi
sızıntısı olabilir. Haldus bu yüzden iki durumu denetler:

- **Kiracılar:** kiracıya ait kayıtlar genel (kiracıya ait olmayan) bir kayıtta toplanamaz; bildirim her zaman
  `HALDUS007` verir. Herkesin gördüğü bir alan, her kiracının kaç kaydı olduğunu ötekilere gösterirdi. Üst kaynağı da
  `MultiTenant` yapın ya da kiracıya ait olmayan bir kaynağı sayın.
- **Kullanıcılar:** kullanıcılara ait kayıtlar (kendisi `OwnedByCurrentUser` olan veya sahipli bir ebeveynin altında
  duran kayıtlar), o kullanıcıya ait olmayan bir kayıtta toplanırsa bildirim `HALDUS007` verir. Beğeni sayısı gibi
  bilerek istenen durumlarda `acrossOwners: true` yazarsınız.

<!-- example: across-owners -->
```csharp
app.MapCrud<Product>()
    .Counts<ProductLike>(into: "LikeCount", acrossOwners: true);

app.MapAppendOnly<ProductLike>()
    .References<Product>()
    .OwnedByCurrentUser();

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class ProductLike
{
    [MaxLength(200)]
    public string? Note { get; set; }
}
```

Her kullanıcı yalnızca kendi beğenilerini görür (`/me/product-likes`), ama ürünün `likeCount` alanı bütün kullanıcıların
beğenilerini sayar ve ürünü gören herkese açıktır.

## Birlikte kullanım

- Üst kaynak `MapCrud`, `MapReadOnly` veya `MapAppendOnly` ile eşlenmiş olmalıdır (ayrı uçlu detaylar da olur);
  `MapStore` gibi öteki archetype'lar `HALDUS007` verir. Üst kaydın tek kolonlu bir anahtarı olmalıdır.
- Alt kaynak aynı projede bir `Map*` çağrısıyla eşlenmiş olmalıdır (`HALDUS009`) ve üst kaynağa bir referansla bağlı
  olmalıdır; değilse `HALDUS007` verilir ve mesaj eklenecek bildirimi söyler.
- `Publishable` ile birleşmez (`HALDUS007`).
- Bir kayıtta birden çok toplanan alan olabilir; aynı alt kaynağın hem sayısı hem toplamı tutulabilir.
- `IComputed` ile birlikte türetilmiş durum yazılır; bkz. [Türetilmiş durum](#türetilmiş-durum).

## Sınırlar

- Değişiklik izleyicisini atlayan yazmalar (`ExecuteUpdate`, `ExecuteDelete`, bakımın temizliği, veritabanının
  zincirleme silmesi) toplamı yenilemez. Bir sonraki izlenen alt kayıt yazması onu düzeltir.
- Süresi dolan (`Expiring`) bir alt kayıt, başka bir yazma yeniden saydırana kadar toplamda kalır.
- Toplanan alan null olamayan `decimal`, `int`, `long` veya `double` olmalıdır. Ortalama, en küçük, en büyük gibi başka
  toplama biçimleri yoktur; ortalamayı yukarıdaki gibi `Compute()` içinde sayı ve toplamdan hesaplarsınız.
- Her alt kayıt yazması, üst kayıt başına iki-üç ek sorgudur; aynı üst kaydın yazmaları kilit yüzünden sıraya girer.
  Tek bir üst kayda çok yoğun yazılan kaynaklarda bu bir darboğaz olabilir.
- Başka bir kaynağın `Counts<Üst>()` toplamı, alt tip hiyerarşisinde alt tiplerin kayıtlarını saymaz; bkz.
  [Alt tip](relationships.md#alt-tip).

## Ayrıca bakın

- [Detaylar ve hesaplanan alanlar](details-and-computed-fields.md): `IComputed` ve kaydın kendi satırlarından
  hesaplanan alanlar.
- [İlişkiler](relationships.md): alt kaynağı üst kaynağa bağlayan `References`, `Under`.
- [Kiracılar ve sahiplik](tenancy-and-ownership.md)
- [Bildirim dizini](../reference/declarations.md)
