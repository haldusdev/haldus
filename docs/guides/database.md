# Veritabanı ve migration'lar

Haldus'un ürettiği model sıradan bir EF Core modelidir. Şemayı oluşturmak ve değiştirmek için EF Core migration'larını
kullanırsınız; Haldus tabloları kendiliğinden oluşturmaz ve değiştirmez. Bu sayfa veritabanı gereksinimini, migration
akışını, Haldus'un kendi tablolarını ve zinciri değiştirirken dikkat edilecekleri anlatır.

## İçindekiler

- [PostgreSQL](#postgresql)
- [Migration akışı](#migration-akışı)
- [Şemada ne olur](#şemada-ne-olur)
- [Haldus'un tabloları](#haldusun-tabloları)
- [Zinciri değiştirmek](#zinciri-değiştirmek)
- [Migration'ları uygulamak](#migrationları-uygulamak)
- [Birden çok bağlam](#birden-çok-bağlam)
- [Sınırlar](#sınırlar)

## PostgreSQL

Haldus yalnızca PostgreSQL ile çalışır. Boşluksuz numaralar, sıralı kayıtlar ve bakiyeler gibi özellikler PostgreSQL'e
özgü SQL ve danışma kilitleri (`pg_advisory_xact_lock`) kullanır; eşzamanlılık denetimi PostgreSQL'in `xmin` sistem
sütununa dayanır. Sağlayıcı [Npgsql](https://www.npgsql.org/efcore/)'dir:

<!-- example: provider -->
```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();

app.MapCrud<Product>();

public partial class Product
{
    public string Name { get; set; } = "";
}
```

`AddHaldus<AppDbContext>()` bağlamın seçeneklerine Haldus'u ekler: model katkıları (Haldus'un entity'leri ve kendi
tabloları) ve kaydetme sırasında çalışan interceptor. Bağlamı kendiniz kaydedersiniz; bağlantı dizesi, havuz, yeniden
deneme gibi Npgsql ayarları sizindir.

## Migration akışı

Migration'ları üreten araç ve paket:

```bash
dotnet tool install --global dotnet-ef
dotnet add package Microsoft.EntityFrameworkCore.Design
```

Bir değişiklikten sonra:

```bash
dotnet ef migrations add AddIssues
dotnet ef database update
```

Araç uygulamanızı derler ve `Program.cs`'teki host'u kurarak bağlamı oluşturur; Haldus'un model katkıları
`AddHaldus` ile kaydedildiği için migration'a girer. `migrations add` için veritabanına bağlanmak gerekmez;
`database update` bağlantı dizesini kullanıcı sırlarından veya ortamdan okur
(`ConnectionStrings__Default`).

[Eğitimdeki](../getting-started/tutorial.md) uygulamanın ilk migration'ı (`samples/Haldus.Sample.Tracker/Migrations`)
iki kaynak tablosunu ve numaralar için Haldus'un sayaç tablosunu oluşturur:

<!-- fragment -->
```csharp
migrationBuilder.CreateTable(name: "HaldusCounters", ...);
migrationBuilder.CreateTable(name: "Projects", ...);
migrationBuilder.CreateTable(name: "Issues", ...);
migrationBuilder.CreateIndex(name: "IX_Issues_Number", table: "Issues", column: "Number", unique: true);
```

## Şemada ne olur

| Haldus'ta | Şemada |
|---|---|
| Tablo adı | Kaynağın çoğul adı, PascalCase: `Issues`, `StockMovements`. Haldus adı açıkça verir (`ToTable`); başka bir modülden gelen entity'de de aynıdır. |
| `Id` | Birincil anahtar; varsayılan `uuid` (sürüm 7, uygulama üretir). |
| Durum (`Workflow`, `Axis`) | Adıyla saklanan metin sütunu (`character varying(64)`). Enum'a üye eklemek şemayı değiştirmez; üye adını değiştirmek eski satırları geçersiz kılar. |
| `ConcurrencyChecked` | Yeni sütun yok; PostgreSQL'in `xmin` sistem sütunu sürümdür. |
| `References<T>()`, `Under<T>()` | Yabancı anahtar sütunu, kısıtı ve indeksi; silme davranışı bildirimdeki `OnDelete`'tir. |
| `Embeds<T>()` | Sahibin tablosunda kolonlar (EF Core complex property). |
| `Metadata`, `CustomFields`, `Translatable` | `jsonb` sütunları. |
| `NaturalKey`, `Numbered` | Benzersiz indeks. |
| `MapAssociation<A, B>()` | Bağ tablosu (`ProductTags`): iki yabancı anahtardan oluşan birincil anahtar. |

## Haldus'un tabloları

Bazı özellikler Haldus'un kendi tablolarını kullanır. Bir tablo modele yalnızca onu kullanan bir kaynak varsa girer;
hiçbirini kullanmayan bir uygulamanın şemasında Haldus'a ait tablo yoktur.

| Tablo | Onu getiren |
|---|---|
| `HaldusCounters` | Boşluksuz `Numbered` (varsayılan). `gapless: false` bir veritabanı sequence'i kullanır. |
| `HaldusIdempotencyRecords` | `Idempotent()` |
| `HaldusOutbox` | `PublishesEvents()` |
| `HaldusRevisions` | `Revisioned()` |
| `HaldusChanges` | `ChangeFeed()` |
| `HaldusApprovalDecisions` | `Approvable()` |
| `HaldusFileContents` | `MapFile`, içerik veritabanında saklandığında (varsayılan) |
| `HaldusCustomFieldDefinitions` | `CustomFields()` |
| `HaldusChangeSets` | `ChangeSets(...)` |
| `HaldusReadReceipts` | `ReadReceipts()` |
| `HaldusOccurrenceExceptions` | `Recurring()` |
| `HaldusBalanceEntries` | `MapBalance` (defter) |
| `HaldusBalanceHolds` | `MapBalance().Holds(...)` |
| `HaldusJobQueue` | `AddHaldusPostgreSqlJobs<T>()` |

Bu tablolar migration'larınıza girer ve onlarla birlikte yönetilir. Elle değiştirmeyin.

## Zinciri değiştirmek

Zincire bir bildirim eklemek veya çıkarmak çoğu zaman şemayı değiştirir. Her değişiklikten sonra yeni bir migration
üretin ve üretilen dosyayı okuyun:

| Değişiklik | Şemadaki etkisi | Dikkat |
|---|---|---|
| `.Auditable()` eklemek | `CreatedAt`, `UpdatedAt` sütunları | Var olan satırlarda `CreatedAt` sütunun varsayılan değerini alır. |
| `.MultiTenant()` eklemek | Zorunlu `TenantId` sütunu | Var olan satırlar bir kiracıya atanmadan görünmez olur; migration'a bir veri adımı ekleyin. |
| `.SoftDeletable()` eklemek | `DeletedAt` sütunu | Yok. |
| `.Workflow<T>()` eklemek | Zorunlu `Status` sütunu | Var olan satırlar boş bir durum alır; başlangıç durumunu yazan bir veri adımı ekleyin. |
| Bir durumu yeniden adlandırmak | Yok | Satırlarda eski ad durur; migration'da güncelleyin. |
| `.Numbered(...)` eklemek | `Number` sütunu ve benzersiz indeks | Var olan satırlara numara verilmeden indeks kurulamaz. |
| Bir kaynağı kaldırmak | Tablo silinir | Veri kaybıdır. |

Veri adımları sıradan EF Core migration kodudur (`migrationBuilder.Sql(...)`).

## Migration'ları uygulamak

Üretimde migration'ları uygulamanın açılışında değil dağıtım adımında uygulamak genellikle daha güvenlidir:
`dotnet ef database update` ya da bir migration paketi (`dotnet ef migrations bundle`). Bunlar EF Core'un kendi
yollarıdır; Haldus bunlara bir şey eklemez.

Açılışta uygulamak isterseniz bir koşula dikkat edin. Şemayı derleme sırasında dosyaya yazdıran
`Microsoft.Extensions.ApiDescription.Server` paketi uygulamanın açılış kodunu çalıştırır (bkz.
[frontend ve mobil istemciler](frontend.md)); o sırada veritabanı olmayabilir. Haldus bu durumu
`HaldusJobs.IsGeneratingOpenApiDocument` ile söyler:

<!-- example: migrate-on-start -->
```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();

var app = builder.Build();

// Derleme sırasındaki şema üretimi açılış kodunu da çalıştırır; o sırada veritabanına bağlanılmaz.
if (!HaldusJobs.IsGeneratingOpenApiDocument)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseHaldus();
app.MapCrud<Product>();
app.Run();

public partial class Product
{
    public string Name { get; set; } = "";
}
```

`EnsureCreated()` migration geçmişi tutmaz ve var olan tabloları değiştirmez; yalnızca deneme ve test veritabanları
için uygundur.

## Birden çok bağlam

[Modüler](modules.md) bir uygulamada her modülün kendi bağlamı olabilir (`AddHaldusModule<TContext>()`). Her bağlamın
migration'ları ayrıdır; araçta bağlamı adıyla seçersiniz:

```bash
dotnet ef migrations add Initial --context CatalogDbContext --output-dir Migrations/Catalog
dotnet ef database update --context CatalogDbContext
```

Haldus'un tabloları onları kullanan kaynağın bağlamına girer. PostgreSQL iş kuyruğu (`HaldusJobQueue`) tek bir
bağlamdadır: `AddHaldusPostgreSqlJobs<TContext>()` çağrısındaki bağlam.

## Sınırlar

- Yalnızca PostgreSQL desteklenir. SQL Server, MySQL veya SQLite sağlayıcıları çalışmaz.
- Haldus var olan veriyi zincirdeki bir değişikliğe göre dönüştürmez; veri adımlarını siz yazarsınız.
- Örnek mağaza (`samples/Haldus.Sample.Shop`) bilerek migration taşımaz ve tabloları deneme amaçlı oluşturur
  (`Shop:CreateDatabase`); bu bir öneri değildir. Migration'lı örnek için eğitimin uygulamasına bakın
  (`samples/Haldus.Sample.Tracker`).

## Ayrıca bakın

- [Eğitim: ilk API'niz](../getting-started/tutorial.md#4-tabloları-oluşturmak)
- [Üretime hazırlık](production.md)
- [Modüler yapı](modules.md)
