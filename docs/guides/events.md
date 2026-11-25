# Olay yayını

Bir sipariş oluşunca depo sistemine haber vermek, bir destek kaydı kapanınca arama dizinini güncellemek, bir müşteri
silinince faturalama servisinden kaldırmak: başka sistemler kayıtlarınızın değiştiğini öğrenmek ister. Değişikliği
kaydettikten sonra elle bir mesaj göndermek iki yerde yarım kalabilir: kayıt kaydedilir ama mesaj gitmez, ya da mesaj
gider ama kayıt geri alınır. `.PublishesEvents()` bunu bir "outbox" ile çözer: olay kayıtla aynı kaydetmede bir
bekleme tablosuna yazılır ve kayıt kalıcı olduktan sonra, sırayla, sizin seçtiğiniz yayıncılara teslim edilir.

Örneklerde web SDK'nın örtük `using`'leri, `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs`'tir; `builder` ve `app`
satırları gösterilmediyse yazılmış sayılır.

## İçindekiler

- [Olayları açmak](#olayları-açmak)
- [Olay türleri](#olay-türleri)
- [Olayın içeriği](#olayın-içeriği)
- [Outbox ve teslimat](#outbox-ve-teslimat)
- [Kendi yayıncınız](#kendi-yayıncınız)
- [Webhook](#webhook)
- [Amazon SQS](#amazon-sqs)
- [RabbitMQ](#rabbitmq)
- [Kafka](#kafka)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Olayları açmak

Olaylar kaynak başına açılır. Kaynak olay yazar; olayları uygulamanın dışına bir **yayıncı** taşır. Yayıncıyı
uygulama kaydeder: hazır olanlardan biri (webhook, SQS, RabbitMQ, Kafka) ya da kendi yazdığınız bir
`IHaldusEventPublisher`.

<!-- example: tickets -->
```csharp
builder.Services.AddHaldusWebhookPublisher(options =>
    options.Endpoints.Add(new HaldusWebhookEndpoint(
        new Uri("https://hooks.example.com/haldus"),
        builder.Configuration["Webhooks:Secret"]!)));

var app = builder.Build();

var tickets = app.MapCrud<Ticket>()
    .Workflow<TicketStatus>(initial: TicketStatus.Open)
    .EditableIn(TicketStatus.Open)
    .PublishesEvents();

tickets.MapTransition("close", from: TicketStatus.Open, to: TicketStatus.Closed);

public enum TicketStatus
{
    Open,
    Closed,
}

public partial class Ticket
{
    [Required]
    [MaxLength(200)]
    public string Subject { get; set; } = "";
}
```

`.PublishesEvents()` uç üretmez ve entity'ye alan eklemez. Olayların tablosu (`HaldusOutbox`) yalnızca olay yayınlayan
bir kaynak varsa modele girer; migration'larınızda görünür.

## Olay türleri

Olayın türü `{kaynak}.{ne oldu}` biçimindedir. Kaynak adı entity adının kebab-case halidir: `Ticket` → `ticket`,
`StockMovement` → `stock-movement`.

| Tür | Ne zaman |
|---|---|
| `ticket.created` | Kayıt eklendi. |
| `ticket.updated` | Kayıt değişti: `PUT`, `PATCH`, bir geçiş, bir aksiyon, geri yükleme... |
| `ticket.deleted` | Kayıt silindi. Geri alınabilir silmede (`SoftDeletable`) de türü `deleted`'dır; geri yükleme `updated`'dır. |
| `ticket.close` | `close` geçişi çalıştı. Geçişin adı olduğu gibi kullanılır; geçmiş zamana çevrilmez. |

Bir geçiş iki olay üretir: kaydın değişmesi (`ticket.updated`) ve geçişin kendisi (`ticket.close`), bu sırayla. Entity
aksiyonları (`MapAction`) ayrı bir olay üretmez; yalnızca `updated` yazılır. İstek tipinden adlandırılan bir geçişin
olayı da türetilen adı taşır: `MapTransition<BillTicket>()` → `ticket.bill`.

Bir destek kaydının hayatı şu olayları verir:

| İstek | Olaylar |
|---|---|
| `POST /tickets` | `ticket.created` |
| `PATCH /tickets/{id}` | `ticket.updated` |
| `POST /tickets/{id}/close` | `ticket.updated`, `ticket.close` |
| `DELETE /tickets/{id}` | `ticket.deleted` |

Reddedilen istek olay üretmez: geçersiz gövde (400), uymayan durum (409) veya geri alınan bir transaction hiçbir şey
yazmaz.

## Olayın içeriği

Yayıncı her olayı bir `HaldusEvent` olarak alır. Hazır yayıncılar onu JSON'a (camelCase) çevirerek gönderir:

```json
{
  "id": 1842,
  "type": "ticket.close",
  "resource": "ticket",
  "recordId": "01936fb0-4d5e-7f6a-8b7c-9d0e1f2a3b4c",
  "tenantId": null,
  "userId": "ada",
  "data": {
    "id": "01936fb0-4d5e-7f6a-8b7c-9d0e1f2a3b4c",
    "subject": "Yazıcı çalışmıyor",
    "status": "Closed"
  },
  "occurredAt": "2026-11-25T13:00:00.551203+00:00"
}
```

| Alan | Tip | Anlamı |
|---|---|---|
| `Id` | `long` | Olayın sırası; aynı veritabanındaki olaylar arasında artar. Yinelenen teslimatı ayırt etmek için kullanılır. |
| `Type` | `string` | Olayın türü: `ticket.created`. |
| `Resource` | `string` | Kaydın kaynağı: `ticket`. |
| `RecordId` | `string` | Kaydın anahtarı, metin olarak. |
| `TenantId` | `Guid?` | Kayıt kiracıya aitse kiracısı. |
| `UserId` | `string?` | Değişikliği yapan kullanıcı; bilinmiyorsa boş. |
| `Data` | `JsonElement` | Kaydın, olayın yazıldığı andaki hali. |
| `OccurredAt` | `DateTimeOffset` | Olayın yazıldığı an. |

**Veri, kaydın yanıt biçimidir** ("kalın" olay): `GET /tickets/{id}` ne döndürüyorsa olay da onu taşır, enum'lar
adıyla. Yanıtta olmayan bir alan olayda da yoktur: hassas alanlar, sırlar, depo anahtarları dışarı çıkmaz. Silme
olayının verisi kaydın silinmeden önceki son halidir.

Olay, kaydın bağlamda yüklü halini taşır. Kaydı uygulamanın kendi koduyla, gömülü satırlarını yüklemeden yazarsanız
satırlar olayda eksik görünür.

## Outbox ve teslimat

**Yazma.** Olay, kayıtla aynı `SaveChanges` çağrısında `HaldusOutbox` tablosuna yazılır. Kayıt kalıcı olmadıysa olay
da yoktur; olduysa olay kaybolmaz. Kaydı hangi kod yazarsa yazsın olay yazılır: üretilen uç, bir arka plan işi,
uygulamanın kendi kodu. Bunu `SaveChanges` sırasında çalışan bir interceptor yapar.

**Teslimat.** Arka plandaki bir servis bekleyen olayları yazıldıkları sırayla (olay kimliğine göre) bütün
yayıncılara verir ve teslim edileni tablodan siler.

- Aynı süreçte yazılan olay beklemeden teslim edilir. Başka bir sunucunun yazdığı veya açık bir transaction'ın içinde
  yazılan olaylar en geç `HaldusOptions.EventPollInterval` (varsayılan 1 saniye) sonra görülür.
- Aynı anda tek bir dağıtıcı çalışır: birden çok sunucu varsa da olaylar bir veritabanı kilidiyle tek sıradan çıkar.
- Bir tur en çok 100 olay teslim eder; tur doluysa beklemeden yenisi başlar.

**Garantiler.**

| Garanti | Anlamı |
|---|---|
| En az bir kez | Yayıncı hata fırlatırsa olay sonra yeniden denenir. Bir yayıncı olayı göndermiş ama başarısını bildirememişse (bağlantı koptu) olay ikinci kez gelir. Tüketici yineleneni `HaldusEvent.Id` ile ayıklamalıdır. |
| Sıra | Bir olay teslim edilemezse arkasındakiler de bekler; sıra bozulmaz. Aynı kaydın olayları her zaman sıralıdır. Aynı anda commit edilen farklı transaction'ların olayları arasında sıra garanti edilmez. |
| Vazgeçme | Olay `HaldusEvents.MaxAttempts` (varsayılan 10) denemeden sonra bırakılır: tabloda kalır, `DeadAt` ve son hatası (`LastError`) yazılır, sıradakilere geçilir. |

Başarısız denemeler arasındaki bekleme 2, 4, 8... saniyedir ve en çok beş dakikadır. Her başarısız deneme günlüğe bir
uyarı yazar.

**Birden çok yayıncı.** Her olay kayıtlı bütün yayıncılara, kayıt sırasıyla verilir ve ancak hepsi başarılı olursa
teslim edilmiş sayılır. Biri hata fırlatırsa yeniden denemede olay, başarılı olmuş olanlara da yeniden gider.

**Yayıncı yoksa.** Olaylar yine yazılır ama teslim edilmez ve silinmez; bir yayıncı kaydedilene kadar tabloda birikir.
Uygulama açılırken bunun için bir uyarı yazılır.

> [!IMPORTANT]
> Teslimat, veritabanı kilidi ve transaction tutulurken yapılır. Yavaş bir yayıncı bütün olayların teslimatını
> yavaşlatır. Yayıncınızın bir zaman aşımı olmalıdır.

### Ayarlar

<!-- example: event-options -->
```csharp
builder.Services.AddHaldus<AppDbContext>(options => options.EventPollInterval = TimeSpan.FromSeconds(5));
HaldusEvents.MaxAttempts = 20;

var app = builder.Build();

app.MapCrud<Ticket>().PublishesEvents();

public partial class Ticket
{
    [MaxLength(200)]
    public string Subject { get; set; } = "";
}
```

| Ayar | Varsayılan | Anlamı |
|---|---|---|
| `HaldusOptions.EventPollInterval` | 1 saniye | Başka sunuculardan gelen olaylar için bekleme tablosuna en geç ne sıklıkla bakılır. |
| `HaldusEvents.MaxAttempts` | 10 | Bir olaydan vazgeçilmeden önceki en çok deneme. Süreç genelinde statik bir ayardır. |

### Elle teslim

Dağıtıcı bir test veya yönetim komutundan doğrudan çağrılabilir; bir tur teslim eder ve teslim edilen olay sayısını
döndürür. Kayıtlı yayıncı yoksa hiçbir olaya dokunmaz ve 0 döner.

<!-- fragment -->
```csharp
var delivered = await HaldusEventDispatcher.RunAsync(app.Services, cancellationToken);
```

Vazgeçilen olaylar kendiliğinden yeniden kuyruğa alınmaz. Onları yeniden denemek için `HaldusOutbox` tablosundaki
satırın `DeadAt` alanını boşaltın ve `Attempts` alanını sıfırlayın.

## Kendi yayıncınız

`IHaldusEventPublisher` tek bir metottur. Yayıncı tekil (singleton) servis olarak kaydedilir.

<!-- example: custom-publisher -->
```csharp
using System.Text.Json;

builder.Services.AddSingleton<ISearchIndex, SearchIndex>();
builder.Services.AddSingleton<IHaldusEventPublisher, SearchIndexPublisher>();

var app = builder.Build();

app.MapCrud<Article>().PublishesEvents();

public partial class Article
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";
}

public interface ISearchIndex
{
    Task UpsertAsync(string id, JsonElement document, CancellationToken cancellationToken);

    Task RemoveAsync(string id, CancellationToken cancellationToken);
}

public sealed class SearchIndex : ISearchIndex
{
    public Task UpsertAsync(string id, JsonElement document, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RemoveAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
}

// Dizine yazmak ve dizinden silmek tekrarlanabilir işlemlerdir; aynı olayın iki kez gelmesi sonucu değiştirmez.
public sealed class SearchIndexPublisher(ISearchIndex index) : IHaldusEventPublisher
{
    public Task PublishAsync(HaldusEvent published, CancellationToken cancellationToken) => published.Type switch
    {
        "article.deleted" => index.RemoveAsync(published.RecordId, cancellationToken),
        "article.created" or "article.updated" => index.UpsertAsync(published.RecordId, published.Data, cancellationToken),
        _ => Task.CompletedTask,
    };
}
```

Kurallar:

- Hata fırlatmak "teslim edilemedi" demektir: olay kutuda kalır ve yeniden denenir. Başarıyla dönmek "teslim edildi"
  demektir: olay silinir. Göndermediğiniz bir olay için başarıyla dönmeyin.
- Yayıncı en az bir kez teslime dayanıklı olmalıdır. Hedef yineleneni kendisi ayıklamıyorsa `HaldusEvent.Id`'yi
  gönderin ya da saklayın.
- Bütün olaylar tek sıradan geçer. İlgilenmediğiniz türleri hemen başarıyla geçin; yavaş bir çağrı herkesi bekletir.
- Yayıncınızın zaman aşımı `OperationCanceledException` fırlatırsa da sorun olmaz: dağıtıcı onu, kendi iptal
  belirteci tetiklenmediyse, başarısız bir deneme sayar.

## Webhook

`AddHaldusWebhookPublisher` olayları yapılandırılmış adreslere imzalı HTTP istekleriyle teslim eder. Çekirdek pakette
(`Haldus.AspNetCore`) gelir; ayrı bir paket gerekmez.

<!-- example: webhook -->
```csharp
builder.Services.AddHaldusWebhookPublisher(options =>
{
    options.Endpoints.Add(new HaldusWebhookEndpoint(
        new Uri("https://warehouse.example.com/hooks/haldus"),
        builder.Configuration["Webhooks:Warehouse:Secret"]!,
        "order.*"));

    options.Endpoints.Add(new HaldusWebhookEndpoint(
        new Uri("https://billing.example.com/hooks/haldus"),
        builder.Configuration["Webhooks:Billing:Secret"]!,
        "order.created", "order.cancel"));

    options.Timeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();

app.MapCrud<Order>().PublishesEvents();

public partial class Order
{
    [MaxLength(100)]
    public string CustomerName { get; set; } = "";
}
```

`HaldusWebhookEndpoint(url, secret, eventTypes...)`:

| Parametre | Anlamı |
|---|---|
| `url` | Mutlak bir `https://` adresi. |
| `secret` | İmzanın anahtarı; alıcıyla paylaşılır. En az 16 bayt. `whsec_` ile başlıyorsa önekten sonrası base64 olarak çözülür ve çözülmüş hali en az 16 bayt olmalıdır. |
| `eventTypes` | Gönderilecek türler: tam ad (`order.created`) veya bir kaynağın bütün olayları (`order.*`). Boşsa bütün olaylar. |

| `HaldusWebhookOptions` | Varsayılan | Anlamı |
|---|---|---|
| `Endpoints` | boş | Adresler. |
| `Timeout` | 10 saniye | Bir teslimatın en uzun süresi. |
| `AllowInsecureHttp` | `false` | `http://` adreslerine izin verir; yalnızca yerel denemeler içindir. |

Ayarlar uygulama açılırken doğrulanır: göreli bir adres, `http://` (izin verilmeden) veya kısa bir anahtar uygulamayı
açtırmaz.

### İstek

Her olay, adres başına bir `POST` isteğidir. Gövde olayın JSON halidir ([Olayın içeriği](#olayın-içeriği)):

```http
POST /hooks/haldus HTTP/1.1
Host: warehouse.example.com
Content-Type: application/json
Webhook-Id: 1842
Webhook-Timestamp: 1795611600
Webhook-Signature: v1,K5oZfzN95Z9UVu1EsfQmfVNQhnkZ2pj9o9NDN/H/pI4=

{"id":1842,"type":"order.created","resource":"order","recordId":"01936fc0-…","tenantId":null,"userId":"ada","data":{…},"occurredAt":"2026-11-25T13:00:00.551203+00:00"}
```

| Başlık | Değer |
|---|---|
| `Webhook-Id` | Olayın kimliği (`HaldusEvent.Id`). Yinelenen teslimatı tanımak için kullanılır. |
| `Webhook-Timestamp` | İsteğin gönderildiği an, Unix saniyesi. |
| `Webhook-Signature` | `v1,{base64}`: `{id}.{timestamp}.{gövde}` metninin HMAC-SHA256 imzası. |

İmza [Standard Webhooks](https://www.standardwebhooks.com) düzenindedir; anahtar `whsec_…` biçimindeyse o belirtimin
hazır doğrulayıcıları da imzayı kabul eder.

- Alıcı 2xx dışında bir yanıt verirse ya da ulaşılamazsa olay yeniden denenir. Yanıtın gövdesi okunmaz.
- Yönlendirme (3xx) izlenmez ve başarısızlık sayılır: imzalı gövde, adresin gösterdiği başka bir yere gitmemelidir.
- Günlüğe ve kutunun hata alanına yalnızca sunucunun adı yazılır; adresin yolu ve sorgusu bir anahtar taşıyabilir.
- Birden çok adres varsa ve biri başarısız olursa, yeniden denemede olay başarılı olan adreslere de yeniden gider. Adres
  başına ayrı bir kuyruk ve devre kesici yoktur: sürekli hata veren bir adres bütün olayları bekletir.

### Alıcı tarafında doğrulama

Alıcı imzayı `HaldusWebhookSignature.Verify` ile, gövdenin ham baytları üzerinden doğrular. Gövdeyi önce JSON olarak
okuyup yeniden yazmayın: imza tam o baytlar içindir. Aşağıdaki alıcı da bir Haldus uygulamasıdır ve kayıtların bir
kopyasını tutar:

<!-- example: webhook-receiver -->
```csharp
using System.Text.Json;

var app = builder.Build();

app.MapReadOnly<TicketCopy>();

app.MapPost("/hooks/haldus", async (HttpRequest request, IConfiguration configuration, AppDbContext db, CancellationToken cancellationToken) =>
{
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer, cancellationToken);
    var body = buffer.ToArray();

    var valid = HaldusWebhookSignature.Verify(
        configuration["Webhooks:Secret"]!,
        request.Headers[HaldusWebhookSignature.IdHeader].ToString(),
        request.Headers[HaldusWebhookSignature.TimestampHeader].ToString(),
        body,
        request.Headers[HaldusWebhookSignature.SignatureHeader].ToString(),
        DateTimeOffset.UtcNow,
        tolerance: TimeSpan.FromMinutes(5));

    if (!valid)
    {
        return Results.Unauthorized();
    }

    var published = JsonSerializer.Deserialize<HaldusEvent>(body, JsonSerializerOptions.Web)!;
    var copy = await db.Set<TicketCopy>().FirstOrDefaultAsync(item => item.SourceId == published.RecordId, cancellationToken);

    // Aynı olay birden çok kez gelebilir; daha önce işlenmiş bir olay yok sayılır.
    if (copy is not null && copy.LastEventId >= published.Id)
    {
        return Results.NoContent();
    }

    if (copy is null)
    {
        copy = new TicketCopy { Id = Guid.CreateVersion7(), SourceId = published.RecordId };
        db.Add(copy);
    }

    copy.Subject = published.Data.GetProperty("subject").GetString() ?? "";
    copy.Deleted = published.Type == "ticket.deleted";
    copy.LastEventId = published.Id;

    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
});

public partial class TicketCopy
{
    [MaxLength(200)]
    public string SourceId { get; set; } = "";

    [MaxLength(200)]
    public string Subject { get; set; } = "";

    public bool Deleted { get; set; }

    public long LastEventId { get; set; }
}
```

`Verify` şunları denetler:

- Zaman damgası `tolerance` kadardan eski veya ileride değil (ele geçirilmiş bir istek sonradan yeniden
  gönderilemesin).
- İmza, verilen anahtarla hesaplanan imzayla aynı. Karşılaştırma sabit sürelidir.
- `Webhook-Signature` başlığı boşlukla ayrılmış birden çok imza taşıyabilir; biri uyarsa yeter.

Anahtar değiştirilirken gönderen tarafta adresin anahtarını yenisiyle değiştirirsiniz; alıcı bir süre iki anahtarı
da dener (`Verify`'ı önce yeni, sonra eski anahtarla çağırır).

Alıcının uç bir oturum beklemez; uygulamanızın varsayılan yetki kuralı uçları kapatıyorsa bu ucu `AllowAnonymous()` ile
açın. Kimliği imza doğrular.

### SSRF uyarısı

> [!WARNING]
> Webhook adresleri uygulamanın yapılandırmasından gelmelidir. Kullanıcıların tanımladığı adreslere (bir
> `WebhookEndpoint` kaynağı gibi) istek göndermek sunucunuzu iç ağdaki adreslere istek atmaya zorlayabilir
> (SSRF: `http://169.254.169.254/`, `http://localhost:5432/`...). Bu yayıncı bunu engellemez; yalnızca
> yönlendirmeleri izlemez. Kullanıcı adresine gönderim gerekiyorsa adresi çözümleyip özel ve yerel ağ adreslerini
> reddeden kendi yayıncınızı yazın.

## Amazon SQS

`Haldus.Events.Sqs` paketi olayları bir Amazon SQS kuyruğuna (veya SQS uyumlu bir servise) gönderir; servisle resmi
AWS SDK'sı (`AWSSDK.SQS`) konuşur.

<!-- example: sqs -->
```csharp
builder.Services.AddHaldusSqsPublisher(options =>
{
    options.Region = "eu-central-1";
    options.QueueUrl = new Uri("https://sqs.eu-central-1.amazonaws.com/123456789012/shop-events.fifo");
    options.EventTypes.Add("order.*");
});

var app = builder.Build();

app.MapCrud<Order>().PublishesEvents();

public partial class Order
{
    [MaxLength(100)]
    public string CustomerName { get; set; } = "";
}
```

| Seçenek | Varsayılan | Anlamı |
|---|---|---|
| `QueueUrl` | `null` | Kuyruğun adresi; zorunludur. Kuyruk var olmalıdır; Haldus kuyruk oluşturmaz. Adı `.fifo` ile biten kuyruk FIFO sayılır. |
| `Region` | `"us-east-1"` | Bölge; kuyruğun bölgesi olmalıdır. |
| `ServiceUrl` | `null` | SQS uyumlu servisler ve öykünücüler için adres (`http://localhost:9324`); yol içeremez. Boşsa AWS'nin bölgedeki adresi. |
| `AccessKey`, `SecretKey` | `""` | İkisi birlikte verilir. İkisi de boşsa kimlik bilgisi SDK'nın varsayılan zincirinden okunur (ortam değişkenleri, profil, kapsayıcı veya sunucu rolü). |
| `SessionToken` | `null` | Geçici kimlik bilgisinin oturum token'ı. |
| `EventTypes` | boş | Gönderilecek türler (`order.created`, `order.*`); boşsa hepsi. Süzülen olay teslim edilmiş sayılır. |
| `Timeout` | 10 saniye | Bir gönderimin, SDK'nın yeniden denemeleri dahil en uzun süresi. |
| `ConfigureClient` | `null` | SDK istemcisinin ayarlarını (`AmazonSQSConfig`) Haldus kendi ayarlarını yazdıktan sonra değiştirir. |

Mesaj:

| Parça | Değer |
|---|---|
| Gövde | Olayın JSON hali (webhook gövdesiyle aynı). |
| `EventId` özniteliği (`Number`) | Olayın kimliği. |
| `EventType` özniteliği (`String`) | Olayın türü. |
| `Resource` özniteliği (`String`) | Kaynak adı. |
| `MessageGroupId` (FIFO) | `{kaynak}:{kayıt}`: bir kaydın olayları sırayla tüketilir, başka kayıtlarınki onu beklemez. |
| `MessageDeduplicationId` (FIFO) | `{kaynak}:{kayıt}:{olay}`. |

FIFO kimlikleri 128 karakteri aşarsa veya yazdırılabilir ASCII dışında bir karakter taşırsa (boşluk içeren bir doğal
anahtar gibi) SHA-256 özetiyle yazılır.

Garantiler:

- **FIFO kuyruk** aynı kaydın olaylarını sıralı tutar ve yeniden gönderilen olayı SQS'in beş dakikalık yineleme
  penceresi içinde kendisi ayıklar. Kutunun yeniden deneme aralığı da beş dakikaya çıkabildiği için geç bir yeniden
  deneme pencerenin dışına düşüp yinelenebilir.
- **Standart kuyruk** sırayı korumaz ve yineleneni ayıklamaz; tüketici `EventId` özniteliğine bakmalıdır.
- Başarı ancak servis bir mesaj kimliği döndürürse sayılır; her isteğe 200 dönen yanlış bir adres olayları
  "teslim edilmiş" saydırmaz.
- Servisin reddi `HaldusSqsException` olur: kuyruğun adını, durum kodunu (`StatusCode`) ve hata kodunu (`ErrorCode`)
  taşır; servisin açıklamasını, hesap numarasını ve kimlik bilgisini taşımaz. Olay yeniden denenir.

> [!NOTE]
> SQS yayıncısı bir öykünücüye (ElasticMQ) karşı sınandı. Öykünücü imzayı doğrulamaz; gerçek AWS SQS'e karşı
> sınanmadı.

## RabbitMQ

`Haldus.Events.RabbitMq` paketi olayları bir RabbitMQ exchange'ine gönderir; aracıyla resmi istemci
(`RabbitMQ.Client` 7) konuşur.

<!-- example: rabbitmq -->
```csharp
builder.Services.AddHaldusRabbitMqPublisher(options =>
{
    options.HostName = "rabbit.internal";
    options.UseTls = true;
    options.UserName = builder.Configuration["Rabbit:User"]!;
    options.Password = builder.Configuration["Rabbit:Password"]!;
    options.Exchange = "shop.events";
});

var app = builder.Build();

app.MapCrud<Order>().PublishesEvents();

public partial class Order
{
    [MaxLength(100)]
    public string CustomerName { get; set; } = "";
}
```

| Seçenek | Varsayılan | Anlamı |
|---|---|---|
| `HostName` | `"localhost"` | Aracının adı. TLS'te sertifika bu ada göre doğrulanır. |
| `Port` | `null` | Boşsa protokolün varsayılanı: 5672, TLS ile 5671. |
| `VirtualHost` | `"/"` | |
| `UserName`, `Password` | `"guest"` | Üretimde yapılandırmadan verin. |
| `UseTls` | `false` | Bağlantıyı TLS ile kurar. |
| `Exchange` | `""` | Olayların gönderildiği exchange; var olmalıdır. Boşsa varsayılan exchange kullanılır ve `RoutingKey` hedef kuyruğun adı olmalıdır. |
| `RoutingKey` | `null` | Boşsa olayın türü (`order.created`); bir topic exchange'de tüketiciler türe göre bağlanır (`order.*`). |
| `EventTypes` | boş | Gönderilecek türler; boşsa hepsi. Süzülen olay teslim edilmiş sayılır. |
| `Timeout` | 10 saniye | Bağlantının kurulması dahil, bir gönderimin aracı onaylayana kadar en uzun süresi. |
| `ConfigureConnection` | `null` | Bağlantı ayarlarını (`ConnectionFactory`: sertifika, kalp atışı...) Haldus kendi ayarlarını yazdıktan sonra değiştirir. |

Mesaj kalıcıdır (`persistent`) ve `application/json` türündedir. Özellikleri: `message-id` olayın kimliği, `type`
olayın türü, `timestamp` olayın zamanı; başlıklarda `resource` ve `record-id`.

Garantiler:

- **Aracının onayı beklenir.** Kanal onay izlemeyle açılır: aracı mesajı kabul edene kadar olay teslim edilmiş sayılmaz;
  ret (`nack`) hatadır.
- **Yönlendirilemeyen mesaj hatadır.** Mesaj `mandatory` gönderilir: hiçbir kuyruğa ulaşmayan olay teslim edilmemiş
  sayılır ve kutuda kalır. Bağı unutulmuş bir kuyruk olayları sessizce kaybettirmez; ama bütün olayları bekletir.
- Hatadan sonra kanal ve bağlantı kapatılır; bir sonraki olay yeni bir bağlantıyla başlar. İstemcinin kendiliğinden
  kurtarması kapalıdır.
- Aynı kaydın olaylarının sırası, tek tüketicili bir kuyrukta korunur; birden çok tüketici arasında korunmaz.
- Exchange'i, kuyrukları ve bağları Haldus oluşturmaz.

Kapsayıcıda çalışan gerçek bir aracıya karşı sınandı; TLS dinleyicisi dahil (güvenilmeyen sertifika varsayılan olarak
reddedilir).

## Kafka

`Haldus.Events.Kafka` paketi olayları bir Kafka konusuna yazar; kümeyle resmi istemci (`Confluent.Kafka`) konuşur.

<!-- example: kafka -->
```csharp
using Confluent.Kafka;

builder.Services.AddHaldusKafkaPublisher(options =>
{
    options.BootstrapServers = "kafka-1:9092,kafka-2:9092";
    options.Topic = "shop.events";
    options.ConfigureProducer = config =>
    {
        config.SecurityProtocol = SecurityProtocol.SaslSsl;
        config.SaslMechanism = SaslMechanism.Plain;
        config.SaslUsername = builder.Configuration["Kafka:User"];
        config.SaslPassword = builder.Configuration["Kafka:Password"];
    };
});

var app = builder.Build();

app.MapCrud<Order>().PublishesEvents();

public partial class Order
{
    [MaxLength(100)]
    public string CustomerName { get; set; } = "";
}
```

| Seçenek | Varsayılan | Anlamı |
|---|---|---|
| `BootstrapServers` | `""` | Kümeye ilk bağlanılan adresler; zorunludur. Birden çoksa virgülle ayrılır. |
| `Topic` | `""` | Olayların yazıldığı konu; zorunludur. Var olmalıdır; Haldus konu oluşturmaz. |
| `ClientId` | `"haldus-events"` | İstemcinin adı. |
| `EventTypes` | boş | Gönderilecek türler; boşsa hepsi. Süzülen olay teslim edilmiş sayılır. |
| `Timeout` | 10 saniye | Bir olayın, küme onu onaylayana kadar en uzun bekleme süresi (istemcinin yeniden denemeleri dahil); 1 saniye ile 1 saat arası. |
| `ConfigureProducer` | `null` | İstemcinin ayarlarını (`ProducerConfig`) Haldus kendi ayarlarını yazdıktan sonra değiştirir. Güvenlik ayarları (SASL, TLS) buradan verilir. |

Mesaj:

| Parça | Değer |
|---|---|
| Anahtar | `{kaynak}:{kayıt}` (`order:01936fc0-…`). Bir kaydın olayları aynı bölüme düşer. |
| Değer | Olayın JSON hali (webhook gövdesiyle aynı). |
| Zaman | Olayın zamanı (`OccurredAt`). |
| Başlıklar | `event-id`, `event-type`, `resource`. |

Garantiler:

- **Bütün eşlenik kopyalar yazana kadar** (`acks=all`) olay teslim edilmiş sayılmaz.
- Üretici idempotenttir: istemcinin yeniden denediği bir gönderim bölüme iki kez yazılmaz. Kutunun kendi yeniden
  denemesi ise (gönderildiği halde onayı gelmeyen olay) yinelenebilir; tüketici `event-id` başlığına bakmalıdır.
- Aynı anahtar aynı bölüme düştüğü için bir kaydın olayları sırayla tüketilir. Bölümleme Java istemcisinin
  varsayılanıyla aynıdır (`murmur2_random`): aynı konuya yazan başka uygulamalarla aynı anahtar aynı bölüme düşer.
- Olaylar tek tek ve sırayla gönderilir (`linger.ms=0`).
- İstemcinin kendi günlüğü uygulamanın günlüğüne yönlendirilir.
- Paket yerel bir kütüphane (`librdkafka`) taşır.

Kapsayıcıda çalışan tek düğümlü gerçek bir kümeye karşı sınandı; SASL/PLAIN ve TLS dinleyicileri dahil (yanlış parola
ve güvenilmeyen sertifika reddedilir). SCRAM ve karşılıklı TLS sınanmadı.

### Kuyruk yayıncılarının ortak kuralları

SQS, RabbitMQ ve Kafka yayıncıları aynı biçimdedir:

- Ayarlar yayıncı kurulurken doğrulanır. Geçersiz bir ayar olay servisini düşürür ve uygulama açılmaz. Build sırasındaki
  şema üretimi yayıncıları hiç kurmaz; orada kimlik bilgisi gerekmez.
- Bağlantı ilk olayda kurulur.
- Zaman aşımı bir `TimeoutException`'a çevrilir ve olay yeniden denenir.
- Uygulama başına tek bir hedef (kuyruk, exchange, konu) kaydedilir. Aynı olayları başka bir hedefe de göndermek için
  kendi yayıncınızı yazın.
- Hedefi (kuyruk, exchange, konu) Haldus oluşturmaz; uygulama açılmadan önce var olmalıdır.

## Birlikte kullanım

| Bildirim | Birleşir mi |
|---|---|
| `MapCrud`, `MapReadOnly`, `MapAppendOnly` | Evet. |
| `MapJob`, `MapBalance`, `MapStore`, `MapLookup`, `MapSingleton` | Hayır (`HALDUS007`). |
| `Workflow`, `Axis`, `MapTransition` | Evet; her geçiş kendi olayını da üretir. |
| `SoftDeletable` | Evet; yumuşak silme `deleted`, geri yükleme `updated` yazar. |
| `MultiTenant` | Evet; olay `TenantId` taşır. Yayıncı bütün kiracıların olaylarını alır. |
| `Publishable` | Evet; yayın kopyaları olay üretmez, taslak üretir. |
| `Sensitive`, `Encrypted` | Evet; bu alanlar olayın verisinde yer almaz. |

Tanıların listesi: [tanılar](../reference/diagnostics.md).

## Sınırlar

- Değişiklik izleyicisini atlayan yazmalar olay üretmez: `ExecuteUpdate`, `ExecuteDelete`, toplanan alanların
  yazılması, bakım servisinin temizliği (saklama süresi, süresi dolan kayıtlar), veritabanının zincirleme silmesi.
- Eşzamanlı transaction'ların olayları arasında sıra garanti edilmez; aynı kaydın olayları sıralıdır.
- Vazgeçilen olaylar kendiliğinden silinmez ve yeniden kuyruğa alınmaz.
- Olayların okunabildiği bir HTTP ucu yoktur. Değişiklikleri istemciden çekerek izlemek için
  [değişiklik akışı](history-and-publishing.md) (`ChangeFeed`) vardır.
- Teslimat tek sıradan geçer: sürekli başarısız olan bir yayıncı veya adres, vazgeçilene kadar bütün olayları
  bekletir. Webhook'ta adres başına ayrı kuyruk ve devre kesici yoktur.
- SQS'te mesaj boyutu denetlenmez: sınırı aşan bir olay on denemeden sonra bırakılır ve o sırada arkasındakileri
  bekletir.
- SQS yayıncısı ve S3 deposu gibi, gerçek AWS'ye karşı sınanmadı.

## Ayrıca bakın

- [Durum makineleri](state-machines.md): geçişler ve olay adları.
- [Sürüm ve yayın](history-and-publishing.md): değişiklik akışı.
- [Hassas veriler](sensitive-data.md): olayda görünmeyen alanlar.
- [Üretime hazırlık](production.md)
- [Paketler](../reference/packages.md)
- [Bildirim dizini](../reference/declarations.md)
