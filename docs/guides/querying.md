# Listeleme, süzme, sıralama ve arama

Her liste ucu sayfalıdır. Bunun ötesinde Haldus bir kaynağın kayıtlarını kendiliğinden süzülebilir, sıralanabilir veya
aranabilir yapmaz: hangi alanın nasıl sorgulanacağını zincirde siz söylersiniz. Bu sayfa dört yolu anlatır: kendi filtre
sınıfınızla basit süzme (`Filterable`), liste ucunda sıralama (`Sortable`), VE/VEYA koşullu arama ucu (`Searchable`) ve
kaydı anahtarı yerine benzersiz bir alanıyla okumak (`NaturalKey`).

Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. `builder` ve `app` satırları yoksa `Program.cs`'in geri
kalanı yazılmış sayılır.

## İçindekiler

- [Sayfalama](#sayfalama)
- [Süzme](#süzme)
- [Sıralama](#sıralama)
- [Arama](#arama)
- [Doğal anahtar](#doğal-anahtar)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Sayfalama

Liste ucu (`GET /products`) her zaman bir sayfa döndürür. Sayfayı iki sorgu parametresi seçer:

| Parametre | Varsayılan | Kural |
|---|---|---|
| `page` | `1` | 1'den küçük veya verilmemişse 1 sayılır. |
| `pageSize` | `20` | 1'den küçük veya verilmemişse 20, 100'den büyükse 100 sayılır. |

Sınırın dışındaki değerler hata değildir; sessizce düzeltilir. Son sayfanın ötesi boş bir `items` listesi döndürür.

```http
GET /products?page=2&pageSize=2
```

```json
{
  "items": [
    {
      "id": "019a8f4e-5b2c-7d41-9a3e-2f6c1b8d0e71",
      "name": "Klavye",
      "price": 100,
      "description": null
    },
    {
      "id": "019a8f4e-6c3d-7e52-8b4f-3a7d2c9e1f82",
      "name": "Monitör",
      "price": 900,
      "description": "27 inç"
    }
  ],
  "page": 2,
  "pageSize": 2,
  "totalCount": 5
}
```

| Alan | Anlamı |
|---|---|
| `items` | Bu sayfadaki kayıtlar, kaynağın yanıt biçiminde. |
| `page`, `pageSize` | Uygulanan değerler (düzeltilmiş halleriyle). |
| `totalCount` | Süzgeçlerden geçen bütün kayıtların sayısı. |

Başka bir sıra istenmediyse liste anahtara göre artan sırayla döner. Varsayılan anahtar zamana göre sıralı bir GUID
olduğu için bu, kabaca oluşturulma sırasıdır. Sıralı kayıtlarda (`Orderable`) liste `position` alanına göre döner;
bkz. [sıralı kayıtlar](record-operations.md#sıralı-kayıtlar).

`MapLookup` listesi bu kuralın dışındadır: sayfalanmaz ve her zaman koda göre döner
([Archetype'lar](archetypes.md#maplookup)).

## Süzme

Haldus alan başına sorgu parametresi üretmez: `GET /products?name=klavye` hiçbir şeyi süzmez, tanınmayan sorgu
parametreleri yok sayılır. Bir filtre dili de (`?filter=price > 100` gibi) yoktur. Bu ikisi bilerek yapılmadı. Liste
ucunun sorgu parametrelerini kendi yazdığınız bir filtre sınıfı belirler.

### Filtre sınıfı

Filtre `IFilter<T>` arayüzünü uygular ve parametresiz bir kurucusu vardır. Herkese açık, yazılabilir (`set`) her skaler
özelliği liste ucunun isteğe bağlı bir sorgu parametresi olur; adı özelliğin camelCase halidir. `Apply` bu değerleri
sorguya çevirir. `WhereIf` bir koşulu yalnızca ilk argümanı doğruysa ekler.

<!-- example: filter -->
```csharp
app.MapCrud<Product>()
    .Filterable<ProductFilter>();

public partial class Product
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [Range(0.0, 1_000_000.0)]
    public decimal Price { get; set; }

    public string? Description { get; set; }
}

public sealed class ProductFilter : IFilter<Product>
{
    public string? Search { get; set; }

    public decimal? MinPrice { get; set; }

    public decimal? MaxPrice { get; set; }

    public bool OnlyDescribed { get; set; }

    public IQueryable<Product> Apply(IQueryable<Product> query) => query
        .WhereIf(Search is not null, product => product.Name.ToLower().Contains(Search!.ToLower()))
        .WhereIf(MinPrice is not null, product => product.Price >= MinPrice)
        .WhereIf(MaxPrice is not null, product => product.Price <= MaxPrice)
        .WhereIf(OnlyDescribed, product => product.Description != null);
}
```

Üretilen liste ucu dört parametreyi tanır:

| Parametre | Tipi | Filtredeki özellik |
|---|---|---|
| `search` | metin | `Search` |
| `minPrice`, `maxPrice` | sayı | `MinPrice`, `MaxPrice` |
| `onlyDescribed` | `true` / `false` | `OnlyDescribed` |

```http
GET /products?search=klav&minPrice=50&maxPrice=500
```

Yanıt, sayfalamadaki biçimde yalnızca koşullara uyan kayıtları taşır.

### Kurallar

- Bir özellik yalnızca sorguda parametresi varsa doldurulur. Gönderilmeyen parametrenin özelliği sınıftaki varsayılan
  değerini korur; yukarıda `onlyDescribed` gönderilmezse `false` kalır.
- Parametre olabilen tipler: `string`, `bool`, sayılar, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`,
  `TimeSpan`, `Guid`, enum'lar, bunların nullable halleri ve bunların dizileri (`int[]`). Dizi parametresi tekrar
  edilerek verilir: `?stocks=1&stocks=5`. Başka tipteki, salt okunur veya `init` erişimcili özellikler yok sayılır.
- Parametrenin değeri tipine çevrilemiyorsa (`?minPrice=abc`) istek 400 alır.
- `Apply` bir EF Core sorgusu kurar; yazdığınız ifade veritabanına çevrilebilmelidir. Büyük/küçük harf ayrımı gibi
  kararlar da sizindir (yukarıda `ToLower`).
- Filtre, kaynağın kendi kurallarından sonra uygulanır: kiracının, kullanıcının, silinmiş ve süresi dolmuş kayıtların
  gizlenmesi filtreden bağımsızdır. Filtre bir kaydı görünür yapamaz, yalnızca daraltır.
- Filtrenin `Sort` adlı bir özelliği varsa `Sortable` ile birlikte kullanılamaz (`HALDUS007`): ikisi de `sort`
  parametresini isterdi.
- `.Filterable<TFilter>()` doğrudan `Map*` çağrısının zincirinde yazılmalıdır; bir değişken üzerinden çağrılırsa
  generator onu göremez (`HALDUS004`).

### Kendiliğinden gelen süzgeçler

Bazı bildirimler liste ucuna kendi parametresini ekler. Bunlar filtre sınıfıyla birlikte kullanılabilir:

| Parametre | Bildirim | Etkisi |
|---|---|---|
| `?status=Open` | `Workflow<TStatus>()` | Duruma göre süzer. Filtrenizin `Status` adlı bir özelliği varsa onunki geçerlidir. [Durum makineleri](state-machines.md) |
| `?ticketBilling=Billed` | `Axis<TStatus>()` | Ek durum eksenine göre süzer; parametrenin adı eksenin alanıdır. [Ek durum eksenleri](state-machines.md#ek-durum-eksenleri) |
| `?includeDeleted=true` | `SoftDeletable()` | Silinmiş kayıtları da listeler. [Geri alınabilir silme](lifecycle.md#geri-alınabilir-silme) |
| `?includeArchived=true` | `Archivable()` | Arşivlenmiş kayıtları da listeler. [Arşivleme](lifecycle.md#arşivleme) |
| `?asOf=2026-06-01` | `EffectiveDated(...)` | O gün geçerli olan kayıtlar. [Geçerlilik dönemleri](lifecycle.md#geçerlilik-dönemleri) |
| `?parentId=`, `?roots=true` | `Hierarchical()` | Bir düğümün çocukları veya kökler. [Ağaç](relationships.md#ağaç) |
| `?supplierId=` | `Under<T>(shallow: true)` | Ebeveyne göre süzer. [Ebeveyn altında](relationships.md#ebeveyn-altında) |
| `?kind=car` | `SubtypeOf<T>()` | Üst tipin listesini türe göre süzer. [Alt tip](relationships.md#alt-tip) |
| `?unread=true` | `ReadTracking()`, `ReadReceipts()` | Okunmamış kayıtlar. [Okundu takibi](history-and-publishing.md#okundu-takibi) |
| `?locale=tr` | `Translatable(...)` | Süzmez; çevrilmiş alanları o dilde döndürür. [Çeviriler](fields.md#çeviriler) |
| `?sort=-price` | `Sortable(...)` | Sıralar. [Sıralama](#sıralama) |

## Sıralama

`.Sortable(...)` liste ucuna `sort` parametresini ekler. Yalnızca sayılan alanlara göre sıralanabilir.

<!-- example: sort -->
```csharp
app.MapCrud<Product>()
    .Auditable()
    .Sortable(product => product.Name, product => product.Price, product => product.CreatedAt);

public partial class Product
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public decimal Price { get; set; }
}
```

Değer, virgülle ayrılmış alan adlarıdır (camelCase). Başındaki eksi azalan sıradır:

```http
GET /products?sort=-price,name&page=1&pageSize=20
```

En pahalı ürünler başta, aynı fiyattakiler ada göre. Sıralama sayfalamadan önce uygulanır.

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| Sayılmamış bir alan: `?sort=stock` | 400, hata `sort` alanında. |
| Sayılan alan sayısından çok terim: üç alan sayıldıysa dördüncü terim | 400. |
| `sort` verilmedi | Varsayılan sıra (anahtar; sıralı kayıtlarda `position`). |

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "sort": ["Cannot sort by 'stock'. Sortable fields: name, price, createdAt."]
  }
}
```

Mesaj `Accept-Language` başlığının dilindedir (`en` veya `tr`).

- Sıralama her zaman anahtarla tamamlanır; aynı değeri taşıyan kayıtların sırası sayfadan sayfaya değişmez.
- Sayılabilen alanlar: anahtar, entity'nin skaler alanları, üretilen yabancı anahtarlar (`categoryId`) ve trait'lerin
  yanıtta görünen alanları (`createdAt`, `status`, `number`, toplanan alanlar). Kiracı (`TenantId`), sürüm ve yalnızca
  yazılan alanlar (`Sensitive`, `Encrypted`) sayılamaz: değerine göre sıralamak onu sızdırırdı.
- Sayılamayan bir alan yazılırsa (`p => p.Name.Length`, var olmayan bir alan) proje derlenmez (`HALDUS010`).

## Arama

Basit süzme sorgu parametreleriyle yapılır. "Adı `a` ile başlayan **ve** fiyatı 50 ile 900 arasında olan, **ya da**
açıklaması boş olan" gibi birleşik koşullar için `.Searchable(...)` bir arama ucu ekler: `POST /products/search`. Koşullar
gövdede, tipli bir JSON nesnesi olarak gelir.

<!-- example: search -->
```csharp
app.MapCrud<Product>()
    .Auditable()
    .Searchable(
        product => product.Name,
        product => product.Price,
        product => product.Description,
        product => product.CreatedAt);

public partial class Product
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    public decimal Price { get; set; }

    public string? Description { get; set; }
}
```

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `POST` | `/products/search` | Gövdedeki koşullara uyan kayıtları sayfalı olarak döndürür. Kayıt yazmaz. |

Varsayılan olarak hiçbir alan aranamaz; yalnızca `Searchable` içinde sayılanlar aranır.

### İstek gövdesi

```http
POST /products/search
Content-Type: application/json

{
  "where": {
    "name": { "startsWith": "Kla", "ignoreCase": true },
    "price": { "gte": 50, "lt": 900 },
    "or": [
      { "description": { "isNull": true } },
      { "description": { "contains": "kablosuz" } }
    ],
    "not": { "name": { "endsWith": "Pro" } }
  },
  "sort": [
    { "field": "price", "descending": true },
    { "field": "name" }
  ],
  "page": 1,
  "pageSize": 20
}
```

| Alan | Anlamı |
|---|---|
| `where` | Koşul nesnesi. Verilmezse bütün kayıtlar döner. |
| `where.{alan}` | Bir alanın koşulu (aşağıdaki operatörler). Bir koşulda birden çok operatör VE ile birleşir. |
| `where.and` | Koşul nesnelerinin listesi; hepsi doğru olmalı. |
| `where.or` | Koşul nesnelerinin listesi; en az biri doğru olmalı. |
| `where.not` | Tek bir koşul nesnesi; doğru olmamalı. |
| `sort` | `{ "field": "price", "descending": true }` öğelerinin listesi; `descending` verilmezse artan. Yalnızca sayılan alanlar. |
| `page`, `pageSize` | Liste ucundaki gibi (varsayılan 1 ve 20, en çok 100). |

Aynı nesnedeki alan koşulları ile `and`, `or`, `not` birbirine VE ile bağlanır. Yukarıdaki istek: adı büyük/küçük harf
ayrımı olmadan "Kla" ile başlayan, fiyatı 50 ile 900 arasında (900 hariç), açıklaması boş veya "kablosuz" içeren ve adı
"Pro" ile bitmeyen ürünler.

Yanıt liste ucundakiyle aynı biçimdedir (`items`, `page`, `pageSize`, `totalCount`).

### Operatörler

Bir alanın hangi operatörleri aldığı tipine bağlıdır:

| Alanın tipi | Operatörler |
|---|---|
| `string` | `eq`, `ne`, `contains`, `startsWith`, `endsWith`, `in`, `isNull`, `ignoreCase` |
| Sayılar, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan` | `eq`, `ne`, `gt`, `gte`, `lt`, `lte`, `in`, `isNull` |
| `bool`, `Guid`, enum'lar | `eq`, `ne`, `in`, `isNull` |

| Operatör | Anlamı |
|---|---|
| `eq`, `ne` | Eşittir, eşit değildir. |
| `gt`, `gte`, `lt`, `lte` | Büyüktür, büyük veya eşittir, küçüktür, küçük veya eşittir. |
| `contains`, `startsWith`, `endsWith` | Metin içerir, ile başlar, ile biter. `%` ve `_` joker değildir, harfiyen aranır. |
| `in` | Değer listedekilerden biridir. Boş liste hiçbir kayda uymaz. |
| `isNull` | `true`: alan boştur; `false`: alan doludur. Boş olamayan bir alanda `true` hiçbir kayda uymaz. |
| `ignoreCase` | `true` ise o koşuldaki metin karşılaştırmaları (`in` dahil) büyük/küçük harf ayrımı yapmaz. |

Enum alanının değeri, uygulamanızın JSON ayarına göre adıyla veya sayısıyla yazılır.

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| `where` veya `sort` içinde sayılmamış bir alan, bilinmeyen bir operatör (`"regex"`), gövdenin bilinmeyen bir alanı | 400 |
| `sort` öğesinde `field` yok | 400 |
| İç içe `and` / `or` / `not` 5 düzeyden derin | 400 |
| Toplam 50'den çok koşul | 400 |
| Bir `in` listesinde 100'den çok değer | 400 |
| `sort` içinde 8'den çok öğe | 400 |

Bilinmeyen bir alan veya operatör sessizce yok sayılmaz: yanlış yazılmış bir koşul süzülmemiş sonuç döndürürdü. Son
dört sınırın yanıtı şudur:

```json
{
  "title": "Search is too complex",
  "status": 400,
  "detail": "A search may nest at most 5 levels and contain at most 50 conditions, 100 values per list and 8 sort fields."
}
```

- Arama, liste ucunun gördüğü kayıtları görür: kiracı, kullanıcı, ebeveyn, silinmiş ve süresi dolmuş kayıt kuralları
  aynen geçerlidir. Değerler SQL parametresi olarak gider.
- Kaynak `SoftDeletable` veya `Archivable` ise gövde `"includeDeleted": true` ve `"includeArchived": true` da alır.
- Arama ucu filtre sınıfını (`Filterable`) ve liste ucunun kendiliğinden gelen süzgeçlerini (`?status=`, `?asOf=`...)
  uygulamaz; koşulları yalnızca gövdeden alır. `?locale=` de uygulanmaz: yanıt kaynak dildedir.
- `POST` olduğu halde kayıt yazmaz. Uygulamanın yetki kuralları için uç `HaldusReadOnly` metadatasıyla işaretlidir.

### Aranabilen alanlar

`Searchable` içinde şunlar sayılabilir: anahtar (`Id`), entity'nin skaler alanları, üretilen yabancı anahtarlar ve
trait'lerin yanıtta görünen alanları (`CreatedAt`, `UpdatedBy`, `Status`...). Şunlar sayılamaz ve proje derlenmez
(`HALDUS005`): kiracı (`TenantId`), sürüm, koleksiyonlar, gömülü değer nesneleri, yalnızca yazılan alanlar, alanın
kendisi olmayan ifadeler (`p => p.Name.Length`). `Searchable()` alansız yazılırsa `HALDUS006` olur.

## Doğal anahtar

Bazı kayıtlar anahtarlarından çok başka bir alanlarıyla tanınır: kupon kodu, makalenin adresteki adı, ülke kodu.
`.NaturalKey(...)` o alanı benzersiz yapar ve kaydı o değerle okuyan bir uç ekler. Öteki uçlar `Id` ile kalır; doğal
anahtar adreste `Id`'nin yerine geçmez.

<!-- example: natural-key -->
```csharp
app.MapCrud<Coupon>()
    .NaturalKey(coupon => coupon.Code);

public partial class Coupon
{
    [Required]
    [MaxLength(50)]
    public string Code { get; set; } = "";

    [Range(1, 90)]
    public int Percent { get; set; }
}
```

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/coupons/by-code/{code}` | Kaydı doğal anahtarıyla okur; yoksa 404. |

Adresin son parçası alanın kebab-case adıdır: `Code` → `by-code`, `Slug` → `by-slug`, `PersonId` → `by-person-id`.

```http
GET /coupons/by-code/SPRING-25
```

```json
{
  "id": "019a8f52-1d4e-7a63-8c5f-4b8e3d0f2a93",
  "code": "SPRING-25",
  "percent": 25
}
```

### Kurallar ve hata yanıtları

Aynı değeri taşıyan ikinci kayıt oluşturulamaz; bir kaydın değeri başka bir kaydınkine değiştirilemez:

```http
POST /coupons
Content-Type: application/json

{ "code": "SPRING-25", "percent": 30 }
```

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Already exists",
  "status": 409,
  "detail": "Another Coupon already has the code 'SPRING-25'."
}
```

- Benzersizlik kaydın kapsamı içindedir: kiracı (`MultiTenant`), sahip (`OwnedByCurrentUser`) ve ebeveyn (`Under<T>()`).
  Kullanıcıya ait alışveriş listelerinde iki kullanıcı aynı adı kullanabilir; her biri `by-name` ile kendi kaydını okur.
- Kontrol oluşturmada, `PUT`'ta ve `PATCH`'te yapılır. Kayıt kendi değeriyle çakışmaz.
- Değeri görünmeyen kayıtlar da tutar: silinmiş (`SoftDeletable`) veya süresi dolmuş (`Expiring`) bir kuponun kodu
  yeniden kullanılamaz, ama `by-code` ile de okunamaz (404).
- Benzersizliği veritabanındaki benzersiz bir indeks sağlar (`UX_{Tablo}_{Alan}`). Aynı değerle eşzamanlı gelen
  isteklerden biri oluşturur, ötekiler 409 alır; hiçbiri 500 almaz.
- Alan, boş olamayan bir `string`, `int`, `long` veya `Guid` olmalıdır. Nullable, gömülü veya yalnızca yazılan bir alan
  doğal anahtar olamaz; tek bir alan sayılır (`HALDUS010`).

Doğal anahtarın sonradan değişmemesi gerekiyorsa onu [yalnızca oluştururken yazılan](fields.md#yalnızca-oluştururken-yazılan-alanlar)
bir alan yapın: `.NaturalKey(c => c.Code).CreateOnly(c => c.Code)`.

## Birlikte kullanım

- `Filterable`, `Sortable` ve `Searchable` birlikte yazılabilir; liste ucu filtreyi ve sıralamayı, arama ucu yalnızca
  gövdesini kullanır.
- Üçü de liste işlemi isteyen bildirimlerdir: `MapSingleton` ve `MapLookup` ile, ya da liste işlemi `Without` ile
  kaldırılmış bir kaynakta yazılamaz (`HALDUS007`).
- `NaturalKey`; `MapCrud`, `MapReadOnly`, `MapAppendOnly` ve ayrı uçlu detayla birleşir. Anahtarı zaten istemcinin
  verdiği `MapStore`, `MapLookup` ve `MapBalance` ile birleşmez (`HALDUS007`). Takılabilir kaynaklarda (`AttachedTo`)
  yazılamaz. `RoleOf<T>()` oyuncunun kimliğini kendiliğinden doğal anahtar yapar; o kaynak ikinci bir `NaturalKey`
  bildiremez.
- `Purgeable` ölçütünü filtre sınıfından alır ve `Filterable` ister; bkz.
  [ölçüte göre silme](record-operations.md#ölçüte-göre-silme).

## Sınırlar

- Alan başına kendiliğinden üretilen sorgu parametreleri (`?priceMin=100`) ve tek bir `filter` ifadesi (AIP-160
  tarzı) yoktur. Bu, kullanıcının kararıdır; süzme yalnızca `Filterable` ve `Searchable` ile yapılır.
- Çevirilere ve özel alanların (`customFields`) değerlerine göre süzülemez, sıralanamaz ve aranamaz. Metadata çantası
  da aranabilen ve sıralanabilen alanlar arasında değildir.
- Sayfalama sayfa numarasıyladır; imleçli (keyset) sayfalama yoktur. Değişiklikleri izlemek için
  [değişiklik akışı](history-and-publishing.md#değişiklik-akışı) vardır.
- Arama ucunda tam metin arama ve benzerlik sıralaması yoktur; `contains` bir alt metin karşılaştırmasıdır.

## Ayrıca bakın

- [Ortak kurallar](../concepts/conventions.md): adresler, liste yanıtı, hata biçimi.
- [Alanlar ve anahtarlar](fields.md): `CreateOnly`, anahtar biçimi, özel alanlar.
- [Kaydın yaşam döngüsü](lifecycle.md): silinmiş, arşivlenmiş ve süresi dolmuş kayıtların listede görünmesi.
- [HTTP sözleşmesi](../reference/http.md): bütün durum kodları.
- [Tanılar](../reference/diagnostics.md): `HALDUS004`, `HALDUS005`, `HALDUS006`, `HALDUS007`, `HALDUS010`.
