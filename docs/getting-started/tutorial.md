# Eğitim: ilk API'niz

Bu eğitimde sıfırdan küçük bir **proje ve iş takibi** API'si kuracaksınız. Projeler ve onların altında işler (issue)
olacak; işler açık, sürüyor ve çözüldü durumlarından geçecek, numara alacak, süzülüp sıralanabilecek ve eşzamanlı
düzenlemeye karşı korunacak. Yazacağınız kod 100 satırı geçmeyecek; geri kalanını Haldus üretecek.

Bitmiş uygulama depoda [samples/Haldus.Sample.Tracker](../../samples/Haldus.Sample.Tracker) klasöründedir. Bu sayfadaki
bütün istek ve yanıtlar o uygulamadan alınmıştır.

## İçindekiler

- [Başlamadan önce](#başlamadan-önce)
- [1. Projeyi oluşturmak](#1-projeyi-oluşturmak)
- [2. Veritabanını başlatmak](#2-veritabanını-başlatmak)
- [3. Projeler kaynağı](#3-projeler-kaynağı)
- [4. Tabloları oluşturmak](#4-tabloları-oluşturmak)
- [5. Uçları denemek](#5-uçları-denemek)
- [6. Ne üretildi?](#6-ne-üretildi)
- [7. Projenin altında işler](#7-projenin-altında-işler)
- [8. Durum ve geçişler](#8-durum-ve-geçişler)
- [9. Süzme, sıralama ve eşzamanlılık](#9-süzme-sıralama-ve-eşzamanlılık)
- [10. API şeması](#10-api-şeması)
- [Sonraki adımlar](#sonraki-adımlar)

## Başlamadan önce

| Gereken | Nasıl denetlenir |
|---|---|
| .NET 10 SDK | `dotnet --version` 10 ile başlamalı. |
| Docker | `docker --version`. PostgreSQL'i kapsayıcıda çalıştıracağız. |
| EF Core aracı | `dotnet ef --version`. Yoksa: `dotnet tool install --global dotnet-ef` |
| Haldus paketleri | [Kurulum](installation.md#paketi-eklemek) sayfasındaki gibi yerel bir klasöre üretilmiş olmalı. |

Örneklerdeki istekleri `curl` ile, bir HTTP istemcisiyle ya da IDE'nizin `.http` dosyalarıyla gönderebilirsiniz.

## 1. Projeyi oluşturmak

Boş bir web projesi oluşturun ve paketleri ekleyin:

```bash
dotnet new web -n Tracker
cd Tracker
dotnet add package Haldus.AspNetCore --version 0.1.0-preview.1
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add package Microsoft.EntityFrameworkCore.Design
```

`Microsoft.EntityFrameworkCore.Design` yalnızca migration'ları üreten araç içindir. Haldus paketleri yerel bir
klasördeyse projenin yanına [kurulumdaki](installation.md#paketi-eklemek) `nuget.config` dosyasını koymayı unutmayın.

Üretilen kodu görebilmek için proje dosyasına (`Tracker.csproj`) şunu ekleyin:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
</PropertyGroup>
```

## 2. Veritabanını başlatmak

PostgreSQL'i bir kapsayıcıda başlatın. Parolayı kendiniz seçin:

```bash
docker run -d --name tracker-db -e POSTGRES_PASSWORD=<parola> -e POSTGRES_DB=tracker -p 127.0.0.1:5432:5432 postgres:17-alpine
```

Bağlantı dizesini kaynak koda yazmak yerine kullanıcı sırlarına koyun:

```bash
dotnet user-secrets init
dotnet user-secrets set ConnectionStrings:Default "Host=127.0.0.1;Port=5432;Database=tracker;Username=postgres;Password=<parola>"
```

## 3. Projeler kaynağı

Haldus'ta bir kaynak iki parçadan oluşur: entity'nin sizin yazdığınız yarısı ve onu eşleyen bir `Map*` çağrısı.

Entity'yi `partial` bir sınıf olarak yazın. Yalnızca kendi alanlarınızı yazarsınız; anahtar (`Id`) ve sonradan
ekleyeceğimiz alanları Haldus üretir. Alanlardaki doğrulama kuralları (`[Required]`, `[MaxLength]`) üretilen istek
tiplerine kopyalanır.

<!-- example: step3 step7 step8 final -->
```csharp
// Project.cs
using System.ComponentModel.DataAnnotations;

namespace Tracker;

public partial class Project
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    [MaxLength(1000)]
    public string? Description { get; set; }
}
```

Veritabanı bağlamı da `partial` olmalıdır; Haldus eşlenen her entity'nin `DbSet` özelliğini onun öteki yarısına ekler:

<!-- example: step3 step7 step8 final -->
```csharp
// AppDbContext.cs
using Microsoft.EntityFrameworkCore;

namespace Tracker;

public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
```

Son olarak `Program.cs` dosyasını şöyle değiştirin:

<!-- example: step3 -->
```csharp
// Program.cs
using Haldus;
using Microsoft.EntityFrameworkCore;
using Tracker;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();

var app = builder.Build();

app.UseHaldus();
app.MapCrud<Project>();

app.Run();
```

`AddHaldus` Haldus'un servislerini kaydeder, `UseHaldus` isteğin dilini çözer (hata mesajları İngilizce veya Türkçe
döner). `MapCrud<Project>()` ise derleme sırasında okunur: generator bu satırı görür ve projeler için bütün kodu üretir.

## 4. Tabloları oluşturmak

Haldus tabloları kendiliğinden oluşturmaz; şemayı EF Core migration'larıyla yönetirsiniz. Haldus'un ürettiği model
sıradan bir EF Core modelidir, bu yüzden araç onu olduğu gibi görür:

```bash
dotnet ef migrations add Initial
dotnet ef database update
```

`Migrations` klasörüne bakarsanız `Projects` tablosunu görürsünüz. Haldus bir özelliğin ihtiyaç duyduğu kendi
tablolarını da (ör. numaralar için `HaldusCounters`) aynı migration'a ekler. Ayrıntısı:
[veritabanı ve migration'lar](../guides/database.md).

## 5. Uçları denemek

Uygulamayı çalıştırın:

```bash
dotnet run
```

Bir proje oluşturun:

```bash
curl -i http://localhost:5080/projects -H "Content-Type: application/json" -d '{"name":"Web sitesi","description":"Kurumsal sitenin yenilenmesi"}'
```

(Bağlantı noktası `Properties/launchSettings.json` dosyasındaki değerdir; sizinki farklı olabilir.)

```http
HTTP/1.1 201 Created
Location: /projects/01a21347-0237-76b8-8f9b-7ce8db060eed
Content-Type: application/json; charset=utf-8
```

```json
{
  "id": "01a21347-0237-76b8-8f9b-7ce8db060eed",
  "name": "Web sitesi",
  "description": "Kurumsal sitenin yenilenmesi"
}
```

Kimliği Haldus üretti: zamana göre sıralı bir GUID (v7). Projeleri listeleyin:

```http
GET /projects
```

```json
{
  "items": [
    {
      "id": "01a21347-0237-76b8-8f9b-7ce8db060eed",
      "name": "Web sitesi",
      "description": "Kurumsal sitenin yenilenmesi"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1
}
```

Liste her zaman sayfalıdır: `?page=2&pageSize=50` (en çok 100). Şimdi bir doğrulama hatası deneyin; ad zorunlu:

```http
POST /projects
Content-Type: application/json
Accept-Language: tr

{ "name": "" }
```

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Bir veya daha fazla doğrulama hatası oluştu.",
  "status": 400,
  "errors": {
    "name": ["Name alanı zorunludur."]
  }
}
```

Hata alan düzeyindedir (`errors.name`) ve `Accept-Language` başlığının dilindedir; başlık olmadan İngilizce döner.

Geri kalan uçlar da hazırdır:

| İstek | Etkisi |
|---|---|
| `GET /projects/{id}` | Tek proje; yoksa 404. |
| `PUT /projects/{id}` | Bütün alanları değiştirir. |
| `PATCH /projects/{id}` | Yalnızca gönderilen alanları değiştirir; `null` gönderilen alan boşaltılır. |
| `DELETE /projects/{id}` | Siler; 204 döner. |

## 6. Ne üretildi?

Derlemeden sonra `obj/Generated/Haldus.Generators/Haldus.Generators.HaldusGenerator/` klasörüne bakın:

| Dosya | İçeriği |
|---|---|
| `Tracker.Project.Entity.g.cs` | `Project` sınıfının öteki yarısı: `Id`. |
| `Tracker.Project.Dtos.g.cs` | `ProjectResponse`, `CreateProjectRequest`, `UpdateProjectRequest`, `PatchProjectRequest`. |
| `Tracker.Project.Endpoints.g.cs` | Beş uç ve onların kuralları. |
| `HaldusModule.g.cs` | EF Core yapılandırması ve kayıtlar. |
| `Tracker.AppDbContext.DbSets.g.cs` | `public DbSet<Project> Projects`. |

Bu dosyaları değiştirmezsiniz. Davranışı değiştirmek için `Map*` zincirini veya entity'nin kendi yarısını
değiştirirsiniz; dosyalar bir sonraki derlemede yeniden üretilir. Nasıl çalıştığının ayrıntısı:
[Haldus nasıl çalışır](../concepts/how-it-works.md).

## 7. Projenin altında işler

Her işin bir projesi var ve işlere proje üzerinden ulaşmak istiyoruz: `/projects/{projectId}/issues`. Bunu
`Under<Project>()` söyler. İş entity'sini yazın:

<!-- example: step7 -->
```csharp
// Issue.cs
using System.ComponentModel.DataAnnotations;

namespace Tracker;

public partial class Issue
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    [MaxLength(4000)]
    public string? Description { get; set; }

    [Range(1, 5)]
    public int Priority { get; set; } = 3;

    [MaxLength(100)]
    public string? Assignee { get; set; }
}
```

`Program.cs` dosyasına tek satır ekleyin:

<!-- example: step7 -->
```csharp
// Program.cs
using Haldus;
using Microsoft.EntityFrameworkCore;
using Tracker;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();

var app = builder.Build();

app.UseHaldus();
app.MapCrud<Project>();
app.MapCrud<Issue>().Under<Project>();

app.Run();
```

Haldus işe `ProjectId` yabancı anahtarını ekledi. Bu alan istek gövdesinde yer almaz; adresten gelir. Şemayı
güncelleyin ve bir iş oluşturun:

```bash
dotnet ef migrations add AddIssues
dotnet ef database update
```

```http
POST /projects/01a21347-0237-76b8-8f9b-7ce8db060eed/issues
Content-Type: application/json

{ "title": "Giriş sayfası açılmıyor", "priority": 4, "assignee": "ayse" }
```

```json
{
  "id": "01a21347-039e-75ae-96eb-a53b932cfc4a",
  "title": "Giriş sayfası açılmıyor",
  "description": null,
  "priority": 4,
  "assignee": "ayse",
  "projectId": "01a21347-0237-76b8-8f9b-7ce8db060eed"
}
```

Proje silinince işleri de silinir (`Under` için varsayılan `OnDelete.Cascade`). Başka bir projenin işine o projenin
adresi dışından ulaşılamaz. İlişki türlerinin hepsi: [ilişkiler](../guides/relationships.md).

## 8. Durum ve geçişler

Bir iş açık başlar, üzerinde çalışılır ve çözülür. Durumu istemcinin gönderdiği bir alanla değil, adlandırılmış
geçişlerle değiştirmek istiyoruz; böylece kurallar sunucuda kalır. Durumları bir enum olarak yazın ve çözüm notunu
taşıyan geçişin istek tipini ekleyin. `Issue.cs` dosyasının yeni hali:

<!-- example: step8 final -->
```csharp
// Issue.cs
using System.ComponentModel.DataAnnotations;
using Haldus;

namespace Tracker;

public enum IssueStatus
{
    Open,
    InProgress,
    Resolved,
}

public partial class Issue
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    [MaxLength(4000)]
    public string? Description { get; set; }

    [Range(1, 5)]
    public int Priority { get; set; } = 3;

    [MaxLength(100)]
    public string? Assignee { get; set; }

    // Herkese açık setter'ı olmadığı için yalnızca yanıtta görünür; onu ResolveIssue geçişi yazar.
    public string? Resolution { get; private set; }

    public void Resolve(string resolution) => Resolution = resolution;
}

// POST /projects/{projectId}/issues/{id}/resolve isteğinin gövdesi ve etkisi.
public sealed class ResolveIssue : ITransition<Issue>
{
    [Required]
    [MaxLength(500)]
    public string Resolution { get; init; } = "";

    public void Apply(Issue issue) => issue.Resolve(Resolution);
}
```

`Program.cs` içinde işlerin zincirini genişletin ve geçişleri tanımlayın:

<!-- example: step8 -->
```csharp
// Program.cs
using Haldus;
using Microsoft.EntityFrameworkCore;
using Tracker;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();

var app = builder.Build();

app.UseHaldus();
app.MapCrud<Project>();

var issues = app.MapCrud<Issue>()
    .Under<Project>()
    .Workflow<IssueStatus>(initial: IssueStatus.Open)
    .EditableIn(IssueStatus.Open, IssueStatus.InProgress)
    .DeletableIn(IssueStatus.Open)
    .Numbered("ISS-{seq:5}");

issues.MapTransition("start", from: IssueStatus.Open, to: IssueStatus.InProgress);
issues.MapTransition<ResolveIssue>(from: [IssueStatus.Open, IssueStatus.InProgress], to: IssueStatus.Resolved);
issues.MapTransition("reopen", from: IssueStatus.Resolved, to: IssueStatus.Open);

app.Run();
```

| Satır | Etkisi |
|---|---|
| `Workflow<IssueStatus>(initial: ...)` | `Status` alanı üretilir. Yeni iş `Open` doğar; alan istek gövdesinden yazılamaz. |
| `EditableIn(...)` | `PUT` ve `PATCH` yalnızca bu durumlarda çalışır; başka durumda 409. |
| `DeletableIn(...)` | Silme yalnızca açık işte çalışır. |
| `Numbered("ISS-{seq:5}")` | Boşluksuz, benzersiz bir `Number` alanı: `ISS-00001`, `ISS-00002`... |
| `MapTransition("start", ...)` | `POST /projects/{projectId}/issues/{id}/start`. Gövdesi yoktur. |
| `MapTransition<ResolveIssue>(...)` | `POST .../resolve`. Adres istek tipinin adından türetilir; gövde `ResolveIssue`'dur ve doğrulanır, sonra `Apply` çalışır. |

Haldus geçişte sırayla şunları yapar: kaydı bulur, durumunu denetler, iş mantığını çalıştırır, yeni durumu yazar ve
kaydeder. Siz yalnızca mantığı yazarsınız (`Apply`).

Şemayı güncelleyin (`dotnet ef migrations add AddIssueWorkflow`, `dotnet ef database update`) ve deneyin. Yeni işin
yanıtında durum ve numara vardır:

```json
{
  "id": "01a21347-039e-75ae-96eb-a53b932cfc4a",
  "title": "Giriş sayfası açılmıyor",
  "description": null,
  "priority": 4,
  "assignee": "ayse",
  "resolution": null,
  "projectId": "01a21347-0237-76b8-8f9b-7ce8db060eed",
  "status": "Open",
  "number": "ISS-00001"
}
```

İşi başlatın, sonra bir daha başlatmayı deneyin:

```http
POST /projects/01a21347-0237-76b8-8f9b-7ce8db060eed/issues/01a21347-039e-75ae-96eb-a53b932cfc4a/start
```

İlki `200 OK` ve `"status": "InProgress"` döner. İkincisi geçişin izin verdiği durumda değildir:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Not allowed in the current state",
  "status": 409,
  "detail": "'start' is not allowed while the record is in the 'InProgress' state."
}
```

İşi çözün:

```http
POST /projects/01a21347-0237-76b8-8f9b-7ce8db060eed/issues/01a21347-039e-75ae-96eb-a53b932cfc4a/resolve
Content-Type: application/json

{ "resolution": "Oturum çerezi düzeltildi" }
```

```json
{
  "id": "01a21347-039e-75ae-96eb-a53b932cfc4a",
  "title": "Giriş sayfası açılmıyor",
  "description": null,
  "priority": 4,
  "assignee": "ayse",
  "resolution": "Oturum çerezi düzeltildi",
  "projectId": "01a21347-0237-76b8-8f9b-7ce8db060eed",
  "status": "Resolved",
  "number": "ISS-00001"
}
```

Boş bir çözüm notu 400 alır (`errors.resolution`): geçişin istek tipi de doğrulanır. Çözülmüş işi düzenlemek veya
silmek 409 alır (`'update' is not allowed while the record is in the 'Resolved' state.`); `reopen` onu yeniden açar.

İş mantığını yazmanın öteki yolları (bağımlılık alan handler sınıfları, lambdalar) ve reddetme (`422`):
[iş mantığı](../guides/business-logic.md). Durum makinesinin bütün seçenekleri: [durum makineleri](../guides/state-machines.md).

## 9. Süzme, sıralama ve eşzamanlılık

Son adımda dört şey ekleyeceğiz:

- işleri sorumlusuna, başlığına ve önceliğine göre süzmek,
- işleri önceliğe ve oluşturulma zamanına göre sıralamak,
- iki kişinin aynı işi aynı anda düzenleyip birbirinin değişikliğini ezmesini önlemek,
- her projede kaç iş olduğunu göstermek.

Süzme için bir filtre sınıfı yazın. Herkese açık özellikleri liste ucunun isteğe bağlı sorgu parametreleri olur:

<!-- example: final -->
```csharp
// IssueFilter.cs
using Haldus;

namespace Tracker;

// Liste ucunun sorgu parametreleri: ?assignee=ayse&search=giriş&minPriority=4
public sealed class IssueFilter : IFilter<Issue>
{
    public string? Assignee { get; set; }

    public string? Search { get; set; }

    public int? MinPriority { get; set; }

    public IQueryable<Issue> Apply(IQueryable<Issue> query) => query
        .WhereIf(Assignee is not null, issue => issue.Assignee == Assignee)
        .WhereIf(Search is not null, issue => issue.Title.ToLower().Contains(Search!.ToLower()))
        .WhereIf(MinPriority is not null, issue => issue.Priority >= MinPriority);
}
```

`Program.cs` dosyasının son hali:

<!-- example: final -->
```csharp
// Program.cs
using Haldus;
using Microsoft.EntityFrameworkCore;
using Tracker;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseHaldus();
app.MapOpenApi();

app.MapCrud<Project>()
    .Auditable()
    .Counts<Issue>()
    .Sortable(project => project.Name, project => project.CreatedAt);

var issues = app.MapCrud<Issue>()
    .Under<Project>()
    .Auditable()
    .ConcurrencyChecked()
    .Workflow<IssueStatus>(initial: IssueStatus.Open)
    .EditableIn(IssueStatus.Open, IssueStatus.InProgress)
    .DeletableIn(IssueStatus.Open)
    .Numbered("ISS-{seq:5}")
    .Filterable<IssueFilter>()
    .Sortable(issue => issue.Priority, issue => issue.CreatedAt);

issues.MapTransition("start", from: IssueStatus.Open, to: IssueStatus.InProgress);
issues.MapTransition<ResolveIssue>(from: [IssueStatus.Open, IssueStatus.InProgress], to: IssueStatus.Resolved);
issues.MapTransition("reopen", from: IssueStatus.Resolved, to: IssueStatus.Open);

app.Run();
```

| Satır | Etkisi |
|---|---|
| `Auditable()` | `CreatedAt` ve `UpdatedAt` alanları; Haldus doldurur. |
| `Counts<Issue>()` | Projeye `IssueCount` alanı ekler; bir iş eklenince veya silinince yeniden sayılır. |
| `Sortable(...)` | `?sort=-priority,createdAt` (eksi azalan sıra). Yalnızca sayılan alanlara göre. |
| `ConcurrencyChecked()` | Kaydın sürümü `ETag` başlığında döner; değiştiren her istek onu `If-Match` başlığında geri gönderir. |
| `Filterable<IssueFilter>()` | Filtre sınıfı liste ucuna bağlanır. |
| `AddOpenApi()`, `MapOpenApi()` | API şeması: `GET /openapi/v1.json`. |

Şemayı güncelleyin (`dotnet ef migrations add AddAuditingAndCounts`, `dotnet ef database update`) ve deneyin.

**Süzme ve sıralama.** Ayşe'nin işlerini, en yüksek öncelik başta:

```http
GET /projects/01a21347-0237-76b8-8f9b-7ce8db060eed/issues?assignee=ayse&sort=-priority
```

Sayılmamış bir alana göre sıralamak 400 alır:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "sort": ["Cannot sort by 'title'. Sortable fields: priority, createdAt."]
  }
}
```

Durum makinesi olan kaynakta `?status=Open` süzgeci kendiliğinden gelir.

**Eşzamanlılık.** Artık her iş yanıtında bir `ETag` başlığı var:

```http
GET /projects/01a21347-0237-76b8-8f9b-7ce8db060eed/issues/01a21347-039e-75ae-96eb-a53b932cfc4a

HTTP/1.1 200 OK
ETag: "744"
```

Değiştiren bir isteği `If-Match` başlığı olmadan gönderirseniz 428 alırsınız:

```json
{
  "title": "Version required",
  "status": 428,
  "detail": "Send the version you last read in the If-Match header."
}
```

Okuduğunuz sürümle gönderin:

```http
POST /projects/01a21347-0237-76b8-8f9b-7ce8db060eed/issues/01a21347-039e-75ae-96eb-a53b932cfc4a/start
If-Match: "744"
```

İstek geçer ve yanıt yeni sürümü taşır (`ETag: "747"`). Bu arada başka biri kaydı değiştirmişse elinizdeki sürüm eskidir
ve istek 412 alır:

```json
{
  "title": "Resource has changed",
  "status": 412,
  "detail": "The resource was modified after you last read it. Reload it and try again."
}
```

**Toplanan alan.** Proje artık işlerinin sayısını taşır:

```json
{
  "id": "01a21347-0237-76b8-8f9b-7ce8db060eed",
  "name": "Web sitesi",
  "description": "Kurumsal sitenin yenilenmesi",
  "createdAt": "2026-11-25T18:15:50.55078+03:00",
  "updatedAt": null,
  "issueCount": 2
}
```

## 10. API şeması

`GET /openapi/v1.json` bütün uçları, istek ve yanıt tiplerini, doğrulama kurallarını içeren OpenAPI belgesini döndürür.
Haldus frontend kodu üretmez; bu şemadan TypeScript istemcisi üretebilirsiniz. Şemayı derleme sırasında bir dosyaya
yazdırmak ve istemci üretmek: [frontend ve mobil istemciler](../guides/frontend.md).

## Sonraki adımlar

Yazdığınız kod: iki entity yarısı, bir enum, bir geçiş isteği, bir filtre ve `Program.cs`. Haldus bunlardan 15 uç,
istek ve yanıt tipleri, doğrulama, durum denetimi, numaralama, eşzamanlılık denetimi ve EF Core yapılandırması üretti.

- [Haldus nasıl çalışır](../concepts/how-it-works.md): generator, dört katman ve neyin ne zaman üretildiği.
- [Ortak kurallar](../concepts/conventions.md): adresler, anahtarlar, `PUT` ile `PATCH`, hata yanıtları.
- [Kiracılar, kullanıcılar ve yetkilendirme](../guides/tenancy-and-ownership.md): kayıtları kiracıya veya kullanıcıya bağlamak.
- [Uygulamanızı test etmek](../guides/testing.md): bu uygulama için uçtan uca bir test.
- [Bildirim dizini](../reference/declarations.md): Haldus'un bütün bildirimleri.
