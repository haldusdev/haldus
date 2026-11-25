# Güvenlik politikası

## Desteklenen sürümler

Haldus 1.0 öncesindedir. Güvenlik düzeltmeleri yalnızca en son sürüme ve `main` dalına yapılır.

| Sürüm | Destek |
|---|---|
| `main` | Evet |
| `0.1.0-preview.1` | Yalnızca bir sonraki ön sürümle düzeltilir |

## Bir açığı bildirmek

Güvenlik açıklarını herkese açık bir issue olarak **bildirmeyin**. Deponun GitHub sayfasındaki **Security** sekmesinden
**Report a vulnerability** ile özel olarak bildirin.

Bildirimde şunlar yardımcı olur:

- etkilenen sürüm veya commit,
- açığa yol açan `Map*` bildirimi ve zinciri (mümkünse en küçük örnek),
- açığı gösteren istekler ve beklenen ile gerçekleşen davranış,
- etkisi: hangi veriye kimin ulaşabildiği veya neyi değiştirebildiği.

Bildirimi aldığımızı ve sonraki adımları aynı kanal üzerinden size yazarız. Düzeltme yayınlandıktan sonra, isterseniz
adınızla birlikte, değişiklik günlüğünde anılır.

## Kapsam

Haldus'un ürettiği kod ve çalışma zamanı kütüphaneleri kapsamdadır: kiracı ve kullanıcı sınırlarının aşılması, başka
bir kaydın verisinin görünmesi, yalnızca yazılan bir alanın (`Sensitive`, `Encrypted`) bir yanıtta veya olayda
görünmesi, sırların sızması, token ile erişimin amacından fazlasına izin vermesi, doğrulamanın atlanması gibi.

Örnek uygulamalar (`samples/`) gösterim içindir; onlardaki açıkları da bildirebilirsiniz, ama üretimde kullanılmak
üzere yazılmadıklarını göz önünde bulundurun.

## Haldus'un yaptığı ve uygulamanın sorumluluğu

Haldus bazı güvenlik kurallarını kendiliğinden uygular; bazıları uygulamanındır. Ayrıntılar ilgili belgelerdedir:

| Haldus uygular | Uygulamanın işi |
|---|---|
| Kiracı ve kullanıcı kapsamının her sorguya uygulanması ([kiracılar](docs/guides/tenancy-and-ownership.md)) | Kimlik doğrulama ve uçların yetkilendirmesi (`RequireAuthorization`) |
| İsteklerin doğrulanması; istemcinin Haldus'un alanlarını yazamaması | Hangi kullanıcının hangi işlemi yapabileceği (roller, politikalar) |
| Sırların yalnızca özetinin saklanması ([sırlar](docs/guides/secrets.md)) | Data Protection anahtarlarının kalıcılığı ve korunması ([üretime hazırlık](docs/guides/production.md)) |
| Hassas alanların yanıtlara girmemesi ([hassas alanlar](docs/guides/sensitive-data.md)) | Kendi kodunun bu alanları bir yanıta koymaması |
| İndirilen dosyaların ek olarak ve `nosniff` ile gönderilmesi ([dosyalar](docs/guides/files.md)) | Yüklenen içeriğin taranması |
| Webhook isteklerinin imzalanması ([olaylar](docs/guides/events.md)) | Kullanıcıların tanımladığı adreslere istek gönderiliyorsa SSRF koruması |
