# HTTP sözleşmesi

Haldus'un ürettiği bütün uçlar aynı HTTP sözleşmesine uyar: aynı durum kodları, aynı hata gövdesi, aynı başlıklar ve
aynı JSON biçimi. Bu sayfa o sözleşmenin başvurusudur. Bir özelliğe özgü uçlar (geçişler, toplu işlemler, dosyalar)
kendi rehberlerinde anlatılır; burada yalnızca hepsinde ortak olan kurallar vardır.

## İçindekiler

- [Durum kodları](#durum-kodları)
- [Hata gövdesi](#hata-gövdesi)
- [Başlıklar](#başlıklar)
- [Sayfalama](#sayfalama)
- [JSON biçimi](#json-biçimi)
- [`PUT` ve `PATCH`](#put-ve-patch)
- [Ayrıca bakın](#ayrıca-bakın)

## Durum kodları

### Başarı

| Kod | Ne zaman |
|---|---|
| 200 OK | Okuma (`GET`), güncelleme (`PUT`, `PATCH`), geçiş ve entity aksiyonu (yanıt kaydın güncel hali; kendi sonucunu döndüren aksiyonda o sonuç), `MapStore` / `MapLookup` için var olan anahtara `PUT`, `MapSingleton` yazmaları, yanıt döndüren `MapAction` ve `MapQuery`. |
| 201 Created | Kayıt oluşturuldu: `POST` ile oluşturma ve `MapStore` / `MapLookup` için yeni bir anahtara `PUT`. Yanıt kaydı taşır; `Location` başlığı onun adresini gösterir. |
| 202 Accepted | `MapJob` işi oluşturuldu (veya yeniden çalıştırıldı) ve arka planda çalışacak. Yanıt işin kaydını taşır; `Location` başlığı işin adresini gösterir. |
| 204 No Content | Silme; yanıt döndürmeyen `MapAction`. |
| 207 Multi-Status | `.Batch(atomic: false)` ile kısmi toplu işlem. Gövde öğe başına bir sonuç taşır; bkz. [toplu işlemler](../guides/record-operations.md#toplu-işlemler). |

### Hata

| Kod | Ne zaman |
|---|---|
| 400 Bad Request | İstek kurallara uymuyor: alan doğrulaması (`errors` ile), var olmayan veya görünmeyen bir kayda referans (alan düzeyinde), bu kayda ait olmayan satır kimliği, ağaçta döngü, sıralanamayan alan (`?sort=`), çok büyük veya çok karmaşık toplu istek ya da arama, zorunlu olduğu halde gönderilmeyen veya 255 karakterden uzun `Idempotency-Key`, boş dosya içeriği. Okunamayan JSON (bozuk gövde, tipe uymayan değer) da ASP.NET Core tarafından 400 ile reddedilir. |
| 401 Unauthorized | Kullanıcıya ait bir kaynağa (`OwnedByCurrentUser`, `/me` altındaki uçlar) kullanıcısı çözülemeyen bir istek geldi; onay veya ret kararı veren bir kullanıcı yok. Uygulamanın kendi kimlik doğrulaması da 401 döndürebilir. |
| 403 Forbidden | Kiracıya ait bir kaynağa (`MultiTenant`) kiracısı çözülemeyen bir istek geldi; onay kararı verilemiyor (içeriği yazan kişi, gereken rolde olmayan kişi, aynı kişinin ikinci düzeyi). Uygulamanın yetkilendirme kuralları da 403 döndürebilir. |
| 404 Not Found | Kayıt yok ya da isteği yapan onu göremiyor (başka bir kiracının, başka bir kullanıcının, silinmiş veya süresi dolmuş kayıt). Adresteki anahtar tipe uymuyorsa (`/products/abc`) hiçbir uç eşleşmez ve yine 404 döner. Geçersiz veya eksik `X-Access-Token`, geçersiz dosya bağlantısı ve henüz yüklenmemiş dosya içeriği de 404'tür. |
| 405 Method Not Allowed | Kaynakta bu işlem üretilmedi: `MapAppendOnly`'ye `PUT`, `MapSingleton`'a `DELETE`, `.Without(CrudOperation.Delete)` ile kaldırılmış silme. |
| 409 Conflict | Kaydın şimdiki hali isteğe izin vermiyor: başka kayıtların referans verdiği kaydı silmek, benzersiz bir değerin (doğal anahtar, istemcinin verdiği kimlik) zaten alınmış olması, durum makinesinde yanlış durumda geçiş, güncelleme veya silme, istek işlenirken durumun başka bir istekle değişmesi, ters çevrilmiş veya yerine geçilmiş kaydı değiştirmek, onaylanmış kaydı güncellemek, yetersiz bakiye, çakışan geçerlilik dönemi, kapanmış değişiklik kümesi. |
| 412 Precondition Failed | `If-Match` başlığındaki sürüm kaydın şimdiki sürümüyle uyuşmuyor ya da başlık okunamıyor. Kaydı yeniden okuyun ve tekrar deneyin. |
| 413 Payload Too Large | Yüklenen dosya içeriği kaynağın kabul ettiği boyuttan büyük (`MapFile`). |
| 415 Unsupported Media Type | Yüklenen dosya içeriğinin türü kaynağın kabul ettiği türlerden değil (`MapFile`). |
| 422 Unprocessable Entity | İş mantığı isteği reddetti (`TransitionRejectedException`; mesaj onu fırlatan koddan gelir); aynı `Idempotency-Key` daha önce farklı bir gövdeyle kullanıldı. |
| 428 Precondition Required | Sürüm kontrollü (`ConcurrencyChecked`) bir kaynakta durum değiştiren bir istek `If-Match` başlığı olmadan geldi. |

Kendi yazdığınız uçlar ve ASP.NET Core'un kendisi (kimlik doğrulama, yetkilendirme, hız sınırı) başka kodlar da
döndürebilir; bu tablo yalnızca Haldus'un ürettiği uçları anlatır.

## Hata gövdesi

Bütün hatalar [problem details](https://www.rfc-editor.org/rfc/rfc9457) biçimindedir
(`Content-Type: application/problem+json`). `title` ve `detail` isteğin dilindedir; bkz.
[`Accept-Language`](#accept-language).

```http
HTTP/1.1 404 Not Found
Content-Type: application/problem+json

{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Resource not found",
  "status": 404,
  "detail": "Product with identifier '01937c6e-5b0a-7a3c-9f51-2d4c8e0b6a11' was not found."
}
```

Alan düzeyindeki hatalar `errors` nesnesinde, alanın JSON adıyla (camelCase) taşınır. Her alan bir mesaj dizisidir:

```http
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "name": ["The Name field must be at most 200 characters long."],
    "price": ["The Price field must be between 0 and 1000000."]
  }
}
```

İç içe alanların adı yolunu söyler:

| Alan | Örnek anahtar |
|---|---|
| Gömülü değer nesnesinin alanı | `price.currency` |
| Gömülü satırın alanı | `orderItems[2].quantity` |
| Atomik toplu istekte bir öğenin alanı | `items[2].name` |
| Özel alan | `customFields.color` |
| Sıralama, metadata, dosya içeriği | `sort`, `metadata`, `content` |

Referans verilen kaydın bulunamaması da aynı biçimdedir: `{ "errors": { "categoryId": ["Category '7' was not found."] } }`.

Haldus problem details'e kendi ek alanını (bir hata kodu gibi) eklemez; hatayı durum kodundan ve, alan hatalarında,
`errors` anahtarından tanıyın. `title` ve `detail` metni dile göre değiştiği için kodda onlarla karşılaştırma
yapmayın. `type` gibi standart alanları ASP.NET Core'un problem details servisi doldurur.

Sık görülen başlıklar (İngilizce):

| Kod | `title` |
|---|---|
| 400 | `One or more validation errors occurred.` |
| 404 | `Resource not found` |
| 409 (silme) | `Resource is in use` |
| 409 (benzersiz değer) | `Already exists` |
| 409 (durum) | `Not allowed in the current state` |
| 412 | `Resource has changed` |
| 422 | `The operation was rejected` |
| 428 | `Version required` |

## Başlıklar

| Başlık | Yön | Ne için |
|---|---|---|
| `Location` | Yanıt | 201 ve 202 yanıtlarında yeni kaydın adresi: `/products/01937c6e-…`. |
| `ETag` | Yanıt | Sürüm kontrollü kaynakta kaydın sürümü. |
| `If-Match` | İstek | Sürüm kontrollü kaynakta, yazmanın dayandığı sürüm. |
| `Idempotency-Key` | İstek | Tekrara karşı korunan uçlarda isteğin anahtarı. |
| `Idempotent-Replayed` | Yanıt | Yanıt, daha önce saklanmış bir yanıtsa `true`. |
| `X-Access-Token` | İstek | Token ile erişilen uçlarda kaydı tanıtan token. |
| `Accept-Language` | İstek | Mesajların dili. |

### `Location`

Oluşturma uçları (201) ve işin oluşturulması (202) yeni kaydın adresini `Location` başlığında verir. Adres, isteğin
adresinden kurulur ve uygulamanın `PathBase`'ini içerir: `POST /products` → `Location: /products/{id}`,
`PUT /feature-flags/new-checkout` → `Location: /feature-flags/new-checkout`.

### `ETag` ve `If-Match`

`.ConcurrencyChecked()` bildirilmiş bir kaynakta kaydın sürümü gövdede değil `ETag` başlığında taşınır
(`ETag: "4127"`). Tekil okuma, oluşturma ve yazma yanıtları başlığı içerir; liste yanıtları içermez. Durum değiştiren
her istek (`PUT`, `PATCH`, `DELETE`, geçişler, aksiyonlar ve `restore`, `archive` gibi trait uçları) okuduğunuz sürümü
`If-Match` ile gönderir:

```http
PATCH /customers/01937c6e-5b0a-7a3c-9f51-2d4c8e0b6a11
If-Match: "4127"
Content-Type: application/json

{ "loyaltyPoints": 12 }
```

| `If-Match` | Sonuç |
|---|---|
| Yok | 428 |
| Okunamıyor | 412 |
| Eski bir sürüm | 412 |
| Şimdiki sürüm | İstek işlenir; yanıt yeni `ETag`'i taşır |
| `*` | Kayıt varsa sürüme bakılmadan işlenir |

Sürüm kontrolü olmayan kaynaklar `If-Match` başlığını okumaz. Ayrıntısı:
[eşzamanlılık denetimi](../guides/lifecycle.md#eşzamanlılık-denetimi).

### `Idempotency-Key`

`.Idempotent()` bildirilmiş bir kaynağın oluşturma ve geçiş uçları (bakiyede komutlar) `Idempotency-Key` başlığını
okur. Aynı anahtarla
gelen ikinci istek işlemi yeniden çalıştırmaz; saklanan yanıtı (durum kodu, gövde, `Location` ve `ETag` ile)
`Idempotent-Replayed: true` başlığıyla döndürür.

- Anahtar 1–255 karakterdir. `.Idempotent(required: true)` ile başlık zorunludur; gönderilmezse 400 döner.
- Anahtarın kapsamı kiracı, kullanıcı, yöntem ve adrestir: başka bir kullanıcının aynı anahtarı ayrı bir anahtardır.
- Aynı anahtar farklı bir gövdeyle gelirse 422 döner.
- Yalnızca başarılı (2xx) yanıtlar saklanır. Başarısız istek anahtarla birlikte geri alınır; düzeltip aynı anahtarla
  yeniden deneyebilirsiniz.
- Aynı anahtarla aynı anda gelen ikinci istek, ilki bitene kadar bekler.
- Yanıtlar `HaldusOptions.IdempotencyRetention` (varsayılan 24 saat) boyunca saklanır; sonra aynı anahtar yeni bir
  istek sayılır.

Ayrıntısı: [tekrara karşı koruma](../guides/lifecycle.md#tekrara-karşı-koruma).

### `X-Access-Token`

`.TokenAccess(...)` bildirilmiş kayıtlar oturumu olmayan biri tarafından gizli bir token ile okunur ve sayılan
işlemleri çalıştırır (`GET /invitations/by-token`, `POST /invitations/by-token/accept`). Token adreste değil
`X-Access-Token` başlığında taşınır. Eksik ve geçersiz token aynı yanıtı alır (404); yanıt, bir token'ın var olup
olmadığını ele vermez. Ayrıntısı: [token ile erişim](../guides/secrets.md#token-ile-erişim).

### `Accept-Language`

Haldus'un döndürdüğü metinler (hata başlıkları, açıklamalar, doğrulama mesajları) isteğin dilindedir. Desteklenen
diller İngilizce (`en`, varsayılan) ve Türkçedir (`tr`). Dil `app.UseHaldus()` ile çözülür; o çağrı ASP.NET Core'un
istek yerelleştirmesini (`UseRequestLocalization`) açar.

```http
GET /products/01937c6e-5b0a-7a3c-9f51-2d4c8e0b6a11
Accept-Language: tr
```

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Kayıt bulunamadı",
  "status": 404,
  "detail": "'01937c6e-5b0a-7a3c-9f51-2d4c8e0b6a11' kimlikli Product kaydı bulunamadı."
}
```

Desteklenmeyen bir dil istenirse İngilizce döner. ASP.NET Core'un varsayılan sağlayıcıları dili sorgu dizesinden
(`?culture=tr`) ve yerelleştirme çerezinden de okur. `Accept-Language` verinin kendisini değiştirmez: çevrilebilir
alanlar ancak `?locale=` ile çevrilir. Metinleri değiştirmek veya dil eklemek için bkz.
[yapılandırma](configuration.md#mesajlar-ve-dil).

## Sayfalama

Liste uçları (`GET /products`) sayfalanır:

| Parametre | Varsayılan | Sınır |
|---|---|---|
| `page` | 1 | 1'den küçük değer 1 sayılır |
| `pageSize` | 20 | En çok 100; 1'den küçük değer varsayılan sayılır |

Sınırın dışındaki değerler hata değildir, sınıra çekilir. Yanıt bir zarftır:

```json
{
  "items": [
    { "id": "01937c6e-5b0a-7a3c-9f51-2d4c8e0b6a11", "name": "Keyboard", "price": 1250.50 }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1
}
```

`totalCount` süzmeden sonraki toplam kayıt sayısıdır. Liste, başka bir sıra istenmedikçe anahtara göre sıralıdır ve
sıralama her zaman anahtarla tamamlanır; böylece sayfalar kararlıdır. Sıralama (`?sort=-createdAt,name`) ve süzme
için bkz. [sıralama](../guides/querying.md#sıralama) ve [süzme](../guides/querying.md#süzme).

`MapLookup` listesi sayfalanmaz; zarf yerine düz bir dizi döndürür.

## JSON biçimi

| Konu | Kural |
|---|---|
| Alan adları | camelCase: `LoyaltyPoints` → `loyaltyPoints`. |
| Adresler | Kebab-case çoğul: `StockMovement` → `/stock-movements`. Geçiş ve aksiyon adları da kebab-case'dir: `/orders/{id}/mark-paid`. |
| Boş değerler | Yanıtta `null` olarak yazılır; alan atlanmaz (uygulamanın JSON ayarı bunu değiştirmediyse). İstisna, yalnızca oluşturma yanıtında görünen sır ve token alanlarıdır. |
| Zaman | ISO 8601, `DateTimeOffset`: `2026-11-25T09:14:03.512345+00:00`. Haldus'un yazdığı zamanlar UTC'dir ve mikrosaniyeye kırpılır (PostgreSQL daha hassasını saklamaz). |
| Kimlik | `Guid` metin olarak (`"01937c6e-…"`), `int` / `long` sayı olarak. |
| Haldus'un durum alanları | Adıyla yazılır: `"status": "Placed"`. Bu, `Workflow` ve `Axis` durumları, işin durumu, onay durumu, dosyanın durumu ve tekrar sıklığı için uygulamanın JSON ayarından bağımsız olarak geçerlidir. |
| Sizin enum alanlarınız | Uygulamanın JSON ayarını izler; ASP.NET Core'un varsayılanı sayıdır. Adıyla yazılmasını istiyorsanız enum tipine `[JsonConverter(typeof(JsonStringEnumConverter<T>))]` yazın veya `ConfigureHttpJsonOptions` ile genel bir dönüştürücü ekleyin. |
| Bilinmeyen alanlar | İstekte bulunan ama istek tipinde olmayan alanlar yok sayılır (ASP.NET Core'un varsayılanı). Yalnızca yanıtta yer alan bir alanı (`id`, `status`, `createdAt`) göndermek onu değiştirmez. |

## `PUT` ve `PATCH`

`MapCrud` kaynağında ikisi de üretilir ve farklı şeyler yapar:

| | `PUT` | `PATCH` |
|---|---|---|
| Anlamı | Yazılabilir alanların tamamını değiştirir. | Yalnızca gövdede bulunan alanları değiştirir. |
| Gönderilmeyen alan | Varsayılan değerini alır (boş bırakılabilir alan `null`, sayı `0`); `null` olamayan metin alanı ve `[Required]` alan 400 alır. | Değişmez. |
| `null` gönderilen boş bırakılabilir alan | Boşaltılır. | Boşaltılır. |
| `null` gönderilen, `null` olamayan alan | 400 | 400 (`The Name field is required.`) |
| Doğrulama | Bütün alanlar doğrulanır. | Yalnızca gönderilen alanlar doğrulanır. |

`PATCH` "gönderilmedi" ile "`null` gönderildi" ayrımını yapar. Şemada bunun için bir sarmalayıcı tip yoktur; istek tipi
sıradan, boş bırakılabilir alanlar taşır.

```http
PATCH /customers/01937c6e-5b0a-7a3c-9f51-2d4c8e0b6a11
If-Match: "4127"
Content-Type: application/json

{ "email": null }
```

Bu istek `email` alanını boşaltır; `name` ve öteki alanlara dokunmaz. `{ "name": null }` ise `name` zorunlu olduğu için
400 alır.

Bazı alanların `PATCH` kuralı farklıdır:

| Alan | `PATCH` davranışı |
|---|---|
| Gömülü satırlar (`orderItems`) | Gönderildiyse liste bütün olarak eşitlenir; `null` gönderilemez (400). |
| `metadata` | Birleştirilir: gönderilen anahtar eklenir veya değişir, `null` gönderilen anahtar silinir. |
| Gömülü değer nesnesi (`price`) | Bütün olarak değişir; `null` gönderilemez. |
| Yalnızca oluştururken yazılan alanlar | `PUT` ve `PATCH` isteklerinde yer almaz. |

Ayrıntıları: [detaylar](../guides/details-and-computed-fields.md), [metadata](../guides/fields.md#metadata),
[yalnızca oluştururken yazılan alanlar](../guides/fields.md#yalnızca-oluştururken-yazılan-alanlar).

## Ayrıca bakın

- [Archetype'lar](../guides/archetypes.md): her archetype'ın uçları
- [Kurallar ve adlandırma](../concepts/conventions.md)
- [Yapılandırma](configuration.md)
- [Tanılar](diagnostics.md)
