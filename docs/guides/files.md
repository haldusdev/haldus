# Dosyalar ve takılabilir kaynaklar

Bir faturanın PDF'i, bir ürünün görseli, bir destek kaydına eklenen ekran görüntüsü: asıl içeriği bayt akışı olan
kayıtlar JSON gövdesiyle yazılamaz. `MapFile` böyle bir kaynağı iki adımda yazar: önce kayıt (adı ve sizin alanlarınız)
oluşturulur, sonra içeriği ayrı bir uçla yüklenir. Boyut ve tür sınırları, içeriğin özeti, depolama ve güvenli indirme
Haldus'undur. Bu sayfa ayrıca dosyaları, yorumları ve etiketleri başka kaynaklara takmayı anlatır.

Örneklerde web SDK'nın örtük `using`'leri, `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs`'tir; `builder` ve `app`
satırları gösterilmediyse yazılmış sayılır.

## İçindekiler

- [Dosya kaynağı](#dosya-kaynağı)
- [Yükleme](#yükleme)
- [Dosyayı indirmek](#dosyayı-indirmek)
- [Süreli bağlantılar](#süreli-bağlantılar)
- [Depolama](#depolama)
- [Takılabilir kaynaklar](#takılabilir-kaynaklar)
- [Etiketler](#etiketler)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Dosya kaynağı

`MapFile<T>(maxBytes, türler...)` içeriği ayrıca yüklenen bir `MapCrud` kaynağıdır. Kaydın geri kalanı sıradan bir
kaynak gibi davranır: listelenir, okunur, `PUT` / `PATCH` ile güncellenir, silinir.

<!-- example: file -->
```csharp
app.MapFile<Attachment>(maxBytes: 5 * 1024 * 1024, "image/*", "application/pdf", "text/plain");

public partial class Attachment
{
    [MaxLength(500)]
    public string? Caption { get; set; }
}
```

| Parametre | Varsayılan | Anlamı |
|---|---|---|
| `maxBytes` | 10 MB | Kabul edilen en büyük içerik, bayt olarak. 1 bayt ile 1 GB arasında olmalıdır. |
| `contentTypes` | boş | Kabul edilen türler: `"application/pdf"` gibi tam tür ya da `"image/*"` gibi joker. Boşsa her tür kabul edilir. Karşılaştırma büyük-küçük harfe duyarsızdır. |

İki parametre de derleme zamanında okunur; sabit olmalıdırlar. Bir değişken veya hesaplanan bir değer verilirse
derleme `HALDUS010` ile durur.

Haldus entity'ye şu alanları ekler. `FileName` dışındakileri yalnızca Haldus yazar; istekte gönderilseler de yok
sayılırlar.

| Alan | Tip | Anlamı |
|---|---|---|
| `FileName` | `string` | İndirirken önerilen ad. Zorunludur, en çok 255 karakter. İstekle yazılır ve değiştirilir. |
| `ContentType` | `string?` | Yüklenirken bildirilen tür (yalnızca tür; `charset` gibi parametreler atılır). |
| `Size` | `long` | İçeriğin bayt cinsinden boyutu. |
| `Checksum` | `string?` | İçeriğin SHA-256 özeti, küçük harfli onaltılık. |
| `Status` | `FileStatus` | `Pending` (içerik yok) veya `Uploaded`. JSON'da adıyla yazılır. |
| `UploadedAt` | `DateTimeOffset?` | İçeriğin son yüklendiği an. |
| `CreatedAt`, `UpdatedAt` | | `MapFile` kendiliğinden `Auditable` olur: yükleme bekleyen kaydın ne zaman oluşturulduğu bilinmelidir. |

İçeriğin depodaki anahtarı da entity'de durur ama hiçbir yanıtta gösterilmez.

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/attachments` | Kayıtları listeler (sayfalı). |
| `GET` | `/attachments/{id}` | Kaydı okur. |
| `POST` | `/attachments` | Kaydı içeriksiz oluşturur (201). |
| `PUT`, `PATCH` | `/attachments/{id}` | Kaydın alanlarını günceller; içeriğe dokunmaz. |
| `DELETE` | `/attachments/{id}` | Kaydı ve içeriğini siler (204). |
| `PUT` | `/attachments/{id}/content` | İçeriği yükler veya değiştirir. |
| `GET` | `/attachments/{id}/content` | İçeriği indirir. |

Kayıt oluşturulur:

```http
POST /attachments
Content-Type: application/json

{ "fileName": "notes.txt", "caption": "Toplantı notları" }
```

```http
HTTP/1.1 201 Created
Location: /attachments/01936f4e-8a2b-7c3d-9e4f-5a6b7c8d9e0f
```

```json
{
  "id": "01936f4e-8a2b-7c3d-9e4f-5a6b7c8d9e0f",
  "fileName": "notes.txt",
  "caption": "Toplantı notları",
  "createdAt": "2026-11-25T09:30:00.123456+00:00",
  "updatedAt": null,
  "contentType": null,
  "size": 0,
  "checksum": null,
  "status": "Pending",
  "uploadedAt": null
}
```

## Yükleme

İçerik `PUT /{id}/content` ile iki biçimde gönderilir:

- **Ham gövde.** Gövdenin kendisi içeriktir, türü isteğin `Content-Type` başlığıdır.
- **Multipart form.** Gövde `multipart/form-data` ise formun ilk dosya parçası içeriktir; türü parçanın kendi
  `Content-Type` başlığıdır. Dosya parçasından önceki alanlar atlanır, sonrakiler okunmaz. Tarayıcıların ve çoğu
  istemci kütüphanesinin yüklediği biçim budur.

```http
PUT /attachments/01936f4e-8a2b-7c3d-9e4f-5a6b7c8d9e0f/content
Content-Type: text/plain; charset=utf-8

test
```

```http
HTTP/1.1 200 OK
Content-Type: application/json
```

```json
{
  "id": "01936f4e-8a2b-7c3d-9e4f-5a6b7c8d9e0f",
  "fileName": "notes.txt",
  "caption": "Toplantı notları",
  "createdAt": "2026-11-25T09:30:00.123456+00:00",
  "updatedAt": "2026-11-25T09:30:04.481022+00:00",
  "contentType": "text/plain",
  "size": 4,
  "checksum": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
  "status": "Uploaded",
  "uploadedAt": "2026-11-25T09:30:04.481022+00:00"
}
```

Aynı içerik bir formla da yüklenir:

```bash
curl -X PUT https://localhost:5001/attachments/01936f4e-8a2b-7c3d-9e4f-5a6b7c8d9e0f/content \
  -F "file=@notes.txt;type=text/plain"
```

İçerik ham gövdeyle de formla da yüklense aynı özeti verir: form yalnızca bir taşıma biçimidir.

### Kurallar ve hata yanıtları

| Durum | Yanıt |
|---|---|
| Kayıt yok veya isteği yapan göremiyor. | 404 |
| İçerik `maxBytes`'tan büyük. | 413. Gövde okunurken sayılır; `Content-Length` yanlış söylese de sınır geçerlidir. |
| Tür kabul edilen türlerden değil (veya hiç bildirilmedi ve bir tür listesi var). | 415 |
| Gövde boş; ya da form bir dosya parçası taşımıyor. | 400, alan düzeyinde hata: `content` |
| Kaynak `ConcurrencyChecked` ve `If-Match` yok / uymuyor. | 428 / 412 |

- **Reddedilen yükleme var olan içeriğe dokunmaz.** İçerik her yüklemede yeni bir anahtara yazılır; kayıt ancak içerik
  kabul edilince ona geçer ve eski içerik kayıt kaydedildikten sonra silinir.
- Yeniden yükleme içeriği değiştirir; `size`, `checksum`, `contentType` ve `uploadedAt` yenilenir.
- Yükleme bir yazmadır: kaydın yazma kuralları (eşzamanlılık denetimi, onaylanmış kaydın kilidi gibi) yükleme ucunda da
  geçerlidir.
- Haldus bu uç için sunucunun istek gövdesi sınırını `maxBytes`'a göre ayarlar. Önünüzde kendi gövde sınırı olan bir
  vekil (nginx, bir yük dengeleyici) varsa onun sınırını ayrıca ayarlamanız gerekir.
- Hata mesajları isteğin dilindedir (İngilizce veya Türkçe; `Accept-Language`).

## Dosyayı indirmek

`GET /{id}/content` içeriği gönderir:

```http
HTTP/1.1 200 OK
Content-Type: text/plain
Content-Disposition: attachment; filename=notes.txt; filename*=UTF-8''notes.txt
X-Content-Type-Options: nosniff
ETag: "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
Last-Modified: Wed, 25 Nov 2026 09:30:04 GMT

test
```

- İçerik her zaman ek olarak (`attachment`) ve `nosniff` ile gönderilir. Tür yükleyenin beyanıdır; tarayıcı onu
  tahminle değiştirmemeli ve içerik sayfanın içinde açılmamalıdır (yüklenen bir HTML dosyası sizin alan adınızda
  çalışmasın diye).
- `ETag` içeriğin özetidir, `Last-Modified` son yükleme anıdır. Koşullu istekler (`If-None-Match`) bunlarla çalışır.
- İçeriğin bir parçası `Range` başlığıyla istenebilir (206). Bu, veritabanı ve dosya sistemi depolarında çalışır; S3
  deposunda çalışmaz (bkz. [Depolama](#depolama)).
- Tür bildirilmeden yüklenmiş içerik `application/octet-stream` olarak iner.
- İçeriği henüz yüklenmemiş kayıt 404 döner.

## Süreli bağlantılar

`.SignedLinks()` içeriğe, oturumu olmayan birinin de açabildiği süreli bir bağlantı üretir: bir e-postaya konan
indirme bağlantısı, bir dış sisteme verilen görsel adresi.

<!-- example: signed-links -->
```csharp
app.MapFile<MediaFile>(maxBytes: 2 * 1024 * 1024, "image/*")
    .MultiTenant()
    .SignedLinks();

public partial class MediaFile
{
    [MaxLength(200)]
    public string? Title { get; set; }
}
```

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `POST` | `/media-files/{id}/content/link?expiresInSeconds=600` | Kaydı görebilen birine bir bağlantı verir. |
| `GET` | `/media-files/shared-content/{token}` | İçeriği kimlik sormadan gönderir. |

```http
POST /media-files/01936f52-1c4d-7e8f-a0b1-c2d3e4f5a6b7/content/link?expiresInSeconds=600
```

```json
{
  "url": "/media-files/shared-content/CfDJ8NhXk3...",
  "expiresAt": "2026-11-25T09:40:00.512304+00:00"
}
```

`url` sunucunun köküne görelidir; uygulama bir `PathBase` altında çalışıyorsa onu da taşır. Bağlantıyı tam bir adrese
çevirmek (alan adını eklemek) istemcinin işidir.

Kurallar:

- `expiresInSeconds` verilmezse bağlantı 300 saniye yaşar. Değer 1 ile 86 400 (bir gün) arasına çekilir.
- İçeriği henüz yüklenmemiş kaydın bağlantısı üretilmez: 404.
- Bağlantı, üretildiği andaki içeriğe bağlıdır. İçerik değişirse, kayıt silinirse ya da süresi dolarsa bağlantı 404
  döner; yeni içerik için yeni bir bağlantı üretilir. Bozuk veya uydurma bir bağlantı da 404 döner.
- Bağlantı tahmin edilemez: kaydın kimliği ve içeriğin özeti ASP.NET Data Protection ile imzalanıp şifrelenir. Bir
  kaynağın bağlantısı başka bir kaynağın içeriğini açamaz.
- Bağlantıyı açan uç oturumsuzdur (`AllowAnonymous`): uygulamanızın varsayılan yetki kuralı onu kapatmaz. Kiracıya ait
  bir kaynakta içerik, kaydın kiracısı adına okunur.
- Bağlantıyı taşıyan herkes içeriği indirebilir. `SignedLinks`'i yalnızca paylaşılması istenen dosyalarda açın.

> [!IMPORTANT]
> Bağlantılar Data Protection anahtarlarıyla korunur. Anahtarlar kalıcı değilse uygulama yeniden başlayınca, sunucular
> arasında ortak değilse başka bir sunucuya düşen istekte bütün bağlantılar geçersiz olur. Anahtarların saklanması
> uygulamanın işidir; bkz. [üretime hazırlık](production.md).

## Depolama

İçeriğin nerede durduğunu `IHaldusFileStorage` servisi belirler. Üç hazır depo vardır:

| Depo | Nasıl seçilir | Ne zaman |
|---|---|---|
| Veritabanı | Varsayılan; bir şey yazmazsınız. | Küçük ve orta boy dosyalar, tek bir altyapı. |
| Dosya sistemi | `AddHaldusFileSystemStorage(root)` | Tek sunucu veya bütün sunucuların gördüğü ortak bir disk. |
| S3 uyumlu nesne deposu | `AddHaldusS3Storage(...)` (`Haldus.Storage.S3` paketi) | AWS S3, MinIO ve benzerleri. |

Aynı depo, dışa aktarma işlerinin ürettiği dosyalar için de kullanılır (bkz. [arka plan işleri](jobs.md#dışa-aktarma)).

### Veritabanı

İçerik `HaldusFileContents` tablosunda, dosya kaydıyla aynı veritabanında durur. İçerik ve kayıt aynı kaydetmede
yazılır: ikisi birlikte kalıcı olur ya da hiçbiri olmaz. Tablo yalnızca bir dosya kaynağı (veya dışa aktarma işi) varsa
modele girer; migration'larınızda görünür.

İçerik yazılırken ve okunurken bütün olarak belleğe alınır. Büyük dosyalar için dosya sistemini veya bir nesne deposunu
seçin.

### Dosya sistemi

<!-- example: storage-filesystem -->
```csharp
builder.Services.AddHaldusFileSystemStorage(builder.Configuration["Files:Root"]!);

var app = builder.Build();

app.MapFile<Attachment>(maxBytes: 50 * 1024 * 1024, "application/pdf");

public partial class Attachment;
```

- İçerik önce geçici bir dosyaya yazılır ve yazma bitince yerine taşınır; yarım kalan bir yükleme eski içeriğin yerine
  geçmez.
- Birden çok sunucu varsa hepsi aynı klasörü görmelidir.
- Depo anahtarlarını Haldus üretir. Yine de klasörün dışını gösteren bir anahtar hiçbir koşulda kabul edilmez.
- İçerik veritabanının dışındadır: kayıt kaydedilemezse yazılmış içerik klasörde sahipsiz kalabilir.

### S3 uyumlu depo

`Haldus.Storage.S3` paketi içeriği bir S3 kovasında saklar; servisle resmi AWS SDK'sı (`AWSSDK.S3`) konuşur.

<!-- example: storage-s3 -->
```csharp
builder.Services.AddHaldusS3Storage(options =>
{
    options.Region = "eu-central-1";
    options.Bucket = "shop-uploads";
    options.KeyPrefix = "files";
});

var app = builder.Build();

app.MapFile<Attachment>(maxBytes: 100 * 1024 * 1024, "image/*", "application/pdf");

public partial class Attachment;
```

AWS dışındaki S3 uyumlu bir servis (MinIO gibi) için adres ve anahtarlar verilir:

<!-- example: storage-minio -->
```csharp
builder.Services.AddHaldusS3Storage(options =>
{
    options.ServiceUrl = new Uri("http://localhost:9000");
    options.Bucket = "shop-uploads";
    options.AccessKey = builder.Configuration["S3:AccessKey"]!;
    options.SecretKey = builder.Configuration["S3:SecretKey"]!;
});

var app = builder.Build();

app.MapFile<Attachment>();

public partial class Attachment;
```

| Seçenek | Varsayılan | Anlamı |
|---|---|---|
| `ServiceUrl` | `null` | Servisin adresi (`http://localhost:9000`); yol, sorgu ve kullanıcı bilgisi taşıyamaz. Boşsa AWS'nin `Region` bölgesindeki adresi kullanılır. |
| `Region` | `"us-east-1"` | Bölge. MinIO'nun varsayılanı da `us-east-1`'dir. |
| `Bucket` | `""` | İçeriklerin yazıldığı kova; zorunludur. 3–63 karakter: küçük harf, rakam, `-` ve `.`. Kova var olmalıdır; Haldus kova oluşturmaz. |
| `AccessKey`, `SecretKey` | `""` | İkisi birlikte verilir. İkisi de boşsa kimlik bilgisi SDK'nın varsayılan zincirinden okunur (ortam değişkenleri, profil, kapsayıcı veya sunucu rolü). |
| `SessionToken` | `null` | Geçici kimlik bilgisinin oturum token'ı; yalnızca `AccessKey` ile anlamlıdır. |
| `KeyPrefix` | `null` | Bütün anahtarların önüne eklenen yol (`shop/files`); kovayı başka uygulamalarla paylaşmak için. |
| `ForcePathStyle` | `true` | Kova adreste yol olarak yazılır (`host/kova/anahtar`). `false` olursa alan adına taşınır (`kova.host/anahtar`). |
| `PartSize` | 8 MiB | Çok parçalı yüklemenin parça boyu; 5 MiB ile 1 GiB arasında. Yükleme başına bellekte en çok bu kadar tutulur. |
| `Timeout` | 5 dakika | Tek bir isteğin (bir parçanın veya tek istekle yüklenen içeriğin) süresi. |
| `ConfigureClient` | `null` | SDK istemcisinin ayarlarını (`AmazonS3Config`) Haldus kendi ayarlarını yazdıktan sonra değiştirir: vekil sunucu, yeniden deneme sayısı... |

Davranış:

- İçerik belleğe bütün olarak alınmaz. Yüklenen içerik `PartSize` kadar okunur; ona sığarsa tek bir istekle, sığmazsa
  çok parçalı yüklemeyle gönderilir. Yarıda kalan çok parçalı yükleme iptal edilir; iptal de başarısız olursa parçalar
  kovada kalır ve bir uyarı günlüğe yazılır. Kovaya, tamamlanmamış çok parçalı yüklemeleri temizleyen bir yaşam döngüsü
  kuralı koymanız önerilir.
- `ServiceUrl` verildiğinde SDK'nın yeni varsayılan bütünlük özetleri yalnızca gerektiğinde gönderilir; S3 uyumlu
  servislerin hepsi onları tanımaz.
- Ayarlar uygulama açılırken değil depo ilk kullanıldığında doğrulanır (build sırasındaki şema üretimi kimlik bilgisi
  olmadan çalışabilsin diye). Yanlış bir ayar ilk yüklemede `InvalidOperationException` olur.
- Servisin reddettiği bir istek `HaldusS3StorageException` olur. İstisna servisin durum kodunu (`StatusCode`) ve hata
  kodunu (`ErrorCode`: `NoSuchBucket`, `SignatureDoesNotMatch`...) taşır; gizli anahtarı taşımaz. Olmayan nesne "içerik
  yok" sayılır (404); olmayan kova bir yapılandırma hatasıdır ve istisna olur.
- İndirilen içerik akıtılır ve aranabilir değildir; bu yüzden S3 deposunda `Range` isteği desteklenmez.
- İçerik veritabanının dışındadır: kayıt kaydedilemezse nesne kovada sahipsiz kalabilir.

> [!NOTE]
> S3 deposu MinIO'ya karşı sınandı. Gerçek AWS S3'e karşı sınanmadı.

### Silinen dosyanın içeriği

Bir dosya kaydı silinince içeriği de depodan kaldırılır. Kural kaydetmeye bağlıdır, uçlara değil: dosyanın kendi
`DELETE` ucu, toplu silme, uygulamanızın kendi kodu (`context.Remove(file)`) ve kaydın üst kaydıyla birlikte zincirleme
silinmesi aynı yoldan geçer. Veritabanının kendisinin zincirleme sildiği dosya kayıtları (makale silinince ona takılı
dosyalar, proje silinince altındaki işlerin dosyaları), silme kaydedilmeden önce yabancı anahtarlar izlenerek bulunur.

İçeriğin ne zaman kalktığı depoya göre değişir:

| Depo | İçerik ne zaman kalkar |
|---|---|
| Veritabanı | Kayıtla aynı kaydetmede. İkisi birlikte silinir ya da birlikte kalır. |
| Dosya sistemi, S3, kendi deponuz | Silme kalıcı olunca: açık bir transaction varsa o commit edilince. Transaction geri alınırsa (veya silmeyi içeren kayıt noktasına dönülürse) içerik kalır. |

Commit'ten sonra içerik kaldırılamazsa (depo erişilemez) hata günlüğe uyarı olarak yazılır ve istek başarısız sayılmaz:
kayıtlar zaten silinmiştir. İçerik o durumda depoda kalır.

<!-- example: file-cleanup -->
```csharp
app.MapCrud<Project>();
app.MapFile<Document>().AttachedTo<Project>();

// Proje silinince ona takılı dosya kayıtlarını veritabanı siler; içerikleri de depodan kalkar.
app.MapDelete("/projects/{id:guid}/purge", async (Guid id, AppDbContext db) =>
{
    await db.Set<Project>().Where(project => project.Id == id).ExecuteDeleteAsync();
    return Results.NoContent();
});

public partial class Project
{
    public string Name { get; set; } = "";
}

public partial class Document;
```

Yukarıdaki uç bir istisnadır: `ExecuteDelete` değişiklik izleyicisini atlar ve Haldus silinen kayıtları göremez.
İçerikleri kaldırmak için kaydı yükleyip `context.Remove(...)` ile silin; ya da sildiğiniz dosyaların içeriğini
`HaldusFiles.ReleaseAsync(context, file, ct)` ile elle kaldırın.

### Kendi deponuz

Başka bir depo `IHaldusFileStorage` uygulanıp tekil servis olarak kaydedilerek kullanılır. Arayüz üç metottur:

| Metot | Ne yapar |
|---|---|
| `WriteAsync(context, key, content, ct)` | İçeriği anahtarla yazar; anahtarda içerik varsa değiştirir. |
| `OpenReadAsync(context, key, ct)` | İçeriği okumak için açar; yoksa `null` döner. Akışı çağıran kapatır. |
| `DeleteAsync(context, key, ct)` | İçeriği siler; yoksa bir şey yapmaz. |

`context` dosya kaydını yazan `DbContext`'tir; içeriği kayıtla aynı kaydetmede yazmak isteyen bir depo onu kullanır.
Aşağıdaki örnek, testlerde kullanılabilecek bir bellek içi depodur:

<!-- example: storage-custom -->
```csharp
using System.Collections.Concurrent;

builder.Services.AddSingleton<IHaldusFileStorage, InMemoryFileStorage>();

var app = builder.Build();

app.MapFile<Attachment>();

public partial class Attachment;

public sealed class InMemoryFileStorage : IHaldusFileStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _contents = new();

    public async Task WriteAsync(DbContext context, string key, Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        _contents[key] = buffer.ToArray();
    }

    public Task<Stream?> OpenReadAsync(DbContext context, string key, CancellationToken cancellationToken) =>
        Task.FromResult<Stream?>(_contents.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

    public Task DeleteAsync(DbContext context, string key, CancellationToken cancellationToken)
    {
        _contents.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
```

Okuma akışı aranabilirse (`CanSeek`) indirme ucu `Range` isteklerini kendiliğinden destekler. Yazılan akış istemcinin
gövdesidir: aranabilir değildir ve okunurken boyut sınırı denetlenir. Sınır aşılırsa akışın okunması bir istisna
fırlatır; deponuz bu istisnayı yutmamalı, dışarı bırakmalıdır. Haldus onu 413'e çevirir ve aynı anahtarla
`DeleteAsync` çağırır.

## Takılabilir kaynaklar

Ekler, yorumlar ve notlar çoğu zaman tek bir kaynağa ait değildir: aynı yorum kaynağı hem siparişlere hem faturalara
yazılır. `.AttachedTo<T>()` kaynağı birkaç hedefe takılabilir yapar. Aynı bildirim hedef tarafında da yazılabilir:
`.Attachable<TFile>()` dosyalar, `.Commentable<TComment>()` yorumlar içindir.

<!-- example: attached -->
```csharp
app.MapFile<Attachment>(maxBytes: 10 * 1024 * 1024, "image/*", "application/pdf");
app.MapAppendOnly<Comment>().Auditable().Hierarchical();

app.MapCrud<Order>().Attachable<Attachment>().Commentable<Comment>();
app.MapCrud<Invoice>().Attachable<Attachment>().Commentable<Comment>();

public partial class Attachment;

public partial class Comment
{
    [Required]
    [MaxLength(2000)]
    public string Text { get; set; } = "";
}

public partial class Order
{
    [MaxLength(100)]
    public string CustomerName { get; set; } = "";
}

public partial class Invoice
{
    public decimal Amount { get; set; }
}
```

Aynı şey takılan kaynağın kendi zincirinde de yazılabilir; iki yazım birbirinin aynısıdır ve ikisi birlikte yazılsa da
hedef bir kez sayılır:

<!-- example: attached-to -->
```csharp
app.MapAppendOnly<Comment>()
    .AttachedTo<Order>()
    .AttachedTo<Invoice>();

app.MapCrud<Order>();
app.MapCrud<Invoice>();

public partial class Comment
{
    [Required]
    [MaxLength(2000)]
    public string Text { get; set; } = "";
}

public partial class Order;

public partial class Invoice;
```

| Bildirim | Nereye | Etkisi |
|---|---|---|
| `AttachedTo<T>(onDelete)` | takılan kaynağın zinciri | `T` bir hedef olur. Her çağrı bir hedef ekler. `onDelete` varsayılan olarak `OnDelete.Cascade`'dir. |
| `Attachable<TFile>()` | hedefin zinciri | `TFile`'ı bu hedefe takar. `TFile` `MapFile` ile eşlenmiş olmalıdır. |
| `Commentable<TComment>()` | hedefin zinciri | `TComment`'i bu hedefe takar. `TComment` `MapCrud`, `MapAppendOnly` veya `MapFile` ile eşlenmiş olmalıdır. |

Takılan kaynağın kendi adresi yoktur; her hedefin adresi altında ayrı ayrı sunulur:

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/orders/{orderId}/comments` | O siparişin yorumları. |
| `POST` | `/orders/{orderId}/comments` | Siparişe yorum yazar. |
| `GET` | `/orders/{orderId}/comments/{id}` | Bir yorumu okur. |
| `GET`, `POST`... | `/invoices/{invoiceId}/comments` | Aynı uçlar faturada. |
| `PUT` | `/orders/{orderId}/attachments/{id}/content` | Dosya kaynağının içerik uçları da her hedefin altındadır. |

Hangi uçların üretildiği takılan kaynağın archetype'ına bağlıdır: `MapAppendOnly` yorum güncellenemez ve silinemez,
`MapFile` ek her `MapCrud` ucuyla birlikte içerik uçlarını taşır.

```http
POST /orders/01936f60-2a3b-7c4d-8e5f-6a7b8c9d0e1f/comments
Content-Type: application/json

{ "text": "Kargo adresi değişti." }
```

```http
HTTP/1.1 201 Created
Location: /orders/01936f60-2a3b-7c4d-8e5f-6a7b8c9d0e1f/comments/01936f61-0b1c-7d2e-9f30-4a5b6c7d8e9f
```

```json
{
  "id": "01936f61-0b1c-7d2e-9f30-4a5b6c7d8e9f",
  "text": "Kargo adresi değişti.",
  "parentId": null,
  "orderId": "01936f60-2a3b-7c4d-8e5f-6a7b8c9d0e1f",
  "invoiceId": null,
  "createdAt": "2026-11-25T10:12:44.902113+00:00",
  "updatedAt": null
}
```

Kurallar:

- Her hedef için isteğe bağlı gerçek bir yabancı anahtar üretilir (`OrderId`, `InvoiceId`); "tip + kimlik" çifti
  değildir. Bir kayıtta tam olarak biri doludur; bunu veritabanındaki bir kısıt da sağlar.
- Hedef adresten gelir. Gövdede gönderilen `orderId` veya `invoiceId` yok sayılır; kayıt başka bir hedefe taşınamaz.
- Hedef yoksa ya da isteği yapan onu göremiyorsa 404 döner.
- Takılı kayıt, takıldığı kayıt görünürse görünür. Başka bir hedefin adresinden aranan kayıt 404'tür.
- Hedef silinince ona takılı kayıtlar da silinir (`Cascade`); öteki hedeflere takılı olanlar kalır. Takılı dosyaların
  içerikleri de depodan kaldırılır (bkz. [silinen dosyanın içeriği](#silinen-dosyanın-içeriği)).
- Hedeflerden biri kiracıya aitse takılan kaynak da kiracıya ait sayılır. Zincirleme silme kiracı sınırını aşmaz: öyle
  bir durumda kiracıya ait olmayan hedeflerin silme davranışı `Restrict` olur ve takılı kaydı olan hedef silinemez (409).
- `Hierarchical()` birleşir: yanıtlanan kayıt aynı hedefe takılı olmalıdır; değilse 400 (`parentId`).
- Yetkilendirme gibi kurallar takılan kaynağın kendi `Map` çağrısına eklenir: `/orders/{orderId}/attachments` uçlarını
  `app.MapFile<Attachment>()` eşler ve kurallarını da o çağrı taşır. Hedefin builder'ına eklenen
  `RequireAuthorization()` takılı kaynağın uçlarına uygulanmaz.

## Etiketler

`.Taggable<TTag>()` kaynağın kayıtlarını ortak bir etiket kümesine bağlar. `MapAssociation<TEntity, TTag>()` yazmakla
aynıdır; çoktan-çoğa bağın bütün kuralları geçerlidir (bkz. [ilişkiler](relationships.md)).

<!-- example: taggable -->
```csharp
app.MapLookup<Label>();

app.MapCrud<Article>()
    .Taggable<Label>();

public partial class Article
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";
}

public partial class Label;
```

Adresler etiket kaynağının adından ve anahtarından kurulur. `Label` bir `MapLookup` olduğu için anahtarı koddur:

| Yöntem | Adres | Ne yapar |
|---|---|---|
| `GET` | `/articles/{articleId}/labels` | Makalenin etiketleri (sayfalı). |
| `GET` | `/articles/{articleId}/labels/{labelCode}` | Bağ varsa 204, yoksa 404. |
| `PUT` | `/articles/{articleId}/labels/{labelCode}` | Etiketi bağlar (204). |
| `DELETE` | `/articles/{articleId}/labels/{labelCode}` | Bağı kaldırır (204). |
| `PUT` | `/articles/{articleId}/labels` | Makalenin etiketlerini verilen listeyle aynı yapar. |
| `GET` | `/labels/{labelCode}/articles` | Bir etiketin makaleleri (sayfalı). |
| `PUT` | `/labels/{labelCode}/articles` | Bir etiketin makalelerini verilen listeyle aynı yapar. |

```http
PUT /articles/01936f70-4c5d-7e6f-8a9b-0c1d2e3f4a5b/labels
Content-Type: application/json

{ "ids": ["campaign", "featured"] }
```

```http
HTTP/1.1 204 No Content
```

Kurallar:

- İki uç da bir `Map*` çağrısıyla eşlenmiş olmalıdır. Uçlardan biri yoksa veya isteği yapan onu göremiyorsa 404 döner.
- Topluca değiştirmede listedeki her kayıt isteği yapana görünür olmalıdır; biri bile değilse 400 döner ve hiçbir şey
  değişmez. Liste en çok 1000 kimlik taşır.
- Etiket bağının uçları makalenin adresinin dışında, kökte eşlenir; yine de makalenin builder'ına eklenen kuralları
  (yetkilendirme, hız sınırı) alırlar. `RequireAuthorization()` zincirde `Taggable`'dan önce de sonra da yazılabilir.
  Etiket kaynağının kendi uçları (`/labels`) bu kurallardan etkilenmez.

## Birlikte kullanım

| Bildirim | Birleşir mi |
|---|---|
| `MultiTenant`, `OwnedByCurrentUser`, `Auditable`, `ConcurrencyChecked`, `References`, `Under`, `Filterable`, `Searchable`, `Sortable` | Evet. `MapFile` bir `MapCrud`'dır. |
| `Workflow`, `Document`, `SoftDeletable`, `Cloneable`, `Publishable`, `Reversible`, `Supersedes`, `CreatedFrom` | Hayır (`HALDUS007`): dosyanın durumu ve içeriği kendi sabit modelidir; kopyalanan veya başka bir kayıttan doğan kayıt içeriksiz kalırdı. |
| `Retention` | Hayır (`HALDUS007`): kayıtlar topluca silinir ve içerik depoda sahipsiz kalırdı. |
| `SignedLinks` + `Under` / `AttachedTo` | Hayır (`HALDUS007`): bağlantının adresi kaynağın kendi koleksiyonundadır. |
| `SignedLinks` başka bir `Map*` çağrısında | Hayır (`HALDUS007`): yalnızca `MapFile` ile yazılır. |
| `Attachable<T>()`, `T` `MapFile` değilse | Hayır (`HALDUS007`); içeriği olmayan kayıt için `Commentable` kullanın. |

Takılabilir bir kaynağın tek bir adresi yoktur. Bu yüzden şunlarla birleşmez (`HALDUS007`): `Under`, `Orderable`,
`NaturalKey`, `ChangeFeed`, `CustomFields`, durum makinesi, `CreatedFrom`, `/me` altında sahiplik
(`OwnedByCurrentUser(underMe: false)` birleşir), builder'ında `MapTransition` / `MapAction` ve `MapDetail`. Takılabilir
bir kaynak bir bağın ucu veya başka bir kaynağın ebeveyni de olamaz. Tanıların listesi:
[tanılar](../reference/diagnostics.md).

## Sınırlar

- Devam ettirilebilir (parça parça) yükleme yoktur; içerik tek bir istekle yüklenir.
- Nesne deposuna doğrudan (ön imzalı adresle) yükleme yoktur; içerik her zaman uygulamanın üzerinden geçer.
- Virüs taraması ve görsel işleme (küçük resim, boyutlandırma) yoktur.
- İçerik tarayıcıda açılacak biçimde (`inline`) sunulamaz.
- Yükleme bekleyen (`Pending`) dosya kayıtları kendiliğinden silinmez.
- Değişiklik izleyicisini atlayan silmeler (`ExecuteDelete`, elle yazılmış SQL) ve veritabanında uygulamanın dışından
  yapılan silmeler içeriği depoda bırakır. Zincirleme silme en çok sekiz ilişki boyunca ve yalnızca tek kolonlu yabancı
  anahtarlarda izlenir.
- Süreli bağlantılar tek tek iptal edilemez (içerik değişince veya süresi dolunca geçersiz olurlar), en çok bir gün
  yaşar ve kaç kez açıldıkları sayılmaz.
- Veritabanı deposu içeriği belleğe alır. Dosya sistemi ve S3 depolarında içerik kayıtla aynı transaction'da değildir:
  silinen içerik commit'ten sonra kaldırılır, yazılan içerik ise kayıttan önce yazılır (kayıt kaydedilemezse sahipsiz
  kalabilir).
- S3 deposu kovayı oluşturmaz ve `Range` isteklerini desteklemez; gerçek AWS S3'e karşı sınanmadı.

## Ayrıca bakın

- [Archetype'lar](archetypes.md): `MapCrud` ve öteki kaynak biçimleri.
- [İlişkiler](relationships.md): çoktan-çoğa bağ, ebeveyn altında yaşayan kaynaklar.
- [Arka plan işleri](jobs.md): dışa aktarılan dosyalar aynı depoda durur.
- [Üretime hazırlık](production.md): Data Protection anahtarları, depolama.
- [Bildirim dizini](../reference/declarations.md)
