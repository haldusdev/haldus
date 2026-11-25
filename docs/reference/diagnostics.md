# Tanılar

Haldus'un generator'ı zinciri derleme zamanında okur. Okuyamadığı veya çelişkili bulduğu bir bildirimi sessizce yok
saymaz; `HALDUS001`–`HALDUS010` kodlu bir derleme hatası verir. Hepsinin önem derecesi hatadır (`Error`); uyarı yoktur.
Bu sayfa her tanının ne demek olduğunu, neden çıktığını ve nasıl düzeltileceğini anlatır.

Her tanının yardım bağlantısı (`helpLinkUri`) İngilizce başvurunun, [docs/guide.md](../guide.md) dosyasının ilgili
başlığına gider (`https://github.com/haldusdev/haldus/blob/main/docs/guide.md#haldus007` gibi). IDE'nizde tanı koduna
tıkladığınızda açılan sayfa odur; bu sayfa aynı tanıların Türkçe ve örnekli halidir.

Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır; `builder` ve `app` değişkenlerini kuran satırlar da öyle.
Her tanı için önce hatayı veren, sonra düzeltilmiş örnek verilir.

## İçindekiler

| Kod | Kısaca |
|---|---|
| [`HALDUS001`](#haldus001) | Entity üst düzey bir `partial class` değil. |
| [`HALDUS002`](#haldus002) | Entity birden çok `Map*` çağrısıyla eşlenmiş. |
| [`HALDUS003`](#haldus003) | Anahtarın tipi bu archetype'ta desteklenmiyor. |
| [`HALDUS004`](#haldus004) | Bildirim zincire değil bir değişkene yazılmış. |
| [`HALDUS005`](#haldus005) | `Searchable` alanı aranabilir bir alan değil. |
| [`HALDUS006`](#haldus006) | `Searchable()` alansız çağrılmış. |
| [`HALDUS007`](#haldus007) | İki bildirim birleşemiyor veya bildirim archetype'la çelişiyor. |
| [`HALDUS008`](#haldus008) | `MapQuery` istek tipinin parametresiz kurucusu yok. |
| [`HALDUS009`](#haldus009) | İlişkinin hedefi eşlenmiş bir kaynak değil. |
| [`HALDUS010`](#haldus010) | Bildirimin argümanı derleme zamanında okunamıyor veya geçersiz. |

## HALDUS001

**Entity must be a partial class.** Eşlenen entity üst düzey bir `partial class` değil.

Haldus entity'nin öteki yarısını (anahtar, trait alanları, yabancı anahtarlar) üretir. Bunun için entity'nin bir
sınıf (`record` veya `struct` değil) olması, `partial` yazılması ve başka bir tipin içinde tanımlanmamış olması
gerekir.

<!-- example: haldus001, error: HALDUS001 -->
```csharp
app.MapCrud<Product>();

public class Product
{
    public string Name { get; set; } = "";
}
```

Sınıfı `partial` yapın ve başka bir sınıfın içinden çıkarın:

<!-- example: haldus001-fixed -->
```csharp
app.MapCrud<Product>();

public partial class Product
{
    public string Name { get; set; } = "";
}
```

## HALDUS002

**Entity is mapped more than once.** Aynı entity birden çok `Map*` çağrısıyla eşlenmiş.

Bir entity tam olarak bir archetype'a sahiptir. İki çağrı iki ayrı uç kümesi ve iki model yapılandırması demek olurdu.

<!-- example: haldus002, error: HALDUS002 -->
```csharp
app.MapCrud<Product>();
app.MapReadOnly<Product>();

public partial class Product
{
    public string Name { get; set; } = "";
}
```

Birini silin. Yalnızca bazı işlemleri istiyorsanız `Without` / `Only` kullanın; aynı veriyi iki ayrı biçimde sunmanız
gerekiyorsa iki ayrı entity yazın.

<!-- example: haldus002-fixed -->
```csharp
app.MapCrud<Product>()
    .Only(CrudOperation.List, CrudOperation.Get, CrudOperation.Create);

public partial class Product
{
    public string Name { get; set; } = "";
}
```

## HALDUS003

**Unsupported key type.** Entity'de yazdığınız anahtar alanının tipi bu archetype'ta desteklenmiyor. Mesaj alanı,
yazdığınız tipi ve desteklenen tipleri söyler.

| Archetype | Anahtar | Desteklenen tipler |
|---|---|---|
| `MapCrud`, `MapReadOnly`, `MapAppendOnly`, `MapFile` | `Id` | `Guid`, `int`, `long` |
| `MapJob` | `Id` | `Guid` |
| `MapStore` | `Key` | `string`, `Guid`, `int`, `long` |
| `MapLookup` | `Code` | `string` |
| `MapBalance` | `Key` | `string` |

<!-- example: haldus003, error: HALDUS003 -->
```csharp
app.MapCrud<Product>();

public partial class Product
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";
}
```

Anahtarı desteklenen bir tiple yazın ya da hiç yazmayın; Haldus `Guid` (v7) üretir. Metin bir kimlik istiyorsanız
anahtarı yazmak yerine biçimini seçin (`.KeyStrategy(KeyStrategy.Prefixed, "prd")`); bkz.
[anahtar biçimi](../guides/fields.md#anahtar-biçimi).

<!-- example: haldus003-fixed -->
```csharp
app.MapCrud<Product>();

public partial class Product
{
    public string Name { get; set; } = "";
}
```

## HALDUS004

**Declaration is not part of a Map call chain.** Bir bildirim `Map*` çağrısının zincirine değil, bir değişkene
yazılmış.

Generator zinciri derleme zamanında, kaynak koddan okur. Bir değişken üzerinden sonradan yapılan çağrıyı (koşullu
veya koşulsuz) göremez; zincir metotları çalışma zamanında hiçbir şey yapmadığı için çağrı sessizce etkisiz kalırdı.

<!-- example: haldus004, error: HALDUS004 -->
```csharp
var products = app.MapCrud<Product>();
products.MultiTenant();

public partial class Product
{
    public string Name { get; set; } = "";
}
```

Bildirimi doğrudan `Map*` çağrısına zincirleyin:

<!-- example: haldus004-fixed -->
```csharp
app.MapCrud<Product>()
    .MultiTenant();

public partial class Product
{
    public string Name { get; set; } = "";
}
```

Builder bir değişkende tutulabilir; ama ona yalnızca sahibin builder'ından yapılan çağrılar (`MapDetail`,
`MapTransition`, `MapAction`) ve sıradan Minimal API çağrıları (`RequireAuthorization`, `MapGet`) yazılır.
`AsSubresource` ve `AsCommands` de `MapDetail` çağrısına doğrudan zincirlenmelidir.

## HALDUS005

**Field cannot be searched.** `Searchable(...)` içine yazılan ifade aranabilir bir alan değil.

Her argüman `item => item.Property` biçiminde olmalı ve özellik entity'nin anahtarı, skaler bir alanı veya bir
trait'in açık alanı (`CreatedAt` gibi) olmalıdır. Bir alanın üzerinde yapılan hesap, koleksiyon veya iç içe nesne
aranamaz. Hassas (`Sensitive`) alanlar da aday sayılmaz.

<!-- example: haldus005, error: HALDUS005 -->
```csharp
app.MapCrud<Product>()
    .Searchable(product => product.Name.Length);

public partial class Product
{
    public string Name { get; set; } = "";
}
```

Alanın kendisini yazın:

<!-- example: haldus005-fixed -->
```csharp
app.MapCrud<Product>()
    .Searchable(product => product.Name);

public partial class Product
{
    public string Name { get; set; } = "";
}
```

Ayrıntısı: [arama](../guides/querying.md#arama).

## HALDUS006

**Searchable needs at least one field.** `Searchable()` alan verilmeden çağrılmış.

Varsayılan olarak hiçbir alan aranamaz; aranabilecek ve sıralanabilecek alanlar tek tek sayılır. Alansız çağrı bir
şey açmazdı.

<!-- example: haldus006, error: HALDUS006 -->
```csharp
app.MapCrud<Product>()
    .Searchable();

public partial class Product
{
    public string Name { get; set; } = "";

    public decimal Price { get; set; }
}
```

<!-- example: haldus006-fixed -->
```csharp
app.MapCrud<Product>()
    .Searchable(product => product.Name, product => product.Price);

public partial class Product
{
    public string Name { get; set; } = "";

    public decimal Price { get; set; }
}
```

## HALDUS007

**Declarations cannot be combined.** İki bildirim birlikte kullanılamıyor ya da bir bildirim archetype'ın anlamıyla
çelişiyor. Haldus bunu sessizce yok saymaz.

Mesaj her zaman çakışan bildirimleri ve nedenini söyler; çoğu zaman ne yazılacağını da önerir. Çözüm ya birini
kaldırmak ya da mesajın önerdiği bildirimi kullanmaktır.

<!-- example: haldus007, error: HALDUS007 -->
```csharp
app.MapAppendOnly<StockMovement>()
    .ConcurrencyChecked();

public partial class StockMovement
{
    public int Quantity { get; set; }
}
```

Mesaj: `ConcurrencyChecked cannot be combined with MapAppendOnly`. Eklenen ve hiç değişmeyen kaydın korunacak bir
sürümü yoktur; bildirimi kaldırın:

<!-- example: haldus007-fixed -->
```csharp
app.MapAppendOnly<StockMovement>();

public partial class StockMovement
{
    public int Quantity { get; set; }
}
```

En sık görülen mesajlar:

| Mesaj | Ne yapılır |
|---|---|
| `ConcurrencyChecked cannot be combined with MapAppendOnly` (ve `SoftDeletable`, `Archivable` için benzerleri) | Archetype bu işleme sahip değil; bildirimi kaldırın. Hangi archetype'ın neyi aldığı: [archetype'lar](../guides/archetypes.md#birlikte-kullanım). |
| `Numbered cannot be combined with MapReadOnly` | Numara ve ömür oluştururken verilir; kaynağı `MapCrud` veya `MapAppendOnly` yapın. |
| `Filterable cannot be combined with MapLookup` | Lookup listesi sayfalanmaz ve süzülmez; `MapCrud` veya `MapStore` kullanın. |
| `MapLookup does not have the operation(s) 'Patch'; they cannot be selected with Without or Only` | `Without` / `Only` yalnızca archetype'ın sahip olduğu işlemleri seçer. |
| `Filterable requires the List operation, which has been removed from this resource` | Liste ucunu geri ekleyin ya da bildirimi kaldırın. |
| `Purgeable needs Filterable<TFilter>() for its criteria and Batch() for the rules of deleting each record` | İki bildirimi de ekleyin. |
| `Batch cannot be combined with ConcurrencyChecked while the resource can be updated or deleted: …` | Toplu öğe sürüm taşıyamaz; güncelleme ve silmeyi `Without` ile kaldırın ya da birini bırakın. |
| `OwnedByCurrentUser places the resource under /me, which cannot be combined with Under<T>(); pass underMe: false …` | `.OwnedByCurrentUser(underMe: false)` yazın. |
| `ReadTracking needs OwnedByCurrentUser: … use ReadReceipts for shared records` | Paylaşılan kayıtta `ReadReceipts()` kullanın. |
| `Rotatable rotates the secret of the resource; declare SecretOnce(prefix: ...) as well` | Gerekli bildirimi ekleyin. Aynı biçim `TokenOnly` → `TokenAccess`, `ScheduledPublishing` → `Publishable` için de geçerlidir. |
| `Publishable cannot be combined with Workflow: the published copy carries only the content of the draft` | Yayın kendi durum modelidir; ikisinden birini seçin. |
| `A resource can live under only one parent; Under is declared more than once` | Tek bir `Under<T>()` bırakın; öteki ilişkiler için `References<T>()` kullanın. |
| `A singleton under a parent is scoped by that parent; remove MultiTenant …` | Ebeveyn altındaki singleton kiracıyı ebeveyninden alır. |
| `KeyStrategy.Client cannot be combined with MultiTenant: a taken key would reveal a record the caller cannot see; …` | Anahtarı Haldus'a bırakın, tekrara karşı `Idempotent()` kullanın. |
| `{Bildirim} cannot be combined with TokenOnly: the record would be reachable without its token` | Yalnızca token ile ulaşılan kayıtta kimlikli uç üreten bildirimler kullanılamaz. |

Her bileşim kuralı, ilgili özelliğin rehberindeki "Birlikte kullanım" bölümündedir.

## HALDUS008

**Query request needs a parameterless constructor.** `MapQuery` çağrısının istek tipi sorgu dizesinden
doldurulamıyor.

`MapQuery` bir `GET` ucudur; istek tipinin herkese açık, yazılabilir skaler özellikleri tek tek sorgu
parametresine bağlanır. Bunun için tipin herkese açık parametresiz bir kurucusu ve yazılabilir özellikleri olmalıdır.
Konumsal bir `record` bu koşulu karşılamaz.

<!-- example: haldus008, error: HALDUS008 -->
```csharp
app.MapQuery<ShippingQuoteRequest, ShippingQuote>((request, _) =>
    Task.FromResult(new ShippingQuote(12.5m)));

public sealed record ShippingQuoteRequest(string Country);

public sealed record ShippingQuote(decimal Price);
```

İsteği yazılabilir özellikleri olan bir sınıf (veya özellikli bir `record`) yapın:

<!-- example: haldus008-fixed -->
```csharp
app.MapQuery<ShippingQuoteRequest, ShippingQuote>((request, _) =>
    Task.FromResult(new ShippingQuote(12.5m)));

public sealed class ShippingQuoteRequest
{
    [MaxLength(2)]
    public string Country { get; set; } = "";
}

public sealed record ShippingQuote(decimal Price);
```

Yanıt tipi için böyle bir koşul yoktur. Gövdeden okunan `MapAction` isteği de konumsal bir `record` olabilir.

## HALDUS009

**Reference target cannot be resolved.** Bir ilişkinin hedefi `Map*` ile eşlenmiş bir kaynak değil.

Ebeveyn (`Under<T>()`), alt tip (`SubtypeOf<T>()`), türetme (`DerivedFrom<T>()`), toplanan alan (`Counts<T>()`,
`Sums<T>()`) ve detayın sahibi gibi bildirimler hedefin adresini ve modelini bilmek zorundadır; hedef aynı projede bir
`Map*` çağrısıyla eşlenmiş olmalıdır. `References<T>()` başka bir modülün derlenmiş entity'sini de gösterebilir; onun
için tanı yalnızca hedef hiç bilinmeyen bir tipse çıkar.

<!-- example: haldus009, error: HALDUS009 -->
```csharp
app.MapCrud<Address>()
    .Under<Customer>();

public partial class Customer
{
    public string Name { get; set; } = "";
}

public partial class Address
{
    public string City { get; set; } = "";
}
```

Hedefi de eşleyin:

<!-- example: haldus009-fixed -->
```csharp
app.MapCrud<Customer>();
app.MapCrud<Address>()
    .Under<Customer>();

public partial class Customer
{
    public string Name { get; set; } = "";
}

public partial class Address
{
    public string City { get; set; } = "";
}
```

## HALDUS010

**Declaration cannot be read at compile time or is invalid.** Bir bildirimin argümanı derleme zamanında okunamıyor ya
da okunduğu halde geçersiz.

Generator argümanları kaynak koddan okur. Bu yüzden argümanlar derleme zamanında bilinen değerler olmalıdır: enum
üyeleri, metin sabitleri, sabit argümanlı `TimeSpan.FromMinutes(15)` gibi ifadeler ve `item => item.Property`
biçiminde lambda'lar. Değişken, yapılandırmadan okunan değer veya bir metodun sonucu okunamaz.

<!-- example: haldus010, error: HALDUS010 -->
```csharp
var lifetime = TimeSpan.FromMinutes(builder.Configuration.GetValue<int>("Coupons:LifetimeMinutes"));

app.MapCrud<Coupon>()
    .Expiring(lifetime);

public partial class Coupon
{
    public string Code { get; set; } = "";
}
```

Mesaj: `The lifetime of Expiring must be readable at compile time; write it as TimeSpan.FromMinutes(15),
TimeSpan.FromHours(1) or TimeSpan.FromDays(7) with constant arguments`. Değeri sabit olarak yazın:

<!-- example: haldus010-fixed -->
```csharp
app.MapCrud<Coupon>()
    .Expiring(TimeSpan.FromMinutes(15));

public partial class Coupon
{
    public string Code { get; set; } = "";
}
```

En sık görülen mesajlar:

| Mesaj | Ne yapılır |
|---|---|
| `The states of a transition must be written as enum members, for example from: OrderStatus.Draft, to: OrderStatus.Placed` | Durumları değişkenden değil doğrudan enum üyesi olarak yazın. |
| `'…' is not a state of 'Order'; transitions use a status type declared with Workflow<TStatus>() or Axis<TStatus>()` | Geçişin durumları kaynağın `Workflow` veya `Axis` ile bildirilen durum tipinden olmalıdır. |
| `The name of a transition must be a constant; it becomes part of the generated route` | Geçiş veya aksiyon adını metin sabiti olarak yazın. |
| `'MarkPaid' is not a valid transition name; use lowercase words separated by '-', for example "mark-paid"` | Adı küçük harfli, tireyle ayrılmış yazın. |
| `The initial state must be written as an enum member, for example Workflow<OrderStatus>(initial: OrderStatus.Draft)` | `initial:` argümanını enum üyesi olarak yazın. |
| `The number pattern 'ORD-{yyyy}' must contain {seq} exactly once` | Numara deseninde tam bir `{seq}` veya `{seq:N}` olmalıdır; desteklenen parçalar `{seq}`, `{seq:N}`, `{yyyy}`, `{yy}`, `{MM}`, `{dd}`. |
| `The resource has a state machine, so Reversible must list the states in which a record may be reversed, for example Reversible(Status.Posted)` | Durum makinesi olan kaynakta `Reversible`, `Supersedes` ve `ChangeSets` durumları sayar. |
| `The maxItems of Batch must be between 1 and 10000` | `Batch(maxItems: ...)` değerini aralığa çekin. |
| `KeyStrategy.Prefixed needs a prefix written as a string literal: …` | Öneki metin sabiti olarak verin: `.KeyStrategy(KeyStrategy.Prefixed, "ord")`. |

`Sortable`, `CreateOnly` ve `NaturalKey` içinde geçersiz bir alan da `HALDUS010` verir (`Searchable` için
[`HALDUS005`](#haldus005)).

## Ayrıca bakın

- [docs/guide.md](../guide.md): tanıların İngilizce başvurusu (yardım bağlantılarının gittiği yer)
- [Bildirim dizini](declarations.md)
- [Archetype'lar](../guides/archetypes.md#birlikte-kullanım)
- [Sık sorulan sorular](../faq.md)
