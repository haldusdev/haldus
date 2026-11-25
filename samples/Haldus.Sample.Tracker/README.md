# Proje ve iş takibi

[Eğitimin](../../docs/getting-started/tutorial.md) bitmiş uygulaması: projeler ve onların altında, durumları geçişlerle
değişen, numaralı, süzülüp sıralanabilen ve eşzamanlı düzenlemeye karşı korunan işler. Haldus'u ilk kez görüyorsanız
buradan başlayın; örnek mağazanın aksine küçük ve migration'larıyla birlikte gelir.

| Dosya | İçeriği |
|---|---|
| `Program.cs` | Servisler ve bütün `Map*` bildirimleri. |
| `Project.cs`, `Issue.cs` | Entity'lerin sizin yazdığınız yarısı; durumlar (`IssueStatus`) ve çözme geçişi (`ResolveIssue`). |
| `IssueFilter.cs` | Liste ucunun sorgu parametreleri. |
| `AppDbContext.cs` | `partial` veritabanı bağlamı. |
| `Migrations/` | EF Core migration'ları; Haldus'un tabloları dahil. |

Depo içinde Haldus'a paket yerine projeyle başvurulur (`Haldus.Sample.Tracker.csproj`); kendi uygulamanızda bunun yerine
`Haldus.AspNetCore` paketini eklersiniz.

## Çalıştırmak

PostgreSQL'i başlatın (parolayı kendiniz seçin):

```bash
docker run -d --name tracker-db -e POSTGRES_PASSWORD=<parola> -e POSTGRES_DB=tracker -p 127.0.0.1:5432:5432 postgres:17-alpine
```

Bağlantı dizesi kaynak kodda durmaz; ortamdan veya kullanıcı sırlarından okunur. PowerShell'de:

```powershell
$env:ConnectionStrings__Default = "Host=127.0.0.1;Port=5432;Database=tracker;Username=postgres;Password=<parola>"
dotnet ef database update --project samples/Haldus.Sample.Tracker
dotnet run --project samples/Haldus.Sample.Tracker --urls http://localhost:5080
```

`dotnet ef` aracı yoksa: `dotnet tool install --global dotnet-ef`.

## Denemek

```bash
curl -i http://localhost:5080/projects -H "Content-Type: application/json" -d '{"name":"Web sitesi"}'
```

Bütün uçlar `GET /openapi/v1.json` şemasındadır. Uçların ne yaptığı ve örnek istek ve yanıtlar eğitimdedir.
