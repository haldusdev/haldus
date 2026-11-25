# Hassas ve şifreli alanlar

Bazı alanlar kaydedilmeli ama bir daha gösterilmemelidir: kimlik numarası, banka hesabı, bir entegrasyonun parolası.
`Sensitive(...)` böyle alanları **yalnızca yazılan** alanlar yapar: istekte yer alırlar, hiçbir yanıtta görünmezler.
`Encrypted(...)` buna bir adım ekler: alan veritabanında da şifreli saklanır. İkisi de kaynağın zincirinde ve gömülü
satırların (`MapDetail`) zincirinde bildirilir.

Sunucunun ürettiği sırlar (API anahtarı, webhook sırrı) için bu sayfa değil [sırlar](secrets.md) sayfası geçerlidir:
onları istemci yazmaz, Haldus üretir ve bir kez gösterir.

Örneklerde web SDK'nın örtük `using`'leri, `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. Üst düzey komutlar `Program.cs` içindedir; `builder` ve
`app` değişkenleri [kurulum](../getting-started/installation.md) sayfasındaki gibi tanımlıdır.

## İçindekiler

- [Yalnızca yazılan alanlar](#yalnızca-yazılan-alanlar)
- [Şifreli alanlar](#şifreli-alanlar)
- [Gömülü satırlarda](#gömülü-satırlarda)
- [Alanın girmediği yerler](#alanın-girmediği-yerler)
- [Data Protection anahtarları](#data-protection-anahtarları)
- [Kendi koruyucunuz](#kendi-koruyucunuz)
- [Birlikte kullanım](#birlikte-kullanım)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Yalnızca yazılan alanlar

`Sensitive(alanlar...)` sayılan alanları yanıt DTO'sundan çıkarır; istek DTO'larında kalırlar. Değer veritabanında
düz saklanır ve sunucunun kendi kodu onu her alan gibi okur.

<!-- example: sensitive -->
```csharp
app.MapCrud<Contact>()
    .Sensitive(contact => contact.NationalId);

public partial class Contact
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(20)]
    public string? NationalId { get; set; }
}
```

Uçlar değişmez; değişen yalnızca DTO'lardır:

| DTO | `nationalId` |
|---|---|
| `CreateContactRequest`, `UpdateContactRequest`, `PatchContactRequest` | var |
| `ContactResponse` | yok |

```http
POST /contacts
Content-Type: application/json

{ "name": "Ada", "nationalId": "12345678901" }
```

```json
{ "id": "0193a5ce-9000-7c30-8d30-940516270001", "name": "Ada" }
```

### Yazma kuralları

- Alanın doğrulama kuralları (`MaxLength`, `Range`, `Required`...) her alan gibi uygulanır; geçersiz değer `400` alır.
- `PATCH` göndermediği alana dokunmaz; gönderirse yazar, `null` gönderirse boşaltır.
- `PUT` tam değiştirmedir: alanı göndermeyen bir `PUT` onu boşaltır (alan zorunluysa `400` alır). İstemci değeri
  okuyamadığı için yeniden de gönderemez; kaydın öteki alanlarını değiştirmek için `PATCH` kullanın.
- Alan yazılabilir bir skaler özellik olmalıdır (herkese açık setter'ı olan, gömülü değer nesnesi olmayan). Değilse
  `HALDUS010` olur.

## Şifreli alanlar

`Encrypted(alanlar...)` sayılan metin alanlarını veritabanında şifreli saklar ve, `Sensitive` alanlar gibi, yalnızca
yazılan alan yapar. Şifreleme ASP.NET Core Data Protection ile yapılır; yeni bir paket gerekmez.

<!-- example: encrypted -->
```csharp
app.MapCrud<Contact>()
    .Sensitive(contact => contact.NationalId)
    .Encrypted(contact => contact.Iban);

public partial class Contact
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(20)]
    public string? NationalId { get; set; }

    [MaxLength(34)]
    public string? Iban { get; set; }
}
```

- Şifreleme bir EF Core değer dönüştürücüsüdür: veritabanına şifreli metin yazılır, entity'de açık metin durur. Sunucunun
  kendi kodu alanı açık haliyle okur ve yazar; sorgu izdüşümlerinde de dönüştürücü çalışır.
- Şifreli metin açık metinden uzundur. Bu yüzden alanın uzunluk sınırı kolona uygulanmaz; istekte, açık metin
  üzerinde doğrulanmaya devam eder (`[MaxLength(34)]` 34 karakterden uzun bir IBAN'ı `400` ile reddeder).
- Her alan kendi amacıyla şifrelenir (`{Namespace}.{Tip}.{Alan}`, ör. `Shop.Contact.Iban`): bir alanın şifreli değeri
  başka bir alana veya kaynağa kopyalanarak açılamaz.
- Alan bir metin (`string`) özelliği olmalıdır; değilse `HALDUS010` olur.

Yanıtta görünmesine bilerek izin verilmedi: yanıtı saklayan her yer (olay kutusu, idempotency kaydı, sürümler) onu
düz metin olarak saklardı. Şifreli alan her zaman yalnızca yazılır. Maskeleyerek gösterme (son dört karakter) yoktur;
gerekiyorsa ayrı, yanıtta görünen bir alanı kendiniz doldurun.

## Gömülü satırlarda

İki bildirim gömülü satırların zincirinde de yazılır. Satırın alanı sahibin isteklerinde yer alır, sahibin yanıtında
yer almaz.

<!-- example: detail -->
```csharp
var employees = app.MapCrud<Employee>()
    .Revisioned();

employees.MapDetail<BankAccount>()
    .Encrypted(account => account.Iban);

employees.MapDetail<EmergencyContact>()
    .Sensitive(contact => contact.Phone);

public partial class Employee
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class BankAccount
{
    [MaxLength(100)]
    public string BankName { get; set; } = "";

    [MaxLength(34)]
    public string? Iban { get; set; }
}

public partial class EmergencyContact
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(30)]
    public string? Phone { get; set; }
}
```

Satırın hassas alanı sahibin anlık görüntüsüne (`Revisioned`) girmez ve geri yükleme ona dokunmaz.

## Alanın girmediği yerler

Haldus'un yanıt ürettiği bütün yollar tek bir izdüşümden geçer; hassas ve şifreli alanlar o izdüşümde yoktur. Bu
yüzden alan şunların hiçbirinde görünmez:

| Yer | Neden |
|---|---|
| Bütün yanıtlar: oluşturma, okuma, liste, `PUT`, `PATCH`, toplu uçlar, arama, geçişler, aksiyonlar, önizleme, kopya, yayındaki hal | Yanıt DTO'sunda alan yoktur. |
| Olaylar (`PublishesEvents`) | Olayın verisi kaydın yanıt biçimidir. |
| Değişiklik akışı (`ChangeFeed`) | Akış kayıtları yanıt biçiminde döndürür. |
| Dışa aktarma (`Exports<T>()`) | Dışa aktarılan satırlar yanıt biçimindedir. |
| Idempotency kaydı (`Idempotent`) | Saklanan, yanıtın kendisidir. |
| Saklanan sürümler (`Revisioned`) | Alan anlık görüntüye hiç yazılmaz; geri yükleme alanın şimdiki değerine dokunmaz. |

Alanı sunucu tarafında taşıyan yollar da vardır; bunlar değeri istemciye göstermeden aynen kopyalar:

- Kopya (`Cloneable`): kopyanın alanı kaynaktakiyle aynıdır.
- Yayın (`Publishable`): yayın kopyası alanı taşır.

Ayrıca alan bir arama veya sıralama ölçütü olamaz; değerine göre aramak veya sıralamak onu sızdırırdı. Derleme
zamanında reddedilen bildirimler:

| Bildirim | Tanı |
|---|---|
| `Searchable(contact => contact.NationalId)` | `HALDUS005` |
| `Sortable(contact => contact.NationalId)` | `HALDUS010` |
| `NaturalKey(contact => contact.NationalId)` | `HALDUS010` |
| `Translatable(contact => contact.NationalId)` | `HALDUS010` |
| `ChangeSets(...)` (kaynakta veya gömülü satırlarında hassas alan varken) | `HALDUS007` |

Değişiklik kümesi sahnelenen isteği saklar ve geri gösterir (`GET /{id}/edits/{editId}`); hassas alan orada okunur
olurdu. Bu yüzden hassas veya şifreli alanı olan bir kaynak değişiklik kümesi bildiremez.

<!-- example: sensitive-change-sets, error: HALDUS007 -->
```csharp
// Hatalı: değişiklik kümesi isteği saklayıp geri gösterir.
app.MapCrud<Contact>()
    .Sensitive(contact => contact.NationalId)
    .ChangeSets();

public partial class Contact
{
    [MaxLength(20)]
    public string? NationalId { get; set; }
}
```

Tip–örnek ilişkisinde (`TypedBy<T>()`) hassas alanlar varsayılan olarak devralınmaz, iki yönde de: tipin yalnızca
yazılan bir alanı kaydın okunabilir aynı adlı alanına taşınsaydı, gizli değer kaydın yanıtında görünürdü.

## Data Protection anahtarları

`AddHaldus` Data Protection'ı kaydeder (`AddDataProtection()`), ama anahtarların nerede saklanacağını uygulama
belirler. Varsayılan ayarda anahtarlar çalıştığı makinedeki kullanıcı profilinde, profil yoksa yalnızca bellekte
durur. Üretimde iki şey gerekir:

- **Kalıcılık:** anahtarlar uygulama yeniden başladığında, kapsayıcı yeniden oluşturulduğunda kaybolmamalıdır.
- **Paylaşım:** birden çok sunucu aynı anahtar halkasını ve aynı uygulama adını kullanmalıdır.

Anahtarlar kaybolursa şifreli alanlar ve geri okunabilen sırlar (`SecretOnce(recoverable: true)`) bir daha
açılamaz: şifreli alanı okuyan istek `500` alır. Kurtarmanın yolu yoktur.

<!-- example: data-protection -->
```csharp
using Microsoft.AspNetCore.DataProtection;

builder.Services.AddHaldus<AppDbContext>();

// Anahtarlar kalıcı, sunucular arasında paylaşılan bir klasörde durur; uygulama adı bütün sunucularda aynıdır.
builder.Services.AddDataProtection()
    .SetApplicationName("shop")
    .PersistKeysToFileSystem(new DirectoryInfo("/var/lib/shop/keys"));

var app = builder.Build();

app.MapCrud<Contact>()
    .Encrypted(contact => contact.Iban);

public partial class Contact
{
    [MaxLength(34)]
    public string? Iban { get; set; }
}
```

Klasör yerine veritabanı, Azure Blob Storage veya Redis de kullanılabilir; anahtarların kendisi de bir sertifika veya
anahtar kasasıyla korunabilir (`ProtectKeysWith...`). Bunlar ASP.NET Core'un kendi seçenekleridir; Haldus onlara
karışmaz. Data Protection anahtarları varsayılan olarak 90 günde bir yenilenir; eski anahtarlar halkada kaldığı sürece
eski şifreli değerler okunmaya devam eder. Anahtar halkasını silmeyin ve yedekleyin.

## Kendi koruyucunuz

Şifreleme `IHaldusFieldProtector` servisinden geçer. Varsayılan uygulama Data Protection'dır. Kendi anahtar
yönetiminizi (ayrı bir anahtar halkası, bir anahtar kasası) kullanmak isterseniz servisi değiştirirsiniz:

<!-- fragment -->
```csharp
public interface IHaldusFieldProtector
{
    string Protect(string purpose, string value);

    string Unprotect(string purpose, string value);
}
```

`purpose` alanı ayırır (`{Namespace}.{Tip}.{Alan}`); bir amaçla şifrelenen değer başka bir amaçla açılmamalıdır.

<!-- example: protector -->
```csharp
using Microsoft.AspNetCore.DataProtection;

builder.Services.AddHaldus<AppDbContext>();

// Haldus'unkinin yerine geçer: alanlar uygulamanın genel anahtarlarıyla değil kendi halkalarıyla şifrelenir.
builder.Services.AddSingleton<IHaldusFieldProtector, FieldKeyRingProtector>();

var app = builder.Build();

app.MapCrud<Contact>()
    .Encrypted(contact => contact.Iban);

public partial class Contact
{
    [MaxLength(34)]
    public string? Iban { get; set; }
}

public sealed class FieldKeyRingProtector : IHaldusFieldProtector
{
    private readonly IDataProtectionProvider _provider =
        DataProtectionProvider.Create(new DirectoryInfo("/var/lib/shop/field-keys"));

    public string Protect(string purpose, string value) => _provider.CreateProtector(purpose).Protect(value);

    public string Unprotect(string purpose, string value) => _provider.CreateProtector(purpose).Unprotect(value);
}
```

Koruyucu, EF Core modeli kurulurken bir kez çözülür ve uygulama boyunca kullanılır; tekil (singleton) olmalıdır.
Koruyucu değiştirilirse var olan şifreli değerler eski koruyucuyla yazılmıştır ve yenisiyle açılamaz; değerleri
kendiniz yeniden şifrelemelisiniz.

## Birlikte kullanım

| Bildirim | Nereye | Birleştiği | Birleşmediği |
|---|---|---|---|
| `Sensitive(...)` | zincir, `MapDetail` | Bütün archetype'ların yazılabilir alanları; `Revisioned`, `Cloneable`, `Publishable`, `PublishesEvents`, `Batch`, `Purgeable`, `TokenOnly` | `ChangeSets` (`HALDUS007`); alan `Searchable`, `Sortable`, `NaturalKey`, `Translatable` olamaz |
| `Encrypted(...)` | zincir, `MapDetail` | `Sensitive` ile aynı | `Sensitive` ile aynı; ayrıca yalnızca metin alanları |

`Encrypted` alan zaten yalnızca yazılır; aynı alanı ayrıca `Sensitive` olarak saymanız gerekmez.

## Sınırlar

- **Filtre sınıfları:** kendi yazdığınız filtre (`IFilter<T>`) alanı süzmekte serbesttir; Haldus engellemez. Düz
  saklanan hassas alana göre süzmek, sonuçların varlığı üzerinden değeri ele verebilir. Şifreli alana göre süzen bir
  filtre şifreli metinle karşılaştırır ve eşleşme bulamaz (Data Protection aynı değeri her seferinde farklı şifreler).
- **Uygulamanın kendi kodu:** alanı kendi yazdığınız bir uçta veya bir yanıta koyarsanız Haldus engelleyemez.
- **Var olan veri:** bir alanı sonradan `Encrypted` yapmak var olan düz değerleri şifrelemez; o satırlar okunamaz
  (`500`). Alanı şifreli yapmadan önce var olan değerleri bir migration veya betikle şifreleyin ya da boşaltın.
- **Anahtar kaybı:** anahtarlar kaybolursa şifreli değerler açılamaz ve okuma `500` verir.
- **Maskeleme:** şifreli alanın son dört karakteri gibi kısmi bir gösterimi yoktur.
- **`PUT`:** hassas alan her alan gibi `PUT` gövdesinde beklenir; göndermeyen `PUT` onu boşaltır.
- **Veritabanında:** `Sensitive` alan veritabanında düz durur. Veritabanına erişen herkes görür; bunu istemiyorsanız
  `Encrypted` kullanın.

## Ayrıca bakın

- [Bildirim dizini](../reference/declarations.md)
- [Sırlar ve erişim token'ları](secrets.md)
- [Sürümler, yayın ve okundu bilgisi](history-and-publishing.md)
- [Kayıt işlemleri](record-operations.md): `ChangeSets`, `Cloneable`
- [Olay yayını](events.md)
- [Arka plan işleri](jobs.md): dışa ve içe aktarma
- [Alanlar](fields.md)
- [Üretime hazırlık](production.md)
- [Veritabanı ve migration'lar](database.md)
