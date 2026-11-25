# Sırlar ve erişim token'ları

Bazı kayıtlar bir sır taşır: istemci uygulamaların API anahtarı, olay gönderilerini imzalayan webhook sırrı, bir
davetin e-postayla giden bağlantısı. Bu sırların ortak kuralları vardır: sunucu üretir, yalnızca bir kez gösterilir,
veritabanında kendisi değil özeti durur ve sızdığında değiştirilebilir. Haldus bu kuralları dört bildirimle verir:
`SecretOnce` (bir kez gösterilen sır), `Rotatable` (döndürme), `TokenAccess` (oturumu olmayan birinin token ile
erişimi) ve `TokenOnly` (kayda yalnızca token ile ulaşılması).

Örneklerde web SDK'nın örtük `using`'leri, `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs` içindedir; `builder` ve
`app` değişkenleri [kurulum](../getting-started/installation.md) sayfasındaki gibi tanımlıdır.

## İçindekiler

- [Bir kez gösterilen sır](#bir-kez-gösterilen-sır)
- [Döndürme](#döndürme)
- [Token ile erişim](#token-ile-erişim)
- [Yalnızca token](#yalnızca-token)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Bir kez gösterilen sır

`SecretOnce(prefix)` kayda sunucuda üretilen bir sır verir. Sır kayıt oluşturulurken üretilir ve yalnızca oluşturma
yanıtında görünür; sonraki hiçbir yanıt onu (ya da özetini) taşımaz. İstemci sırrı o an saklamalıdır.

<!-- example: secret-once -->
```csharp
app.MapCrud<ApiKey>()
    .MultiTenant()
    .Without(CrudOperation.Update, CrudOperation.Patch)
    .SecretOnce(prefix: "sk")
    .Rotatable(overlap: TimeSpan.FromHours(1));

public partial class ApiKey
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";
}
```

### Sırrın biçimi ve saklanması

| | |
|---|---|
| Biçim | `{önek}_{gövde}`: `sk_Zq3…` (gövde 62 karakterlik alfabeden 43 karakter; 256 bitten fazla rastgelelik) |
| Önek | Küçük harfle başlar, küçük harf ve rakamlardan oluşur, en çok 12 karakterdir. Sırrın türünü ve sızdığında nerede aranacağını gösterir. |
| Veritabanı | `SecretHash`: sırrın SHA-256 özeti (onaltılı, küçük harf). Sır yüksek entropili olduğu için yavaş bir parola özetine gerek yoktur; özet aramada da kullanılır. |
| Listede | `SecretHint`: sırrın son dört karakteri; hangi anahtarın hangisi olduğunu ayırt etmeye yeter. |

Entity `IHasSecret` arayüzünü uygular; `SecretHash` ve `SecretHint` alanlarını Haldus üretir. İstek gövdesindeki
`secret`, `secretHash` veya `secretHint` alanları yok sayılır: sırrı istemci seçemez.

### Üretilen uçlar

Kaynağın standart uçları değişmez; oluşturma yanıtı ek olarak `secret` alanını taşır.

```http
POST /api-keys
Content-Type: application/json

{ "name": "CI" }
```

```json
{
  "id": "0193a5cb-6000-7f00-8a00-617283940001",
  "name": "CI",
  "secretHint": "x9Qa",
  "secret": "sk_Hk2vT0pW8mLr4YbN6cQe1sJu7aZd3FgXo5iRn9Ev0x9Qa"
}
```

```http
GET /api-keys/0193a5cb-6000-7f00-8a00-617283940001
```

```json
{
  "id": "0193a5cb-6000-7f00-8a00-617283940001",
  "name": "CI",
  "secretHint": "x9Qa"
}
```

Yanıttaki `secret` alanı boş olduğunda JSON'a hiç yazılmaz.

### Sunulan sırrı doğrulamak

Bir istemci sırrı bir istekte sunduğunda (`X-Api-Key` başlığı gibi) hangi kayda ait olduğunu `FindBySecretAsync`
bulur; yoksa `null` döner. Döndürülmüş bir sırrın eskisi, örtüşme süresi dolana kadar kabul edilir.

Sorgu, verdiğiniz kaynağın filtrelerinden geçer. Kimlik doğrulanırken kiracı henüz bilinmez; bu yüzden aşağıdaki uç
kiracı filtresini adıyla kaldırır ve kiracıyı bulunan kayıt söyler:

<!-- example: find-by-secret -->
```csharp
app.MapCrud<ApiKey>()
    .MultiTenant()
    .SecretOnce(prefix: "sk");

app.MapGet("/whoami", async (HttpContext http, AppDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
{
    var key = await db.Set<ApiKey>()
        .IgnoreQueryFilters([HaldusFilters.Tenant])
        .FindBySecretAsync(http.Request.Headers["X-Api-Key"], HaldusTime.UtcNow(time), cancellationToken);

    return key is null ? Results.Unauthorized() : Results.Ok(new { key.Id, key.Name, key.TenantId });
})
    .AllowAnonymous();

public partial class ApiKey
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";
}
```

Parametresiz `IgnoreQueryFilters()` kullanmayın: silinmiş (`SoftDeletable`) ve süresi dolmuş (`Expiring`) kayıtların
filtrelerini de kaldırır. Yalnızca kaldırmak istediğiniz filtreyi adıyla verin. Boş veya 512 karakterden uzun bir
değer hiçbir kayda ait sayılmaz.

### Geri okunabilen sır

Bazı sırları sunucunun kendisi kullanmalıdır: webhook gönderilerini imzalayan sır gibi. `recoverable: true` sırrı
özetinin yanında ASP.NET Data Protection ile şifreleyerek de saklar (`SecretCipher`); sunucu onu
`HaldusSecretProtection.Reveal` ile açar. Sır yine hiçbir yanıtta görünmez.

<!-- example: recoverable -->
```csharp
using System.Security.Cryptography;
using System.Text;

var webhooks = app.MapCrud<WebhookEndpoint>()
    .MultiTenant()
    .SecretOnce(prefix: "whsec", recoverable: true)
    .Rotatable();

// POST /webhook-endpoints/{id}/sign-payload
webhooks.MapAction<SignPayload, PayloadSignature>();

public partial class WebhookEndpoint
{
    [Required]
    [Url]
    [MaxLength(500)]
    public string Url { get; set; } = "";
}

public sealed record SignPayload
{
    [Required]
    [MaxLength(10_000)]
    public string Payload { get; init; } = "";
}

public sealed record PayloadSignature(string Signature);

// Haldus handler'ı bulur ve kaydeder. Sırrı açar ve imzayı hesaplar; sırrın kendisi yanıta girmez.
public sealed class SignPayloadHandler(IServiceProvider services)
    : IEntityActionHandler<WebhookEndpoint, SignPayload, PayloadSignature>
{
    public Task<PayloadSignature> HandleAsync(WebhookEndpoint entity, SignPayload request, CancellationToken cancellationToken)
    {
        var secret = HaldusSecretProtection.Reveal(services, entity);
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(request.Payload));

        return Task.FromResult(new PayloadSignature(Convert.ToHexStringLower(signature)));
    }
}
```

Şifrelemenin amaç dizesi entity tipini içerir: bir kaynağın şifreli sırrı başka bir kaynağın alanına kopyalanarak
açılamaz. `AddHaldus` Data Protection'ı kaydeder, ama anahtarların kalıcı ve sunucular arasında ortak olması
uygulamanın işidir; yoksa yeniden başlatılan ya da başka bir sunucuda çalışan uygulama sırları açamaz. Anahtarların
nasıl saklanacağı [hassas ve şifreli alanlar](sensitive-data.md) sayfasında anlatılır.

### Seçenekler

| Parametre | Varsayılan | Anlamı |
|---|---|---|
| `prefix` | (zorunlu) | Sırrın öneki; derleme zamanında okunan bir metin sabiti. Kurala uymayan önek `HALDUS010`'dur. |
| `recoverable` | `false` | Sır ayrıca şifrelenerek saklansın mı (sunucu kullanacaksa). |

## Döndürme

`Rotatable()` sırrın değiştirilmesini sağlar: sızmış veya süresi gelmiş bir sır kaydı silmeden yenilenir.
`SecretOnce` ile birlikte yazılır; tek başına `HALDUS007`'dir.

### Üretilen uçlar

| Yöntem ve adres | Ne yapar |
|---|---|
| `POST /api-keys/{id}/rotate` | Yeni bir sır üretir ve onu bir kez gösterir (`200`). |

```http
POST /api-keys/0193a5cb-6000-7f00-8a00-617283940001/rotate
```

```json
{
  "id": "0193a5cb-6000-7f00-8a00-617283940001",
  "name": "CI",
  "secretHint": "mT7w",
  "secret": "sk_Pc8nQ2rV6xLk0WbJ4dYe9sHu1aFz5GgTo3iRm7Ev2mT7w"
}
```

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| Kayıt yok veya isteği yapana görünmüyor (başka kiracının kaydı dahil) | `404` |
| Kaynak `ConcurrencyChecked` ve `If-Match` yok / eski | `428` / `412` |
| Kayıt bir kayıt zincirinde kapanmış (ters çevrilmiş, yerine geçilmiş) | `409` |

- `overlap` verilmezse eski sır hemen geçersiz olur. Verilirse eski sır o süre boyunca `FindBySecretAsync` tarafından
  kabul edilmeye devam eder; istemcilerinizi kesintisiz yeni sırra geçirmek için zaman tanır.
- Yalnızca bir önceki sır tutulur: örtüşme süresi içinde yeniden döndürülen kayıtta en eski sır hemen ölür.
- Döndürme kaydın içeriğine dokunmaz; bu yüzden kaydın durumuna (`EditableIn`) bağlı değildir. Sızmış bir sır her
  durumda kapatılabilmelidir.
- Geri okunabilen sırda yeni sır şifrelenerek saklanır; `Reveal` döndürmeden sonra yeni sırrı verir.

### Seçenekler

| Parametre | Varsayılan | Anlamı |
|---|---|---|
| `overlap` | `null` (yok) | Eski sırrın geçerli kalacağı süre; `TimeSpan.FromHours(24)` gibi sabit bir ifade. |

## Token ile erişim

`TokenAccess(prefix, operations...)` kaydın, oturumu olmayan biri tarafından gizli bir token ile okunmasını ve
sayılan işlemlerin token ile çalıştırılmasını sağlar. Davet bağlantısı tipik örnektir: davet edilen kişinin henüz
hesabı yoktur; daveti e-postadaki bağlantının taşıdığı token ile okur ve kabul eder. Yönetici aynı daveti oturumuyla,
kimliğiyle görür ve iptal eder.

<!-- example: token-access -->
```csharp
var invitations = app.MapCrud<Invitation>()
    .MultiTenant()
    .Without(CrudOperation.Update, CrudOperation.Patch, CrudOperation.Delete)
    .Workflow<InvitationStatus>(initial: InvitationStatus.Pending)
    .Expiring(TimeSpan.FromDays(7))

    // Token ile yalnızca "accept" çalışır; iptal için oturum gerekir.
    .TokenAccess(prefix: "inv", "accept");

invitations.MapTransition("accept", from: InvitationStatus.Pending, to: InvitationStatus.Accepted);
invitations.MapTransition("revoke", from: InvitationStatus.Pending, to: InvitationStatus.Revoked);

public enum InvitationStatus
{
    Pending,
    Accepted,
    Revoked,
}

public partial class Invitation
{
    [Required]
    [EmailAddress]
    [MaxLength(200)]
    public string Email { get; set; } = "";
}
```

Token sırla aynı biçimdedir (`inv_…`) ve aynı kurallarla saklanır: veritabanında yalnızca özeti (`TokenHash`) durur,
yalnızca oluşturma yanıtında görünür (`token` alanı). Entity `IHasAccessToken` arayüzünü uygular.

### Üretilen uçlar

| Yöntem ve adres | Ne yapar |
|---|---|
| `GET /invitations/by-token` | `X-Access-Token` başlığındaki token'ın kaydını döndürür. |
| `POST /invitations/by-token/accept` | Sayılan geçişi veya aksiyonu token'ın kaydında çalıştırır. |

Kaynağın kimlikle çalışan uçları da (`GET /invitations/{id}`, `POST /invitations/{id}/revoke`...) yerinde durur ve
her zamanki gibi oturum ister.

```http
POST /invitations
Content-Type: application/json

{ "email": "ada@example.com" }
```

```json
{
  "id": "0193a5cc-7000-7a10-8b10-728394050001",
  "email": "ada@example.com",
  "status": "Pending",
  "expiresAt": "2026-12-02T15:00:00.208113+00:00",
  "token": "inv_W2kd9LpQx7Rt4VbN0mZc8sHe3aJu6FgYo1iTn5Er4"
}
```

Davet edilen kişi oturum açmadan:

```http
GET /invitations/by-token
X-Access-Token: inv_W2kd9LpQx7Rt4VbN0mZc8sHe3aJu6FgYo1iTn5Er4
```

```json
{
  "id": "0193a5cc-7000-7a10-8b10-728394050001",
  "email": "ada@example.com",
  "status": "Pending",
  "expiresAt": "2026-12-02T15:00:00.208113+00:00"
}
```

```http
POST /invitations/by-token/accept
X-Access-Token: inv_W2kd9LpQx7Rt4VbN0mZc8sHe3aJu6FgYo1iTn5Er4
```

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| Başlık yok, boş veya token hiçbir kayda ait değil | `404`: "The request carries no valid access token; send it in the X-Access-Token header." İkisi aynı yanıtı alır; yanıt bir token'ın var olup olmadığını ele vermez. |
| Kayıt silinmiş veya süresi dolmuş (`Expiring`) | `404` |
| Geçişin kuralı (ör. kabul edilmiş davet yeniden kabul ediliyor) | Geçişin kendi yanıtı: `409`, `422`... |
| `TokenAccess`'te sayılmayan bir işlemin token adresi | Üretilmez (`404` veya `405`) |

- Token ayrı bir başlıkta (`X-Access-Token`) taşınır, adreste değil; böylece erişim günlüklerine ve tarayıcı
  geçmişine girmez. E-postadaki bağlantı token'ı kendi uygulamanızın sayfasına taşır; sayfa onu başlığa koyar.
- Token uçları kimlik doğrulama istemez (`AllowAnonymous`); kiracı ve kullanıcı zorunluluğu da onlara uygulanmaz.
  İsteği yapanı token tanıtır. İstekte başka bir kiracının başlığı olsa da sonuç değişmez.
- Token ile çalışan geçiş, kaydı bulduktan sonra kimlikle çalışan geçişin kendisini çağırır: durum kuralı, iş mantığı
  ve (`ConcurrencyChecked` kaynakta) `If-Match` aynen geçerlidir. Kayıt kiracıya aitse geçiş o kiracı adına çalışır; değişiklik kaydın kendi kiracısında
  görünür.
- Hangi işlemlerin token ile çalıştığı derleme zamanında sabittir. `TokenAccess`'te var olmayan bir geçiş veya aksiyon
  adı yazmak `HALDUS007`'dir.
- Token kullanılınca kendiliğinden geçersiz olmaz. Ömrünü `Expiring` ve durum makinesi belirler: süresi dolan davet
  token ile bulunamaz, kabul edilmiş davet yeniden kabul edilemez.

### Seçenekler

| Parametre | Anlamı |
|---|---|
| `prefix` | Token'ın öneki; kuralları sır önekiyle aynıdır. |
| `operations` | Token ile çalıştırılabilen geçişlerin ve entity aksiyonlarının adları. Boş bırakılırsa token yalnızca okur. |

## Yalnızca token

`TokenAccess` kimlikle çalışan uçları da bırakır: davette yönetici kimlikle iptal eder, davetli token ile kabul eder.
Sahibi bir kullanıcı olmayan bir kayıtta ise bu, kimliği bilen herkese yazma izni demek olurdu. `TokenOnly()` kayda
**yalnızca** token ile ulaşılmasını sağlar: kimlikle çalışan hiçbir uç üretilmez. Geriye oluşturma ile `/by-token`
uçları kalır.

Tipik örnek misafir sepetidir: ziyaretçinin hesabı yoktur, sepetini oturumsuz oluşturur; sonrasında sepeti
yalnızca onu oluşturanın bildiği token tanıtır.

<!-- example: token-only -->
```csharp
var guests = app.MapCrud<GuestBasket>()
    .Expiring(TimeSpan.FromDays(7), sliding: true)
    .TokenAccess(prefix: "basket", "add-basket-item")
    .TokenOnly();

guests.MapDetail<GuestBasketItem>();
guests.MapAction<AddBasketItem>();

// Misafirin hesabı yoktur: oluşturma ucu da oturum istemez. Token uçları zaten istemez.
guests.AllowAnonymous();

public partial class GuestBasket
{
    [MaxLength(200)]
    public string? Note { get; set; }
}

public partial class GuestBasketItem
{
    [Required]
    [MaxLength(200)]
    public string ProductName { get; set; } = "";

    [Range(1, 1_000)]
    public int Quantity { get; set; }
}

// POST /guest-baskets/by-token/add-basket-item: aynı ürün yeniden eklenirse satır çoğalmaz, miktarı artar.
public sealed record AddBasketItem : IEntityAction<GuestBasket>
{
    [Required]
    [MaxLength(200)]
    public string ProductName { get; init; } = "";

    [Range(1, 1_000)]
    public int Quantity { get; init; } = 1;

    public void Apply(GuestBasket entity)
    {
        if (entity.GuestBasketItems.FirstOrDefault(item => item.ProductName == ProductName) is { } existing)
        {
            existing.Quantity += Quantity;
            return;
        }

        entity.GuestBasketItems.Add(new GuestBasketItem { ProductName = ProductName, Quantity = Quantity });
    }
}
```

### Üretilen uçlar

| Yöntem ve adres | Ne yapar |
|---|---|
| `POST /guest-baskets` | Sepeti oluşturur; yanıt `token` alanını bir kez taşır. |
| `GET /guest-baskets/by-token` | Token'ın sepetini döndürür. |
| `POST /guest-baskets/by-token/add-basket-item` | Sayılan aksiyonu çalıştırır. |

Kimlikle çalışan adresler (`GET /guest-baskets/{id}`, `GET /guest-baskets`, `PUT`, `PATCH`, `DELETE`,
`POST /guest-baskets/{id}/add-basket-item`, `POST /guest-baskets/{id}/expire`) üretilmez; onlara gelen istek `404`
veya `405` alır.

```http
POST /guest-baskets
Content-Type: application/json

{ "guestBasketItems": [ { "productName": "Keyboard", "quantity": 1 } ] }
```

```json
{
  "id": "0193a5cd-8000-7b20-8c20-839405160001",
  "note": null,
  "expiresAt": "2026-12-02T16:00:00.208113+00:00",
  "guestBasketItems": [
    { "id": "0193a5cd-8000-7b20-8c20-839405160002", "productName": "Keyboard", "quantity": 1 }
  ],
  "token": "basket_Lr5mQ8xW1kTz7VbN3dYc0sHe6aJu9FgPo2iRn4Ev8"
}
```

```http
POST /guest-baskets/by-token/add-basket-item
X-Access-Token: basket_Lr5mQ8xW1kTz7VbN3dYc0sHe6aJu9FgPo2iRn4Ev8
Content-Type: application/json

{ "productName": "Keyboard", "quantity": 3 }
```

```json
{
  "id": "0193a5cd-8000-7b20-8c20-839405160001",
  "note": null,
  "expiresAt": "2026-12-02T16:05:00.208113+00:00",
  "guestBasketItems": [
    { "id": "0193a5cd-8000-7b20-8c20-839405160002", "productName": "Keyboard", "quantity": 4 }
  ]
}
```

Kullanıcı oturum açınca misafir sepetini kendi sepetine katmak alana özgü bir iştir ve bir entity aksiyonuyla
yazılır; örnek mağazada `POST /me/baskets/{id}/merge` gövdede `guestToken` alır (bkz. [örnekler](../samples.md)).

### Kurallar

- `TokenOnly`, `TokenAccess` ile birlikte yazılır; tek başına `HALDUS007`'dir. Zincirdeki yeri önemli değildir.
- Kaydın bütün geçişleri ve aksiyonları `TokenAccess`'te sayılmalıdır; sayılmayan bir işlem hiçbir zaman
  çağrılamazdı ve `HALDUS007` olur.
- Zincirde yalnızca kayda kimliğiyle ulaşmayan bildirimler yazılabilir: `TokenAccess`, `Only`, `Without`,
  `Expiring`, `Workflow`, `EditableIn`, `DeletableIn`, `Axis`, `MultiTenant`, `Auditable`, `Idempotent`, `Numbered`,
  `Embeds`, `References`, `KeyStrategy`, `Sensitive`, `Encrypted`, `Metadata`, `PublishesEvents`, `Retention`.
  Kimlikli bir uç üreten veya kayıtları listeleyen her bildirim `HALDUS007`'dir.
- Kayda kimliğiyle ulaşan başka kaynaklar da reddedilir (`HALDUS007`): onun altına yerleşen (`Under`) veya ona takılan
  (`AttachedTo`) kaynak, ondan kimlikle üretilen kaynak (`CreatedFrom`, `on:` olmadan), bağın ucu olması
  (`MapAssociation`), dışa veya içe aktarılması ve satırların komutla yazılması (`AsCommands`).

<!-- example: token-only-listed, error: HALDUS007 -->
```csharp
// Hatalı: kopyalama kimlikle çalışan bir uçtur (POST /{id}/clone); kayda token olmadan ulaşılırdı.
app.MapCrud<GuestBasket>()
    .TokenAccess(prefix: "basket")
    .TokenOnly()
    .Cloneable();

public partial class GuestBasket
{
    [MaxLength(200)]
    public string? Note { get; set; }
}
```

## Birlikte kullanım

| Bildirim | Birleştiği archetype'lar | Birleşmediği bildirimler (`HALDUS007`) |
|---|---|---|
| `SecretOnce` | `MapCrud`, `MapAppendOnly` | `Batch`, `Cloneable`, `Previewable`, `Reversible`, `Supersedes`, `Document`, `CreatedFrom` (öyle doğan kaydın gösterilecek sırrı olmazdı), `Revisioned` (görüntüler sırrın özetini saklardı), `Publishable`; `Create` işlemi kaldırılmışsa |
| `Rotatable` | `SecretOnce` ile | `SecretOnce` olmadan |
| `TokenAccess` | `MapCrud`, `MapAppendOnly` | `SecretOnce` ile aynı liste; ayrıca `Under<T>()` ve `OwnedByCurrentUser` (kaydın görünürlüğü, token'ı taşıyanın sahip olmadığı bir kimliğe bağlı olurdu) |
| `TokenOnly` | `TokenAccess` ile | Yukarıdaki listede olmayan her bildirim |

`SecretOnce` ve `TokenAccess` aynı kayıtta birlikte kullanılabilir. Bir kaynağın sırrı ile token'ı ayrı alanlardır.

<!-- example: secret-batch, error: HALDUS007 -->
```csharp
// Hatalı: toplu oluşturmanın yanıtı her kaydın sırrını gösteremez.
app.MapCrud<ApiKey>()
    .SecretOnce(prefix: "sk")
    .Batch();

public partial class ApiKey
{
    [MaxLength(100)]
    public string Name { get; set; } = "";
}
```

## Sınırlar

- Sır ve token yalnızca tekil oluşturma yanıtında (ve döndürme yanıtında) gösterilir. Kaybedilen sır geri alınamaz;
  döndürülür.
- Token kullanılınca geçersiz olmaz; tek kullanımlık token yoktur. Ömrünü `Expiring` ve durum makinesi belirler.
- Token uçlarında oran sınırlama yoktur. Token 256 bitten fazla rastgelelik taşıdığı için denenerek bulunamaz, ama
  uçları korumak isterseniz ASP.NET Core'un hız sınırlayıcısını builder'a ekleyin (`.RequireRateLimiting(...)`).
- `TokenOnly` kaynakta oluşturma yanıtının `Location` başlığı, var olmayan kimlikli adresi gösterir.
- Geri okunabilen sırlar ASP.NET Data Protection anahtarlarına bağlıdır; anahtarlar kaybolursa sırlar açılamaz.
- Sır özeti tuzsuz SHA-256'dır. Bu, sunucunun ürettiği yüksek entropili sırlar için yeterlidir; kullanıcının seçtiği
  parolalar için değildir. Haldus parola saklamaz.

## Ayrıca bakın

- [Bildirim dizini](../reference/declarations.md)
- [Hassas ve şifreli alanlar](sensitive-data.md)
- [İş mantığı](business-logic.md): entity aksiyonları ve handler'lar
- [Durum makineleri](state-machines.md)
- [Yaşam döngüsü](lifecycle.md): `Expiring`
- [Kiracılar ve sahiplik](tenancy-and-ownership.md)
- [Üretime hazırlık](production.md)
