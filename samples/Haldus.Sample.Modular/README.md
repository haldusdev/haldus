# Modüler örnek

Özelliklerin ayrı sınıf kütüphanelerine bölündüğü küçük bir uygulama. Her modül kendi `Map*` çağrılarını ve
entity'lerini taşır; bazılarının kendi veritabanı bağlamı vardır, bazıları ortak bağlamı kullanır. Konunun anlatımı:
[modüler yapı](../../docs/guides/modules.md).

| Proje | Bağlamı | Neyi gösterir |
|---|---|---|
| `Haldus.Sample.Modular.Catalog` | Kendi (`CatalogDbContext`) | `AddHaldusModule<TContext>()` ile kendi şeması olan modül. |
| `Haldus.Sample.Modular.Sales` | Kendi (`SalesDbContext`) | Başka bir modülün kaydına ayrı bağlamdan, yalnızca kimlikle referans. |
| `Haldus.Sample.Modular.Support` | Ortak | Kendi bağlamı olmayan modül; entity'leri ana projenin bağlamına girer. |
| `Haldus.Sample.Modular.Host` | Ortak (`HostDbContext`) | Uygulamanın kendisi: `AddHaldus<HostDbContext>()`, modüllerin eşlenmesi. |

Her sınıf kütüphanesi generator'a analyzer olarak başvurur; böylece kendi kodunu kendisi üretir. Modüllerin ayrıca
kaydedilmesi gerekmez: ana projenin başvurduğu modüller kendiliğinden bulunur.

## Çalıştırmak

PowerShell'de, çalışan bir PostgreSQL'e karşı:

```powershell
$env:ConnectionStrings__Default = "Host=127.0.0.1;Port=5432;Database=modular;Username=postgres;Password=<parola>"
dotnet run --project samples/Haldus.Sample.Modular/Haldus.Sample.Modular.Host --urls http://localhost:5090
```

Bu örnek migration taşımaz; entegrasyon testleri (`tests/Haldus.IntegrationTests`) tabloları her bağlam için doğrudan
oluşturur. Kendi uygulamanızda her bağlamın migration'ları ayrıdır; bkz.
[veritabanı ve migration'lar](../../docs/guides/database.md#birden-çok-bağlam).
