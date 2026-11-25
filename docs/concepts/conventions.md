# Ortak kurallar

Bu sayfa her kaynakta aynı olan kuralları toplar: adresler, anahtarlar, istek ve yanıt tipleri, listeler, güncelleme
biçimleri, doğrulama, hata yanıtları ve dil. Bir rehber sayfası bunlardan ayrılan bir şey söylemiyorsa burada yazan
geçerlidir.

## İçindekiler

- [Adresler](#adresler)
- [Anahtarlar](#anahtarlar)
- [Yanıt ve istek tipleri](#yanıt-ve-istek-tipleri)
- [Listeler](#listeler)
- [Güncelleme: `PUT` ve `PATCH`](#güncelleme-put-ve-patch)
- [Doğrulama](#doğrulama)
- [Hata yanıtları](#hata-yanıtları)
- [Dil](#dil)
- [JSON](#json)

## Adresler

Bir kaynağın adresi entity adının kebab-case çoğuludur:

| Entity | Adres |
|---|---|
| `Product` | `/products` |
| `OrderItem` | `/order-items` |
| `Category` | `/categories` |
| `Address` | `/addresses` |
| `StockMovement` | `/stock-movements` |

Çoğul basit İngilizce kurallarıyla kurulur: ünsüzden sonra gelen `y` → `ies`; `s`, `x`, `z`, `ch`, `sh` → `es`;
gerisi `s`. Düzensiz çoğullar tanınmaz (`Person` → `/persons`). Adresi değiştiren bildirimler ilgili sayfalarda
anlatılır: ebeveyn altındaki kaynaklar (`/customers/{customerId}/addresses`), kullanıcıya ait kaynaklar
(`/me/carts`), kaydın işlemleri (`/orders/{id}/cancel`).

Uçlar OpenAPI'de kaynağın çoğul adıyla etiketlenir (`Products`) ve işlem adları alır (`ListProducts`, `GetProduct`,
`CreateProduct`, `UpdateProduct`, `PatchProduct`, `DeleteProduct`).

## Anahtarlar

| Archetype | Anahtar | Kim verir | Tipi |
|---|---|---|---|
| `MapCrud`, `MapReadOnly`, `MapAppendOnly`, `MapJob`, `MapFile` | `Id` | Sunucu | `Guid` (sürüm 7: zamana göre sıralı). Entity'de `Id` yazarsanız `Guid`, `int` veya `long` olabilir. |
| `MapStore`, `MapBalance` | `Key` | İstemci, adreste | `string` |
| `MapLookup` | `Code` | İstemci, adreste | `string` |
| `MapSingleton` | Görünmez | | |

Anahtarın biçimi değiştirilebilir: önekli kimlik (`ord_01j9…`), rastgele GUID, ULID veya istemcinin verdiği kimlik;
bkz. [alanlar ve anahtarlar](../guides/fields.md#anahtar-biçimi).

## Yanıt ve istek tipleri

Her kaynak için dört tip üretilir (`Product` için):

| Tip | Kullanıldığı yer | İçindeki alanlar |
|---|---|---|
| `ProductResponse` | Bütün yanıtlar | Anahtar, sizin bütün alanlarınız (yalnızca yazılan alanlar hariç) ve Haldus'un alanları. |
| `CreateProductRequest` | `POST` | Herkese açık setter'ı olan skaler alanlarınız ve istemcinin yazabildiği yabancı anahtarlar. |
| `UpdateProductRequest` | `PUT` | Oluşturmadakiler; yalnızca oluştururken yazılan alanlar (`CreateOnly`) hariç. |
| `PatchProductRequest` | `PATCH` | Güncellemedekiler; hepsi isteğe bağlı. |

Kurallar:

- İsteklere yalnızca skaler alanlar girer (metin, sayı, tarih, `Guid`, enum, gömülü değer nesneleri). Gezinme
  özellikleri girmez.
- Herkese açık setter'ı olmayan alan yalnızca yanıtta görünür.
- Haldus'un yönettiği alanlar (`Status`, `Number`, `TenantId`, `CreatedAt`...) isteklerde yer almaz; gönderilseler de
  yok sayılırlar, çünkü istek tipinde karşılıkları yoktur.
- Sahiplik alanı (`OwnerId`) yanıtta da yer almaz.

## Listeler

Liste ucu her zaman sayfalıdır (`MapLookup` hariç):

```http
GET /products?page=2&pageSize=50
```

```json
{
  "items": [ ],
  "page": 2,
  "pageSize": 50,
  "totalCount": 137
}
```

| Parametre | Varsayılan | Sınır |
|---|---|---|
| `page` | 1 | 1'den küçük değer 1 sayılır. |
| `pageSize` | 20 | En çok 100; büyük değer 100'e indirilir. |

Sıralama verilmezse kayıtlar anahtar sırasıyla döner; varsayılan anahtar zamana göre sıralı olduğu için bu oluşturulma
sırasıdır. Sıralı kaynaklar (`Orderable`) `Position` sırasıyla döner. Süzme, sıralama ve arama:
[listeleme, süzme, sıralama ve arama](../guides/querying.md).

## Güncelleme: `PUT` ve `PATCH`

| | `PUT` | `PATCH` |
|---|---|---|
| Anlamı | Tam değiştirme | Kısmi değiştirme |
| Gönderilmeyen alan | Varsayılan değerine döner (zorunluysa 400) | Değişmez |
| `null` gönderilen alan | Boşaltılır | Boşaltılır |
| Gömülü satırlar | Zorunludur; listeyle eşitlenir | Yalnızca gönderildiyse eşitlenir |

`PATCH` "gönderilmedi" ile "`null` gönderildi"yi ayırt eder:

```http
PATCH /products/01a21347-0237-76b8-8f9b-7ce8db060eed
Content-Type: application/json

{ "description": null }
```

Bu istek yalnızca `description` alanını boşaltır; `name` ve `price` değişmez.

## Doğrulama

Entity'deki DataAnnotations (`[Required]`, `[MaxLength]`, `[Range]`, `[EmailAddress]`, `[RegularExpression]`...)
üretilen istek tiplerine kopyalanır ve her istekte uygulanır. İş mantığının istek tipleri (geçişler, aksiyonlar) de
aynı biçimde doğrulanır. Haldus'un kendi kuralları (referansın var olması, doğal anahtarın benzersizliği, sıralanabilir
alan) da alan düzeyinde hata verir.

## Hata yanıtları

Hatalar [problem details](https://www.rfc-editor.org/rfc/rfc9457) biçimindedir. Doğrulama hataları alan adlarını
(camelCase) taşır:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "name": ["The Name field is required."]
  },
  "traceId": "00-f40307b38fbb6f34bbf9a1495a2dc6a5-38a7b548ca72f28d-00"
}
```

Öteki hatalar `title` ve `detail` taşır:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Not allowed in the current state",
  "status": 409,
  "detail": "'start' is not allowed while the record is in the 'InProgress' state."
}
```

En sık durum kodları:

| Kod | Ne zaman |
|---|---|
| 400 | Doğrulama hatası; bilinmeyen sıralama alanı; var olmayan bir kayda referans. |
| 404 | Kayıt yok ya da isteği yapan onu göremiyor (başka kiracının veya kullanıcının kaydı). |
| 409 | Kayıt bu işlem için uygun durumda değil; benzersizlik çakışması; silinmek istenen kayda başka kayıt bağlı. |
| 412 | `If-Match` ile gönderilen sürüm eski. |
| 422 | İş mantığı işlemi reddetti (`TransitionRejectedException`). |
| 428 | Sürüm kontrollü kaynakta `If-Match` başlığı eksik. |

Bütün durum kodları ve başlıklar: [HTTP sözleşmesi](../reference/http.md).

## Dil

Haldus'un hata ve doğrulama mesajları İngilizce (varsayılan) ve Türkçedir. İsteğin dili `Accept-Language` başlığından
çözülür (`UseHaldus()`):

```http
POST /projects
Accept-Language: tr
Content-Type: application/json

{ "name": "" }
```

```json
{
  "title": "Bir veya daha fazla doğrulama hatası oluştu.",
  "status": 400,
  "errors": { "name": ["Name alanı zorunludur."] }
}
```

`Accept-Language` verinin kendisini değiştirmez; çevrilebilen alanlar ancak `?locale=` ile çevrilir (bkz.
[alanlar ve anahtarlar](../guides/fields.md#çeviriler)). Mesajları değiştirmek veya başka bir dil eklemek:
[yapılandırma](../reference/configuration.md).

## JSON

- Alan adları camelCase'dir (`createdAt`, `projectId`).
- Durum alanları (`status` ve ek eksenler) sayıyla değil adıyla yazılır: `"status": "Open"`.
- Zaman damgaları `DateTimeOffset`'tir ve mikrosaniyeye kırpılır (PostgreSQL daha hassasını saklamaz).
- Sürüm, yanıt gövdesinde değil `ETag` başlığında taşınır.

## Ayrıca bakın

- [Haldus nasıl çalışır](how-it-works.md)
- [HTTP sözleşmesi](../reference/http.md)
- [Yapılandırma](../reference/configuration.md)
