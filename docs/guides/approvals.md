# Onay ve belge

Bazı kayıtlar yazıldıkları anda geçerli değildir: bir masraf talebini başka biri onaylamalıdır, büyük bir satın alma
iki kişinin sırayla onayını ister, bir sözleşme taslak olarak hazırlanır, gönderilir, gerekirse iptal edilip düzeltilir.
Haldus bu kalıpları iki bildirimle verir. `Approvable` bir kaydı ikinci bir kişinin kararına bağlar ve içeriği
değişince kararı geçersiz kılar. `Document` taslak, gönderilmiş ve iptal edilmiş hallerden oluşan hazır bir durum
makinesidir.

Bu sayfadaki örneklerde web SDK'nın örtük `using`'leri, `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs` içindedir;
`builder` ve `app` satırları yazılmadıysa yazılmış sayılır. Hata yanıtları problem details biçimindedir
(`application/problem+json`); örneklerde yalnızca `title`, `status` ve `detail` gösterilir.

Onay kararı bir kullanıcı ister. Haldus kullanıcıyı token'daki claim'den okur (`HaldusOptions.UserClaimType`,
varsayılanı `NameIdentifier`); ayrıntı: [Yapılandırma](../reference/configuration.md).

## İçindekiler

- [Onay](#onay)
- [Onay zinciri](#onay-zinciri)
- [Belge](#belge)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Onay

`.Approvable()` kaydı ikinci bir kişinin kararına bağlar. Kayıt karar bekleyerek doğar; içeriği yazmış olmayan biri
onaylar veya reddeder.

<!-- example: expense-claims -->
```csharp
app.MapCrud<ExpenseClaim>()
    .Approvable();

public partial class ExpenseClaim
{
    [Required, MaxLength(200)]
    public string Description { get; set; } = "";

    [Range(0.0, 100_000.0)]
    public decimal Amount { get; set; }
}
```

### Üretilen alanlar

Alanlar yanıtta görünür, isteklerde yer almaz ve yalnızca Haldus tarafından yazılır.

| Alan | Tip | Anlamı |
|---|---|---|
| `ApprovalStatus` | `ApprovalStatus` | `Pending`, `Approved` veya `Rejected`. Yanıtta adıyla yazılır. |
| `ApprovalLevel` | `int` | Şimdiye kadar onaylayan düzey sayısı. İçerik değişince sıfırlanır. |
| `RequestedBy` | `string?` | İçeriği son yazan kullanıcı. |
| `Contributors` | `List<string>` | İçeriği şimdiye kadar yazmış herkes. Hiçbiri kayda karar veremez. |
| `RequestedAt` | `DateTimeOffset` | İçeriğin son yazıldığı an. |
| `DecidedBy`, `DecidedAt`, `DecisionNote` | | Son kararı veren, zamanı ve notu. |

### Üretilen uçlar

| Yöntem ve adres | Gövde | Ne yapar |
|---|---|---|
| `POST /expense-claims/{id}/approve` | İsteğe bağlı: `{ "note": "..." }` | Sıradaki düzeyi onaylar; son düzeyse kayıt `Approved` olur. |
| `POST /expense-claims/{id}/reject` | İsteğe bağlı: `{ "note": "..." }` | Kaydı reddeder (`Rejected`); hangi düzeyde verilirse verilsin. |
| `GET /expense-claims/{id}/approvals` | | Kayda verilmiş bütün kararlar, veriliş sırasıyla. |

Not en çok 1000 karakterdir; daha uzunu 400 alır. Kararlar silinmez: ret ve sonraki onay birlikte listelenir.

### Örnek

Ada bir masraf talebi oluşturur. Onay alanları gövdede gönderilse de yok sayılır:

```http
POST /expense-claims
Content-Type: application/json

{ "description": "Taxi", "amount": 40 }
```

```json
{
  "id": "0196f3e1-4c2d-7b10-9f3e-8a1c2d3e4f50",
  "description": "Taxi",
  "amount": 40,
  "approvalStatus": "Pending",
  "approvalLevel": 0,
  "requestedBy": "u-ada",
  "contributors": ["u-ada"],
  "requestedAt": "2026-11-25T08:30:12.123456+00:00",
  "decidedBy": null,
  "decidedAt": null,
  "decisionNote": null
}
```

Grace onaylar:

```http
POST /expense-claims/0196f3e1-4c2d-7b10-9f3e-8a1c2d3e4f50/approve
Content-Type: application/json

{ "note": "Looks fine" }
```

```json
{
  "id": "0196f3e1-4c2d-7b10-9f3e-8a1c2d3e4f50",
  "description": "Taxi",
  "amount": 40,
  "approvalStatus": "Approved",
  "approvalLevel": 1,
  "requestedBy": "u-ada",
  "contributors": ["u-ada"],
  "requestedAt": "2026-11-25T08:30:12.123456+00:00",
  "decidedBy": "u-grace",
  "decidedAt": "2026-11-25T09:02:47.654321+00:00",
  "decisionNote": "Looks fine"
}
```

Kararların listesi:

```http
GET /expense-claims/0196f3e1-4c2d-7b10-9f3e-8a1c2d3e4f50/approvals
```

```json
[
  {
    "level": 1,
    "approved": true,
    "userId": "u-grace",
    "note": "Looks fine",
    "decidedAt": "2026-11-25T09:02:47.654321+00:00"
  }
]
```

### Kim karar verebilir

Haldus bir kararı şu sırayla denetler; ilk uymayan kural yanıtı belirler:

| Kural | Uymazsa |
|---|---|
| Kayıt var ve isteği yapana görünüyor | 404 |
| İsteğin bir kullanıcısı var | 401 |
| Kayıt karar bekliyor (`Pending`) | 409 |
| İsteği yapan, kaydın içeriğini hiç yazmamış (`Contributors` içinde değil) | 403 |
| İsteği yapan, bu içeriğin önceki bir düzeyini onaylamamış | 403 |
| İsteği yapan, sıradaki düzeyin rolünde ([onay zinciri](#onay-zinciri) bildirildiyse) | 403 |

İçeriği yazan kendi yazdığına karar veremez:

```http
HTTP/1.1 403 Forbidden
Content-Type: application/problem+json
```

```json
{
  "title": "Not allowed to decide",
  "status": 403,
  "detail": "You wrote this record last, so someone else has to decide on it."
}
```

Karar verilmiş kayda yeniden karar verilemez:

```json
{
  "title": "Not allowed in the current state",
  "status": 409,
  "detail": "'approve' is not allowed while the record is in the 'Approved' state."
}
```

"İçeriği yazan" yalnızca son yazan değildir. Ada talebi oluşturur, Grace tutarı değiştirir: artık ikisi de
`contributors` listesindedir ve ikisi de karar veremez. Kararı üçüncü bir kişi verir. Böylece ikinci bir kişinin küçük
bir düzenlemesi, ilk yazanın kendi içeriğini onaylamasının yolunu açmaz.

### İçerik değişince onay düşer

Kaydın içeriği hangi yoldan değişirse değişsin toplanan onaylar geçersiz olur ve kayıt yeniden karar bekler:
`ApprovalStatus` `Pending`, `ApprovalLevel` `0` olur, içeriği değiştiren kişi `contributors` listesine eklenir.

İçerik şunlardır: sizin yazdığınız alanlar, ilişkilerin ürettiği yabancı anahtarlar (`CategoryId`), gömülü satırlar
(`MapDetail`), `Metadata`, özel alanlar ve çeviriler. Haldus'un yönettiği alanlar (durum, numara, zaman damgaları,
onay alanlarının kendisi) içerik değildir.

Onayı düşüren yollar:

- `PUT` ve `PATCH`, toplu güncelleme, komutla yazılan satırlar,
- içeriği değiştiren bir geçiş veya entity aksiyonu,
- bir sürüme geri yükleme ([Sürümler](history-and-publishing.md)),
- uygulamanın kendi kodunun `SaveChangesAsync` ile yaptığı yazma.

Kuralı uçlar değil, kaydetme anı uygular; bu yüzden içeriği değiştiren yeni bir yol da kuralın dışında kalmaz.
Hiçbir şeyi değiştirmeyen bir yazma (aynı değerle `PATCH`, boş `PATCH`) içerik yazmak sayılmaz: onayı düşürmez ve
yazanı `contributors` listesine eklemez.

### Onaylanmış ve reddedilmiş kayıt

- Onaylanmış kayıt güncelleme uçlarından (`PUT`, `PATCH`, toplu güncelleme, güncelleme önizlemesi) değiştirilemez:

  ```json
  {
    "title": "Not allowed in the current state",
    "status": 409,
    "detail": "'update' is not allowed while the record is in the 'Approved' state."
  }
  ```

- Reddedilmiş kayıt düzenlenebilir. Düzenlenince yeniden `Pending` olur; düzenleyen artık karar veremez.
- Silme onaya bağlı değildir; onaylanmış kayıt da kaynağın kurallarına göre silinebilir.
- Onaya bağlı olmayan bir aksiyon onaylanmış kaydın içeriğini değiştirebilir; o zaman onay düşer ve aksiyonu
  çalıştıran kişi de karar veremeyenlere eklenir.

### Çok düzeyli onay ve onay isteyen işlemler

`levels` birbirinden farklı kaç kişinin sırayla onaylaması gerektiğini söyler. Ardından sayılan geçişler ve aksiyonlar,
kayıt onaylanana kadar çalışmaz.

<!-- example: purchase-requests -->
```csharp
var requests = app.MapCrud<PurchaseRequest>()
    .Workflow<PurchaseRequestStatus>(initial: PurchaseRequestStatus.Open)
    .Approvable(levels: 2, "order");

requests.MapDetail<PurchaseRequestLine>();

// Onay ister: talep iki kez onaylanmadan siparişe dönüşmez. POST /purchase-requests/{id}/order
requests.MapTransition<OrderPurchase>(from: PurchaseRequestStatus.Open, to: PurchaseRequestStatus.Ordered, name: "order");

// Onay istemez; talebin tutarını değiştirdiği için toplanan onayları düşürür. POST /purchase-requests/{id}/adjust-purchase
requests.MapAction<AdjustPurchase>();

public enum PurchaseRequestStatus
{
    Open,
    Ordered,
}

public partial class PurchaseRequest
{
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";

    [Range(0.0, 10_000_000.0)]
    public decimal Amount { get; set; }

    public string? OrderReference { get; private set; }

    public void Order(string reference) => OrderReference = reference;
}

public partial class PurchaseRequestLine
{
    [Required, MaxLength(200)]
    public string Item { get; set; } = "";

    [Range(1, 10_000)]
    public int Quantity { get; set; }
}

public sealed record OrderPurchase : ITransition<PurchaseRequest>
{
    [Required, MaxLength(50)]
    public string Reference { get; init; } = "";

    public void Apply(PurchaseRequest request) => request.Order(Reference);
}

public sealed record AdjustPurchase : IEntityAction<PurchaseRequest>
{
    [Range(0.0, 10_000_000.0)]
    public decimal Amount { get; init; }

    public void Apply(PurchaseRequest request) => request.Amount = Amount;
}
```

Akış:

1. Ada talebi oluşturur. `POST /purchase-requests/{id}/order` 409 alır: talep onaylı değil.
2. Grace onaylar: `approvalStatus` `Pending`, `approvalLevel` `1`.
3. Grace ikinci kez onaylamaya çalışır: 403. Aynı kişi iki düzeyi onaylayamaz.
4. Linus onaylar: `approvalStatus` `Approved`, `approvalLevel` `2`.
5. `order` artık çalışır. Geçişin yazdığı `orderReference` onayı düşürmez: onayın izin verdiği işlemin kendi yazdıkları
   onaydan muaftır.

Onay beklerken onay isteyen bir işlem çağrılırsa:

```json
{
  "title": "Not allowed in the current state",
  "status": 409,
  "detail": "'order' is not allowed while the record is in the 'Pending' state."
}
```

İçerik ikinci onaydan önce değişirse düzey sıfırlanır; önceki turda onaylamış olan Grace yeni içeriği yeniden
onaylayabilir. "Aynı kişi iki düzeyi onaylayamaz" kuralı yalnızca o içerik için verilmiş onaylara bakar.

### Eşzamanlı kararlar

Karar, kararı verenin gördüğü içeriğe bağlıdır. Aynı anda gelen bir onay ve bir retten yalnızca biri kaydedilir; öteki
409 alır ve iz bırakmaz. Araya bir içerik yazması girerse karar da 409 ile durur. Kaynak `ConcurrencyChecked` ise aynı
işi satır sürümü görür: `approve` ve `reject` de `If-Match` ister (eksikse 428, eskiyse 412).

### Seçenekler

| Parametre | Varsayılan | Anlamı |
|---|---|---|
| `levels` | `1` | Kaydın onaylanması için gereken, birbirinden farklı kişilerin sırayla vereceği onay sayısı. 1 ile 10 arasında olmalıdır (`HALDUS010`). |
| `requiredFor` | Yok | Kayıt onaylanmadan çalışmayan geçişlerin ve entity aksiyonlarının adları. Kaynakta o adda bir geçiş veya aksiyon yoksa `HALDUS010` olur. |

## Onay zinciri

`.ApprovalRoles(...)` onayın düzeylerini rollere bağlar: n. rol, n. düzeyin kararını (onay veya ret) verebilecek
kullanıcıları belirler. Kaç düzey gerektiği kayda göre değişecekse entity `IApprovalLevels` uygular.

<!-- example: budget-requests -->
```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHaldus<AppDbContext>();

// Roller varsayılan olarak token'daki rol claim'lerinden okunur; kendi kaynağınız varsa servisi değiştirin.
builder.Services.AddSingleton<ICurrentUserRoles, DirectoryRoles>();

var app = builder.Build();

// Birinci düzeye yönetici, ikinciye finans karar verir.
app.MapCrud<BudgetRequest>()
    .Approvable(levels: 2)
    .ApprovalRoles("manager", "finance");
```

<!-- example: budget-requests -->
```csharp
/// <summary>Küçük talep yalnızca yöneticinin onayıyla geçer; büyüğü finansın onayını da ister.</summary>
public partial class BudgetRequest : IApprovalLevels
{
    public const decimal FinanceThreshold = 10_000m;

    [Required, MaxLength(200)]
    public string Purpose { get; set; } = "";

    [Range(0.0, 10_000_000.0)]
    public decimal Amount { get; set; }

    public int RequiredApprovalLevels => Amount > FinanceThreshold ? 2 : 1;
}

/// <summary>Rolleri token'daki "app_role" claim'lerinden okur.</summary>
public sealed class DirectoryRoles(IHttpContextAccessor accessor) : ICurrentUserRoles
{
    public bool IsInRole(string role) => accessor.HttpContext?.User.HasClaim("app_role", role) == true;
}
```

### Kurallar

- Karar, onay da ret de, sıradaki düzeyin rolüne sorulur. Birinci düzeyde yalnızca `manager` rolündekiler, ikinci
  düzeyde yalnızca `finance` rolündekiler karar verir; rolde olmayan 403 alır:

  ```json
  {
    "title": "Not allowed to decide",
    "status": 403,
    "detail": "This level of the approval must be decided by a user who holds the role declared for it."
  }
  ```

- Rol, öteki kuralların yerine geçmez; onlardan sonra denetlenir. İçeriği yazan kişi rolü taşısa da karar veremez;
  iki rolü de taşıyan kişi iki düzeyi birden onaylayamaz.
- Rol sayısı `Approvable(levels: n)` ile bildirilen düzey sayısına eşit olmalıdır; değilse `HALDUS010` olur.
  `ApprovalRoles` `Approvable` olmadan yazılamaz (`HALDUS010`); boş rol adı da `HALDUS010`'dur. Zincirdeki sırası önemli
  değildir.
- Roller `ICurrentUserRoles` servisine sorulur. Varsayılan uygulama `ClaimsPrincipal.IsInRole` kullanır, yani kimlik
  doğrulamanın kurduğu rol claim'lerine bakar. Kendi rol kaynağınız varsa (bir dizin, bir veritabanı tablosu)
  servisi değiştirin; Haldus kendininkini `TryAdd` ile kaydeder ve servisi her kararda isteğin kapsamından çözer.

### Koşullu düzey

Entity `IApprovalLevels` uygularsa kaç düzey gerektiğini kayıt söyler (`RequiredApprovalLevels`). Değer her kararda
kayıttan yeniden okunur ve 1 ile bildirilen düzey sayısı arasına kırpılır.

- 400 tutarlı bir talep yöneticinin onayıyla `Approved` olur; finans düzeyi hiç istenmez.
- 250 000 tutarlı bir talep yöneticinin onayından sonra `Pending` kalır (`approvalLevel: 1`); finansın onayıyla
  `Approved` olur.

Kayıt onaylandıktan sonra tutarı eşiğin üstüne çıkarılamaz: onaylanmış kayıt güncelleme uçlarından değişmez (409).
Reddedilmiş bir talep düzenlenip tutarı büyütülürse yeniden karar bekler ve artık iki düzey ister.

`RequiredApprovalLevels` herkese açık bir okuma özelliği olduğu için yanıtta da görünür
(`"requiredApprovalLevels": 2`); istemci kaç onay beklendiğini buradan okur.

## Belge

`.Document()` kaydı sabit bir yaşam döngüsü olan bir belge yapar: taslak, gönderilmiş, iptal edilmiş ve düzeltilmiş.
Durumları ve kuralları Haldus'a aittir; `Workflow` yazmazsınız.

<!-- example: contracts -->
```csharp
app.MapCrud<Contract>()
    .Document()
    .Numbered("CTR-{seq:4}", assignOn: DocumentStatus.Submitted);

/// <summary>Tedarik sözleşmesi. Düzeltildiğinde yeni taslağın revizyonu bir artar.</summary>
public partial class Contract : IAmendment<Contract>
{
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";

    [Range(0.0, 10_000_000.0)]
    public decimal Value { get; set; }

    public int Revision { get; private set; } = 1;

    public void Amend(Contract cancelled) => Revision = cancelled.Revision + 1;
}
```

### Durumlar ve kurallar

Durum alanı `Status`'tur; tipi `Haldus.DocumentStatus`'tır.

| Durum | Düzenlenir (`PUT`, `PATCH`) | Silinir | Geçişler |
|---|---|---|---|
| `Draft` | Evet | Evet | `submit` |
| `Submitted` | Hayır (409) | Hayır (409) | `cancel` |
| `Cancelled` | Hayır (409) | Hayır (409) | `amend` |

Bir taslak iptal edilmez (409); taslaktan vazgeçmek için silinir. Liste duruma göre süzülür:
`GET /contracts?status=Cancelled`.

Üretilen alanlar:

| Alan | Anlamı |
|---|---|
| `Status` | `Draft`, `Submitted` veya `Cancelled`; yanıtta adıyla yazılır, istekle yazılamaz. |
| `AmendedFromId` | Bu taslak bir düzeltmeyse, düzelttiği iptal edilmiş belge. |
| `AmendedById` | Bu belge düzeltildiyse, onun yerine hazırlanan taslak. |

### Üretilen uçlar

| Yöntem ve adres | Ne yapar |
|---|---|
| `POST /contracts/{id}/submit` | `Draft` → `Submitted`. Numara bildirildiyse burada atanır. |
| `POST /contracts/{id}/cancel` | `Submitted` → `Cancelled`. |
| `POST /contracts/{id}/amend` | İptal edilmiş belgeden yeni bir taslak oluşturur; 201 döner. |

`submit` ve `cancel` kendiliğinden gelir. Standart uçlar (`GET`, `POST`, `PUT`, `PATCH`, `DELETE`) da vardır.

```http
POST /contracts/0196f3f0-2a3b-7c4d-8e5f-6a7b8c9d0e1f/submit
```

```json
{
  "id": "0196f3f0-2a3b-7c4d-8e5f-6a7b8c9d0e1f",
  "title": "Paper supply",
  "value": 1200,
  "revision": 1,
  "status": "Submitted",
  "number": "CTR-0001",
  "amendedFromId": null,
  "amendedById": null
}
```

### Düzeltme

`POST /{id}/amend` yalnızca iptal edilmiş bir belgede çalışır. Gövde isteğe bağlıdır ve `PATCH` gibi okunur:
gönderilen alanlar yeni taslakta eskisindekinin yerine geçer, gönderilmeyenler kopyalanır.

```http
POST /contracts/0196f3f0-2a3b-7c4d-8e5f-6a7b8c9d0e1f/amend
Content-Type: application/json

{ "value": 1350 }
```

```http
HTTP/1.1 201 Created
Location: /contracts/0196f3f4-9b8a-7d6c-8b5a-4f3e2d1c0b9a
```

```json
{
  "id": "0196f3f4-9b8a-7d6c-8b5a-4f3e2d1c0b9a",
  "title": "Paper supply",
  "value": 1350,
  "revision": 2,
  "status": "Draft",
  "number": null,
  "amendedFromId": "0196f3f0-2a3b-7c4d-8e5f-6a7b8c9d0e1f",
  "amendedById": null
}
```

Yeni taslak şu sırayla kurulur:

1. İptal edilmiş belgenin yazılabilir alanları ve gömülü satırları kopyalanır. Kimlik, durum (`Draft`), numara ve zaman
   damgaları yeni kayıttaki gibi atanır.
2. Entity `IAmendment<T>` uyguluyorsa `Amend(cancelled)` çağrılır. Kopyalanmayan alanlar (revizyon numarası gibi
   herkese açık setter'ı olmayanlar) burada yazılır.
3. Gövdede gönderilen alanlar yazılır.
4. Yeni kayıt oluşturma ucunun kontrollerinden geçer ve kaydedilir; eski belgenin `amendedById` alanı yeni taslağı
   gösterir.

Kurallar:

- Taslak veya gönderilmiş belge düzeltilemez (409: `'amend' is not allowed while the record is in the 'Draft' state.`).
- Bir belge yalnızca bir kez düzeltilir. Düzeltilmiş belge artık değiştirilemez ve yeniden düzeltilemez; ikinci düzeltme
  ve aynı anda gelen iki düzeltmeden ikincisi 409 alır:

  ```json
  {
    "title": "Record can no longer be changed",
    "status": 409,
    "detail": "Contract '0196f3f0-2a3b-7c4d-8e5f-6a7b8c9d0e1f' has been amended and can no longer be changed or amended again."
  }
  ```

- Bir düzeltme çiftinin parçası olan kayıt silinemez; düzeltme taslağı da. Vazgeçilen bir düzeltme gönderilip iptal
  edilir.
- Düzeltme kendi yaşam döngüsünden geçer: gönderilince kendi numarasını alır, eski belgenin numarasını devralmaz.

### Gönderme ve iptale mantık eklemek

`submit` veya `cancel` için iş mantığı gerekiyorsa aynı adla bir `MapTransition` yazın; kendiliğinden gelen geçişin
yerine geçer. Yazmadığınız öteki geçiş kendiliğinden gelmeye devam eder.

<!-- example: contract-submit -->
```csharp
var contracts = app.MapCrud<Contract>()
    .Document()
    .Numbered("CTR-{seq:4}", assignOn: DocumentStatus.Submitted);

contracts.MapTransition("submit", from: DocumentStatus.Draft, to: DocumentStatus.Submitted, contract =>
{
    if (contract.Value <= 0)
    {
        throw new TransitionRejectedException("A contract without a value cannot be submitted.");
    }
});

public partial class Contract
{
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";

    [Range(0.0, 10_000_000.0)]
    public decimal Value { get; set; }
}
```

Reddedilen gönderme 422 alır ve numara harcamaz. Geçişlerin mantığı: [İş mantığı](business-logic.md).

### Belgenin birleşmedikleri

| Bildirim | Tanı |
|---|---|
| `Workflow`, `EditableIn`, `DeletableIn` | `HALDUS007`: durumları `Document` belirler. |
| `MapAppendOnly`, `MapReadOnly` ve öteki archetype'lar | `HALDUS007`: yalnızca `MapCrud`. |
| `Without(CrudOperation.Create)` | `HALDUS007`: belge oluşturma ucu ister. |
| Veritabanının ürettiği anahtar (`int` / `long` `Id`) | `HALDUS007`: düzeltmenin anahtarı kaydetmeden önce bilinmelidir. |
| `amend` adlı bir aksiyon veya geçiş | `HALDUS007`: üretilen uçla çakışır. |

## Birlikte kullanım

| Bildirim | İlişkisi |
|---|---|
| `Workflow(...)` | `Approvable` ile birleşir; onay durumu ve kaydın durumu ayrı alanlardır. Onay geçişleri durdurmaz, yalnızca `requiredFor` ile sayılanları durdurur. |
| `MapTransition`, `MapAction` | `Approvable(levels, "ad")` ile onaya bağlanır; onaya bağlı olmayanlar içeriği değiştirirse onay düşer. |
| `MapDetail<T>()` | Gömülü satırlar içeriktir; değişmeleri onayı düşürür. Onaya bağlı kaynağın ayrı uçlu detayı olamaz (`AsSubresource`, `HALDUS007`): satırlar sahibinden bağımsız yazılırdı. |
| `ConcurrencyChecked()` | `approve` ve `reject` `If-Match` ister. |
| `Numbered(assignOn: DocumentStatus.Submitted)` | Belge numarasını gönderilirken alır. |
| `Revisioned()` | Bir sürüme geri yükleme bir güncellemedir; onayı düşürür. |
| `MultiTenant()`, `OwnedByCurrentUser()` | Kararlar kaydın kiracısıyla eşlenir; başka kiracının kaydına karar verilemez (404). |
| `Publishable()` | `Approvable` ve `Document` ile birleşmez (`HALDUS007`). |
| `MapStore`, `MapSingleton`, `MapLookup` | `Approvable` ile birleşmez (`HALDUS007`). `Approvable` `MapCrud`, `MapAppendOnly` ve `MapReadOnly` ile kullanılır. |
| `MapAction("approve", ...)` | `approve`, `reject` ve `approvals` adları onaya bağlı kaynakta kullanılamaz (`HALDUS007`). |

## Sınırlar

- Onayı geri almak yoktur: verilmiş bir karar ancak içerik değişince geçersiz olur.
- Bir düzeyin birden çok rolü, paralel onay ("iki kişiden biri yeter"), vekâlet ve bir düzeyin atlanması yoktur.
- Rol bildirilmediyse onaylayanın yetkisi denetlenmez; yalnızca "içeriği yazmamış" ve "aynı kişi değil" kuralları
  uygulanır. Kimin karar verebileceğini sınırlamak için `ApprovalRoles` veya uç düzeyinde yetkilendirme kullanın.
- Silme onaya bağlı değildir.
- Değişiklik izleyicisini atlayan yazmalar (`ExecuteUpdate`, `ExecuteDelete`) onayı düşürmez.
- Kayıt silindiğinde kararları kalır; kendiliğinden temizlenmez.
- Hata mesajı (`Approval.OwnRequest`) "en son siz yazdınız" der; kural ise içeriği yazmış herkesi kapsar.
- Belgenin durumları sabittir; `Draft`, `Submitted` ve `Cancelled` dışında durum eklenemez. Ek bir durum gerekiyorsa
  [ek durum eksenleri](state-machines.md#ek-durum-eksenleri) veya `Workflow` kullanın.

## Ayrıca bakın

- [Durum makineleri](state-machines.md)
- [İş mantığı](business-logic.md)
- [Kayıtlar arası zincir](record-chains.md): ters kayıt, yerine geçen kayıt ve başka bir kayıttan üretmek.
- [Sürümler ve yayın](history-and-publishing.md)
- [Bildirim dizini](../reference/declarations.md#onay)
- [Tanılar](../reference/diagnostics.md)
