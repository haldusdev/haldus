# Haldus.LoadTest

Çalışan [örnek mağazayı](../../samples/Haldus.Sample.Shop) gerçek HTTP üzerinden yük altında ölçen küçük bir konsol
aracı. Bağımlılığı yoktur; istekleri `HttpClient` ile gönderir ve yanıt sürelerini ölçer.

Amaç mutlak bir kapasite sayısı vermek değildir. Haldus'ta bazı özellikler doğruluğu bir kilitle sağlar (boşluksuz
numara, sıra, bakiye, toplanan alan, türetme, dönem); araç, bunların eşzamanlılık arttıkça nasıl davrandığını kilidi
olmayan bir yazmayla yan yana gösterir. Bir ölçümün sonuçları [taksonomi sınaması](../../reports/Haldus%20taksonomi%20sınaması.md)
raporundadır.

## Çalıştırmak

Araç backend'i kendisi başlatmaz. Önce örnek mağaza, boş bir PostgreSQL veritabanına karşı ve Release kipinde
başlatılır. Araç her çalıştırmada yeni bir kiracı kullanır ve kimliği başlıklarla verir; bu yüzden backend
`Shop:DemoIdentity=true` ile açılmalıdır (bu ayar kimliği doğrulamadan okur; yalnızca test ve ölçüm içindir).
İstek başına günlük yazmak ölçümü bozduğu için günlük düzeyi yükseltilir. PowerShell'de:

```powershell
$env:ConnectionStrings__Default = "Host=127.0.0.1;Port=5432;Database=shop;Username=postgres;Password=<parola>"
$env:Shop__CreateDatabase = "true"
$env:Shop__DemoIdentity = "true"
$env:Logging__LogLevel__Default = "Warning"
dotnet run -c Release --project samples/Haldus.Sample.Shop --no-launch-profile --urls http://localhost:5080
```

Sonra, başka bir terminalde:

```bash
dotnet run -c Release --project tools/Haldus.LoadTest
```

| Seçenek | Varsayılan | Anlamı |
|---|---|---|
| `--url` | `http://localhost:5080` | Backend'in adresi |
| `--operations` | `2000` | Çalıştırma başına ölçülen işlem sayısı |
| `--warmup` | `100` | Ölçümden önce yapılan, sayılmayan işlem sayısı |
| `--concurrency` | `1,8,32` | Aynı anda istek gönderen işçi sayıları; her biri ayrı bir çalıştırmadır |
| `--scenario` | hepsi | Virgülle ayrılmış senaryo adları |
| `--export-products` | `20000` | Dışa aktarma ölçümünden önce ürün sayısının tamamlanacağı değer |

Çıktı bir Markdown tablosudur: saniyedeki işlem sayısı, yanıt süresinin ortancası, 95. ve 99. yüzdeliği, en uzun süre
ve başarısız (2xx olmayan) yanıtların durum kodlarına göre sayısı.

## Senaryolar

Her senaryoda hazırlık (paylaşılan kayıtlar, işlemin ihtiyaç duyduğu kayıt) ölçülmez; yalnızca aşağıdaki istek ölçülür.

| Senaryo | Ölçülen istek | Neyi gösterir |
|---|---|---|
| `read` | `GET /products?pageSize=20` | Taban okuma (sayfa + toplam sayım) |
| `create` | `POST /persons` | Taban yazma: kilidi ve sayacı olmayan kayıt |
| `numbered-gapless` | `POST /staff-members` | Boşluksuz numara: bütün istekler tek bir sayaç satırını bekler |
| `numbered-sequence` | `POST /refunds` | Sequence'ten gelen numara. İade ayrıca idempotency anahtarı ister; bire bir kıyas değildir |
| `orderable-append` | `POST /categories` | Sıralı kaynağa ekleme (ağacın kilidi) |
| `orderable-move` | `POST /categories/{id}/move` | 100 kardeş arasında başa taşıma; hepsi yeniden numaralanır |
| `balance-hot` | `POST /inventories/{key}/adjust` | Bütün istekler aynı bakiyeye |
| `balance-spread` | aynı | Her işçi kendi bakiyesine; kilidin anahtar başına olduğunu gösterir |
| `balance-hold` | `POST …/holds` + `POST …/holds/{id}/capture` | Tutma ve tahsilat (iki istek tek işlem sayılır) |
| `rollup-hot` | `POST /discussions/{id}/replies` | Toplanan alan: bütün yanıtlar aynı tartışmaya |
| `rollup-spread` | aynı | Her işçi kendi tartışmasına |
| `derived` | `POST /stock-movements` | Türetme: kaynak başına tek kilit altında izdüşüm |
| `effective-dated` | `POST /suppliers/{id}/supplier-rates` | Aynı kapsamda çakışmayan dönemler; kapsam çalıştırma boyunca büyür |
| `export` | `POST /product-exports` ve işin bitmesi | Dosyayı bellekte kuran dışa aktarmanın büyük tabloda süresi |

## Sonuçları okurken

- İstemci, backend ve veritabanı aynı makinedeyse birbirlerinin işlemcisini paylaşırlar; sayılar bir üst sınır değil,
  o makinedeki göreli bir kıyastır.
- Yazma süresinin tabanını veritabanının her commit'te diske yazması (`fsync`) belirler; bu, veritabanının nerede
  çalıştığına (yerel disk, kapsayıcı, sanal makine) çok bağlıdır.
- Araç veritabanını temizlemez. Art arda çalıştırmalar tabloları büyütür; kıyaslanacak ölçümler boş bir veritabanıyla
  başlatılmalıdır.
- Olay dağıtımı (`PublishesEvents` teslimatı) ölçülmez: örnek mağazada kayıtlı bir yayıncı yoktur. Yanıt senaryoları
  yalnızca olayın kutuya yazılmasını içerir.
