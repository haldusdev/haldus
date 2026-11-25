# Bakiyeler

Stok seviyesi, izin günü, hediye kartı bakiyesi, kredi: bu nicelikler `PUT` ile yazılmamalıdır. Aynı anda gelen iki
yazmadan biri ötekini ezer ("10 vardı, ikimiz de 9 yazdık") ve neyin neden değiştiği kaybolur. `MapBalance` bakiyeyi
yalnızca komutlarla değiştirir: "3 düş", "2'yi ayrılmış stoğa taşı". Her komut bakiye başına bir kilidin altında
çalışır ve her değişim bir deftere yazılır.

Örneklerde web SDK'nın örtük `using`'leri, `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs`'tir; `builder` ve `app`
satırları gösterilmediyse yazılmış sayılır.

## İçindekiler

- [Bakiye kaynağı](#bakiye-kaynağı)
- [Düzeltme: adjust](#düzeltme-adjust)
- [Mutlak değer: set](#mutlak-değer-set)
- [Defter: entries](#defter-entries)
- [Kilit ve eşzamanlılık](#kilit-ve-eşzamanlılık)
- [Kovalar](#kovalar)
- [Negatif bakiye](#negatif-bakiye)
- [Tutmalar](#tutmalar)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Bakiye kaynağı

<!-- example: balance -->
```csharp
app.MapBalance<LeaveAllowance>();

// Key ve Quantity alanlarını Haldus üretir.
public partial class LeaveAllowance;
```

Bakiyenin anahtarını istemci verir: bir SKU, bir çalışan numarası, bir kart kodu. Anahtar `Key` alanıdır, metindir ve
en çok 200 karakterdir. Haldus entity'ye şu alanları ekler:

| Alan | Anlamı |
|---|---|
| `Key` | Bakiyenin anahtarı (`string`). |
| `Quantity` | Bakiyenin değeri (`decimal`). Kovalar bildirildiyse onun yerine her kova için bir alan üretilir (bkz. [Kovalar](#kovalar)). |
| `CreatedAt`, `UpdatedAt` | Bakiye kendiliğinden `Auditable` olur: son ne zaman değiştiği bilinmelidir. |

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/leave-allowances` | Bakiyeleri listeler (sayfalı). |
| `GET` | `/leave-allowances/{key}` | Bir bakiyeyi okur. |
| `POST` | `/leave-allowances/{key}/adjust` | Bakiyeyi bir miktar artırır veya azaltır. |
| `POST` | `/leave-allowances/{key}/set` | Bakiyeyi mutlak bir değere çeker. |
| `GET` | `/leave-allowances/{key}/entries` | Defter: her değişim. |
| `POST` | `/leave-allowances/{key}/move` | Bir kovadan ötekine taşır; yalnızca birden çok kova varsa üretilir. |

Bakiye başka hiçbir yoldan yazılamaz: `POST /leave-allowances`, `PUT` ve `DELETE` uçları yoktur (405).

**Hiç yazılmamış bir anahtarın bakiyesi sıfırdır.** Kaydı önceden oluşturmazsınız: bir anahtara gelen ilk başarılı
komut kaydı oluşturur. Ama hiç yazılmamış bir anahtarın okuması 404 döner; defteri ve tutmaları boş bir liste döner.
200 karakterden uzun bir anahtara gelen komut 404 döner.

## Düzeltme: adjust

`POST /{key}/adjust` bakiyeye bir değişim ekler. Yeni değer değil, değişim gönderilir; böylece aynı anda gelen iki
düzeltme birbirini ezmez.

```http
POST /leave-allowances/EMP-1042/adjust
Content-Type: application/json

{ "delta": 14, "reason": "Yıllık izin hakkı" }
```

```json
{
  "key": "EMP-1042",
  "createdAt": "2026-11-25T12:00:00.312774+00:00",
  "updatedAt": null,
  "quantity": 14
}
```

| Alan | Zorunlu | Anlamı |
|---|---|---|
| `delta` | evet | Eklenecek (pozitif) veya düşülecek (negatif) miktar; sıfır olamaz. |
| `bucket` | hayır | Düzeltilen kova; verilmezse ilk kova. |
| `reason` | hayır | Deftere yazılan açıklama ("sayım farkı", "sipariş 1042"); en çok 500 karakter. |

| Durum | Yanıt |
|---|---|
| Düzeltme uygulandı | 200 ve bakiyenin yeni hali. |
| `delta` sıfır | 400, alan düzeyinde hata: `delta` |
| Bilinmeyen kova | 400, alan düzeyinde hata: `bucket` (mesaj bakiyenin kovalarını sayar) |
| Düşüş bakiyeyi sıfırın altına indiriyor | 409 `Insufficient balance`; hiçbir şey yazılmaz. |

Bakiyenin tamamını düşmek serbesttir (sonuç sıfır). Taban denetimi yalnızca azaltan değişimde yapılır: bir şekilde
eksiye düşmüş bir bakiye artırılabilir. Hiç yazılmamış bir anahtardan düşmek de 409'dur ve kayıt oluşturmaz.

## Mutlak değer: set

`POST /{key}/set` bakiyeyi bir değere çeker: bir sayımın sonucu gibi. Deftere aradaki fark yazılır.

```http
POST /leave-allowances/EMP-1042/set
Content-Type: application/json

{ "quantity": 12, "expected": 14, "reason": "Yıl sonu düzeltmesi" }
```

| Alan | Zorunlu | Anlamı |
|---|---|---|
| `quantity` | evet | Yeni değer. Negatif olamaz (`AllowNegative` yoksa). |
| `expected` | hayır | İstemcinin gördüğü değer. Verilirse ve bakiye o değerde değilse istek reddedilir. |
| `bucket` | hayır | Yazılan kova; verilmezse ilk kova. |
| `reason` | hayır | Deftere yazılan açıklama. |

| Durum | Yanıt |
|---|---|
| Yazıldı | 200 ve bakiyenin yeni hali. Değer zaten aynıysa deftere kayıt düşmez. |
| `quantity` negatif | 400, alan düzeyinde hata: `quantity` |
| `expected` uymuyor | 409; mesaj bakiyenin şimdiki değerini söyler. Değeri yeniden okuyup tekrar deneyin. |

`expected` vermeyen bir `set`, arada gelen düzeltmeleri ezer. Sayım gibi "okudum, şimdi yazıyorum" akışlarında her
zaman `expected` gönderin.

## Defter: entries

Bakiyeyi değiştiren her komut deftere bir kayıt ekler; kayıt bakiyeyle aynı kaydetmede yazılır. Reddedilen komut
deftere bir şey yazmaz.

```http
GET /leave-allowances/EMP-1042/entries
```

```json
{
  "items": [
    {
      "id": 2,
      "bucket": "quantity",
      "delta": -2,
      "balance": 12,
      "reason": "Yıl sonu düzeltmesi",
      "createdAt": "2026-11-25T12:05:00.004113+00:00",
      "createdBy": "ada"
    },
    {
      "id": 1,
      "bucket": "quantity",
      "delta": 14,
      "balance": 14,
      "reason": "Yıllık izin hakkı",
      "createdAt": "2026-11-25T12:00:00.312774+00:00",
      "createdBy": "ada"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 2
}
```

| Alan | Anlamı |
|---|---|
| `id` | Kaydın sırası; artar. |
| `bucket` | Değişen kova. |
| `delta` | Kovadaki değişim. |
| `balance` | Kovanın bu kayıttan sonraki değeri. |
| `reason` | Komutun açıklaması. |
| `createdAt`, `createdBy` | Ne zaman ve kim (isteği yapan kullanıcı). |

- En yeni kayıt baştadır. `?page=` ve `?pageSize=` ile sayfalanır, `?bucket=reserved` ile bir kovaya süzülür.
- Bütün bakiyelerin defteri tek bir tablodadır (`HaldusBalanceEntries`); kaynak, kiracı ve anahtarla ayrılır.
- Kiracıya ait bir bakiyenin defteri yalnızca o kiracıya gösterilir.

## Kilit ve eşzamanlılık

Her komut aynı sırayı izler: istek doğrulanır, bakiye kilitlenir, bakiye okunur (yoksa sıfırdan başlar), taban
denetlenir, kova ve defter yazılır, tek bir kaydetme yapılır, kilit bırakılır. Denetim ile yazma arasına başka bir
istek giremez.

- Kilit bakiye başınadır (kaynak + kiracı + anahtar; PostgreSQL danışma kilidi). Aynı bakiyeye gelen komutlar sıraya
  girer; farklı anahtarlar birbirini beklemez.
- Aynı anda gelen yirmi "1 düş" isteğinden, bakiye 10 ise tam on tanesi geçer, kalanı 409 alır; bakiye sıfırda kalır.
  Bu, entegrasyon testlerinde sınanır.
- `.Idempotent()` komutları `Idempotency-Key` başlığıyla tekrara karşı korur: ağ hatasından sonra yinelenen bir düşüş
  iki kez uygulanmaz, ikinci istek ilk yanıtı alır (bkz. [yaşam döngüsü](lifecycle.md)).

<!-- example: idempotent -->
```csharp
app.MapBalance<GiftCard>()
    .MultiTenant()
    .Idempotent();

public partial class GiftCard;
```

```http
POST /gift-cards/GC-7781/adjust
Content-Type: application/json
Idempotency-Key: 6f1c2a4e-1b7d-4a55-9d0e-3c8f2b1a9e77

{ "delta": -25, "reason": "Sipariş 1042" }
```

## Kovalar

`.Buckets(...)` bakiyeyi adlandırılmış kovalara böler. Stokta eldeki, ayrılmış ve hasarlı miktar; izinde kullanılabilir
ve onay bekleyen günler gibi. Her kova ayrı bir `decimal` alandır ve ayrı düzeltilir.

<!-- example: buckets -->
```csharp
app.MapBalance<Inventory>()
    .MultiTenant()
    .Buckets("available", "reserved", "damaged")
    .Sortable(inventory => inventory.Available);

public partial class Inventory;
```

```json
{
  "key": "SKU-1001",
  "createdAt": "2026-11-25T12:10:00.553201+00:00",
  "updatedAt": "2026-11-25T12:14:21.007346+00:00",
  "available": 6.5,
  "reserved": 0,
  "damaged": 2
}
```

Kurallar:

- Kova adları küçük harfli sözcüklerdir, `-` ile ayrılır ve derleme zamanında okunan sabitlerdir: `"available"`,
  `"on-hold"`. Alan adı Pascal case'tir (`OnHold`), JSON'da camelCase (`onHold`). Kurala uymayan veya iki kez geçen ad
  `HALDUS010` olur; `Key`, `TenantId`, `CreatedAt` veya `UpdatedAt` ile çakışan ad da öyle.
- İlk kova varsayılandır: `bucket` gönderilmeyen `adjust` ve `set` onu yazar.
- `.Buckets(...)` verilmezse bakiyenin tek bir kovası vardır: `quantity`.
- Kova alanları listede süzülebilir ve sıralanabilir (`Sortable`, `Filterable`, `Searchable` ile).

Birden çok kova varsa `POST /{key}/move` bir miktarı bir kovadan ötekine tek işlemde taşır:

```http
POST /inventories/SKU-1001/move
Content-Type: application/json

{ "from": "available", "to": "reserved", "quantity": 2, "reason": "Sipariş 1042" }
```

| Durum | Yanıt |
|---|---|
| Taşındı | 200 ve bakiyenin yeni hali. Deftere iki kayıt yazılır: kaynak kovada `-2`, hedef kovada `+2`. |
| `from` veya `to` bilinmeyen bir kova | 400, alan düzeyinde hata: `from` / `to` |
| `from` ile `to` aynı | 400, alan düzeyinde hata: `to` |
| `quantity` sıfır veya negatif | 400, alan düzeyinde hata: `quantity` |
| Kaynak kovada yeterli miktar yok | 409; hiçbir şey yazılmaz. |

İki kova tek kaydetmede değişir: miktar ya ikisinde birden yer değiştirir ya da hiçbirinde. Tek kovalı bir bakiyede
`move` ucu üretilmez (404).

Bekleyen talepler ayrı bir kovayla ifade edilir: izin talebi açılınca gün `available` kovasından `pending` kovasına
taşınır, onaylanınca `pending` kovasından düşülür, reddedilince geri taşınır. Süreli bir ayırma gerekiyorsa
[tutmalar](#tutmalar) bunu kendiliğinden yapar.

## Negatif bakiye

Hiçbir kova varsayılan olarak sıfırın altına düşemez. `.AllowNegative()` bu kuralı kaldırır: açık hesap, eksi stok,
avans izin.

<!-- example: negative -->
```csharp
app.MapBalance<LeaveAllowance>()
    .AllowNegative();

public partial class LeaveAllowance;
```

```http
POST /leave-allowances/EMP-1042/adjust
Content-Type: application/json

{ "delta": -15.5, "reason": "Avans izin" }
```

```json
{
  "key": "EMP-1042",
  "createdAt": "2026-11-25T12:00:00.312774+00:00",
  "updatedAt": "2026-11-25T12:20:00.871090+00:00",
  "quantity": -1.5
}
```

`AllowNegative` bildirildiğinde:

- `adjust`, `move` ve tutma açmak bakiyeyi eksiye düşürebilir; 409 `Insufficient balance` hiç dönmez.
- `set` negatif bir değer kabul eder.
- Kural bütün kovalar için geçerlidir; kova başına ayrı seçilemez.

## Tutmalar

`.Holds(from, to)` bakiyeye karşı süreli bir ayırma (rezervasyon, ön provizyon) açmayı sağlar. Tutma miktarı `from`
kovasından `to` kovasına taşır ve kapanana kadar orada bekletir. Sonra ya tahsil edilir (miktar bakiyeden düşer), ya
bırakılır (eldeki kovaya döner), ya da süresi dolunca kendiliğinden bırakılır. Sepete eklenen ürünün stoğu, ödeme
onayı bekleyen bir kartın tutarı böyle ayrılır.

<!-- example: holds -->
```csharp
app.MapBalance<Inventory>()
    .MultiTenant()
    .Buckets("available", "reserved", "damaged")
    .Holds(from: "available", to: "reserved")
    .Idempotent();

public partial class Inventory;
```

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `POST` | `/inventories/{key}/holds` | Tutma açar: miktar `available`'dan `reserved`'a geçer. |
| `GET` | `/inventories/{key}/holds` | Açık tutmalar, en eskisi başta (sayfasız dizi). |
| `POST` | `/inventories/{key}/holds/{holdId}/capture` | Tahsil: miktar `reserved`'dan düşer. |
| `POST` | `/inventories/{key}/holds/{holdId}/release` | Bırakma: miktar `available`'a döner. |

```http
POST /inventories/SKU-1001/holds
Content-Type: application/json

{ "quantity": 4, "expiresInSeconds": 900, "reason": "Sepet 77" }
```

```http
HTTP/1.1 200 OK
Content-Type: application/json
```

```json
{
  "id": "01936fa0-3c4d-7e5f-9a6b-7c8d9e0f1a2b",
  "quantity": 4,
  "status": "Active",
  "createdAt": "2026-11-25T12:30:00.200118+00:00",
  "expiresAt": "2026-11-25T12:45:00.200118+00:00",
  "reason": "Sepet 77"
}
```

| Alan | Zorunlu | Anlamı |
|---|---|---|
| `quantity` | evet | Tutulan miktar; sıfırdan büyük olmalıdır. |
| `expiresInSeconds` | hayır | Tutmanın ömrü: 1 saniye ile bir yıl (31 536 000) arası. Verilmezse tutma kendiliğinden bırakılmaz. |
| `reason` | hayır | Tutmada ve defterde görünen açıklama; en çok 500 karakter. |

Tahsil ve bırakma gövde almaz; bakiyenin yeni halini döndürür:

```http
POST /inventories/SKU-1001/holds/01936fa0-3c4d-7e5f-9a6b-7c8d9e0f1a2b/capture
```

```json
{
  "key": "SKU-1001",
  "createdAt": "2026-11-25T12:10:00.553201+00:00",
  "updatedAt": "2026-11-25T12:31:10.090455+00:00",
  "available": 6,
  "reserved": 0,
  "damaged": 0
}
```

| Durum | Yanıt |
|---|---|
| `quantity` sıfır veya negatif | 400 |
| `expiresInSeconds` aralığın dışında | 400 |
| Eldeki kovada yeterli miktar yok | 409 `Insufficient balance`; tutma açılmaz. |
| Bakiyenin 1000 açık tutması var | 409; yenisi için önce bazıları kapatılmalıdır. |
| Tutma bulunamadı (veya başka bir kiracının) | 404 |
| Tutma zaten kapandı (tahsil edildi, bırakıldı, süresi doldu) | 409 |
| Süresi geçmiş bir tutma tahsil edilmek isteniyor | 409. Süresi geçmiş ama henüz bırakılmamış tutma elle bırakılabilir. |

Defter her adımı gösterir: tutma açılınca iki kayıt (`available` `-4`, `reserved` `+4`); tahsilde bir kayıt
(`reserved` `-4`); bırakmada ve süre dolumunda iki kayıt (`reserved` `-4`, `available` `+4`). Bırakılan ve süresi dolan
tutmanın defter açıklamasının önünde `Released:` veya `Expired:` yazar.

### Süre dolumu

Ömrü verilen tutmayı, süresi dolunca Haldus'un bakım servisi bırakır. Bakım `HaldusOptions.MaintenanceInterval`
aralığıyla çalışır (varsayılan bir saat). Bu yüzden:

- Süresi geçmiş tutma, süresi geçtiği anda tahsil edilemez hale gelir (409).
- Ama miktarı bir sonraki bakım turuna kadar ayrılan kovada kalır. Miktarın hemen dönmesi gerekiyorsa tutmayı elle
  bırakın ya da bakım aralığını kısaltın:

<!-- example: maintenance -->
```csharp
builder.Services.AddHaldus<AppDbContext>(options => options.MaintenanceInterval = TimeSpan.FromMinutes(1));

var app = builder.Build();

app.MapBalance<Inventory>()
    .Buckets("available", "reserved")
    .Holds(from: "available", to: "reserved");

public partial class Inventory;
```

- Bakım her tutmayı kendi kiracısı adına kapatır. Kapatılamayanları atlayıp sıradakilere geçer; bir tur, kaynak başına
  en çok 1000 tutma dener.
- Ayrılan kova bu arada elle boşaltılmışsa süresi dolan tutma yine kapanır; geri dönen miktar kovada kalanla sınırlıdır.

Kurallar:

- `Holds` iki farklı kova adı ister ve ikisi de `Buckets` ile bildirilmiş olmalıdır; değilse `HALDUS010`. Tek kovalı
  bir bakiyede tutma yoktur.
- Tutma, kova ve defter kaydı bakiyenin kilidi altında, tek kaydetmede yazılır.
- `Idempotent()` tutma açmayı da korur; tahsil ve bırakma zaten ikinci kez uygulanamaz (409).
- Bütün tutmalar tek bir tablodadır (`HaldusBalanceHolds`); kapanan tutmalar silinmez.

## Birlikte kullanım

Bakiye yalnızca kendi komutlarıyla değişir. Zincirinde yalnızca şunlar yazılabilir:

| Bildirim | Etkisi |
|---|---|
| `MultiTenant()` | Her kiracının kendi bakiyeleri ve defteri olur. Kiracısı çözülemeyen istek 403 alır; başka kiracının bakiyesi 404'tür. |
| `Idempotent()` | `adjust`, `set`, `move` ve tutma açma `Idempotency-Key` ile korunur. |
| `Sortable(...)`, `Filterable<T>()`, `Searchable(...)` | Liste ucu kova alanlarına göre sıralanır, süzülür, aranır (bkz. [sorgulama](querying.md)). |
| `Buckets(...)`, `AllowNegative()`, `Holds(...)` | Bu sayfada. |
| `Without(...)`, `Only(...)` | Standart okuma uçlarını seçer. |

Başka her bildirim `HALDUS007` olur (`SoftDeletable`, `ConcurrencyChecked`, `Workflow`, `PublishesEvents`...): bakiyeyi
başka bir yoldan yazan veya gizleyen bir bildirim onu bozardı. `Buckets`, `AllowNegative` ve `Holds` da başka bir
`Map*` çağrısında `HALDUS007`'dir. Tanıların listesi: [tanılar](../reference/diagnostics.md).

## Sınırlar

- Bakiye silinemez.
- Entity'nin partial sınıfına eklediğiniz alanları hiçbir uç yazmaz.
- Uygulamanın kendi kodu bakiyeyi doğrudan (`DbContext` üzerinden) yazarsa deftere kayıt düşmez ve kilit alınmaz.
  Bakiyeyi yalnızca üretilen uçlardan değiştirin.
- Miktarın birimi ve ölçeği yoktur (`decimal`); para birimi, ondalık hane sayısı uygulamanın işidir.
- Defter ve kapanan tutmalar kendiliğinden temizlenmez.
- Bir bakiyenin en çok 1000 açık tutması olur; tutma kısmen tahsil edilemez; ömür en çok bir yıldır.
- Süresi dolan tutma, bakım aralığı kadar gecikmeyle bırakılır.
- Aynı bakiyeye gelen komutlar sıraya girer: tek bir çok sıcak anahtar (bütün siparişlerin düştüğü tek bir SKU) yük
  altında darboğaz olur.

## Ayrıca bakın

- [Archetype'lar](archetypes.md): öteki kaynak biçimleri.
- [Yaşam döngüsü](lifecycle.md): `Idempotent` ve bakım servisi.
- [Çok kiracılık ve sahiplik](tenancy-and-ownership.md)
- [Bildirim dizini](../reference/declarations.md)
