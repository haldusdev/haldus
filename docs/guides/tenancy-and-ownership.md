# Kiracılar, kullanıcılar ve yetkilendirme

Haldus bir kaydın kime ait olduğunu bilir ve kayıtları ona göre gizler: bir kiracının kayıtları öteki kiracılara,
bir kullanıcının kayıtları öteki kullanıcılara görünmez. Bunun için iki bildirim vardır: `MultiTenant()` ve
`OwnedByCurrentUser()`. Kimliği (kim olduğunuzu) Haldus doğrulamaz; kimlik doğrulamanın ürettiği claim'leri okur. Bu
sayfa kimliğin nereden okunduğunu, iki bildirimi, uçlara yetki kuralı eklemeyi ve bir isteğin dışında çalışan kodu
anlatır.

Örneklerde web SDK'nın örtük `using`'leri ile `Haldus`, `Microsoft.EntityFrameworkCore` ve
`System.ComponentModel.DataAnnotations` yazılmış sayılır. `var builder = WebApplication.CreateBuilder(args);` ve
`var app = builder.Build();` satırları yoksa yazılmış sayılır.

## İçindekiler

- [Kimlik nereden okunur](#kimlik-nereden-okunur)
- [Çok kiracılı kaynaklar](#çok-kiracılı-kaynaklar)
- [Kullanıcıya ait kaynaklar](#kullanıcıya-ait-kaynaklar)
- [Yetkilendirme](#yetkilendirme)
- [Kapsam metadatası ile kendi kuralınızı yazmak](#kapsam-metadatası-ile-kendi-kuralınızı-yazmak)
- [Bir isteğin dışında çalışan kod](#bir-isteğin-dışında-çalışan-kod)
- [Kimlik doğrulama](#kimlik-doğrulama)
- [Sınırlar](#sınırlar)
- [Ayrıca bakın](#ayrıca-bakın)

## Kimlik nereden okunur

Haldus geçerli isteğin kiracısını ve kullanıcısını üç servisten sorar. `AddHaldus` varsayılanlarını kaydeder; üçü de
`HttpContext.User` içindeki claim'lere bakar:

| Servis | Ne verir | Varsayılan |
|---|---|---|
| `ITenantProvider` | `Guid? TenantId` | `HaldusOptions.TenantClaimType` claim'i (varsayılan `tenant_id`). Değer bir `Guid` olmalıdır; değilse kiracı yok sayılır. |
| `ICurrentUser` | `string? UserId` | `HaldusOptions.UserClaimType` claim'i (varsayılan `ClaimTypes.NameIdentifier`). |
| `ICurrentUserRoles` | `bool IsInRole(string role)` | `HttpContext.User.IsInRole(role)`, yani rol claim'leri. Yalnızca onay zincirinde (`ApprovalRoles`) sorulur. |

Claim adları sizin kimlik sağlayıcınızda farklıysa ayarları değiştirin:

<!-- example: claim-types -->
```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddHaldus<AppDbContext>(options =>
{
    options.TenantClaimType = "org_id";
    options.UserClaimType = "user_id";
});

var app = builder.Build();

app.UseHaldus();
app.MapCrud<Project>().MultiTenant();

app.Run();

public partial class Project
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

> [!NOTE]
> ASP.NET Core'un JWT şeması gelen claim adlarını varsayılan olarak eşler (ör. `sub` → `ClaimTypes.NameIdentifier`).
> Bu yüzden `UserClaimType` değerini, token'daki adı değil, `HttpContext.User` içinde gördüğünüz adı verin.

Kiracı veya kullanıcı claim'den okunamıyorsa servisi kendiniz yazarsınız. Haldus varsayılanlarını `TryAdd` ile
kaydeder; kendi uygulamanızı `AddHaldus`'tan önce de sonra da kaydedebilirsiniz. Varsayılanlar singleton'dır ve isteğe
`IHttpContextAccessor` üzerinden ulaşır; kendi uygulamanızı da öyle yazın:

<!-- example: tenant-provider -->
```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ITenantProvider, OrganizationTenantProvider>();
builder.Services.AddHaldus<AppDbContext>();

var app = builder.Build();

app.UseHaldus();
app.MapCrud<Project>().MultiTenant();

app.Run();

// Kimlik sağlayıcı kuruluşun kimliğini "org" claim'inde, tiresiz yazıyor: "3f2c9a…".
public sealed class OrganizationTenantProvider(IHttpContextAccessor accessor) : ITenantProvider
{
    public Guid? TenantId =>
        Guid.TryParseExact(accessor.HttpContext?.User.FindFirst("org")?.Value, "N", out var tenantId)
            ? tenantId
            : null;
}

public partial class Project
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

> [!WARNING]
> Kiracıyı ve kullanıcıyı yalnızca doğrulanmış bir kimlikten okuyun. İsteği yapanın kendisinin yazdığı bir yerden
> (bir başlık, adres, sorgu parametresi) okunan kiracı, herkese her kiracının kayıtlarını açar. Haldus'un örnek mağazası
> testler için kimliği başlıklardan okuyan bir uygulama taşır (`DemoIdentity`); o kip yalnızca geliştirme ortamında
> açılabilir.

## Çok kiracılı kaynaklar

`MultiTenant()` kaynağa bir `TenantId` alanı ekler ve kayıtları kiracıya göre ayırır.

<!-- example: multi-tenant -->
```csharp
app.MapCrud<Customer>().MultiTenant();

public partial class Customer
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}
```

Üretilen entity `IMultiTenant` arayüzünü uygular. `TenantId` istek DTO'larında yer almaz; yanıtta da yoktur.

| Ne olur | Nasıl |
|---|---|
| Oluşturma | `TenantId` isteği yapanın kiracısıyla doldurulur. Gövdede gönderilen bir değer yoktur; istek tipinde böyle bir alan bulunmaz. |
| Okuma ve listeleme | Model bir sorgu filtresi taşır (`HaldusFilters.Tenant`): `TenantId`, geçerli kiracıya eşit olmayan kayıt hiçbir sorguda görünmez. |
| Güncelleme | `TenantId` değiştirilemez; kayıt başka bir kiracıya taşınamaz. |
| Başka kiracının kaydı | Okuma, güncelleme ve silme `404` döner; liste boş gelir. Kaydın var olduğu belli olmaz. |
| Kiracısı çözülemeyen istek | `403` döner. Uç grubunun başındaki `HaldusTenantRequiredFilter` isteği kayda ulaşmadan durdurur. |

Kiracısı çözülemeyen isteğin yanıtı (`Accept-Language: tr` ise metinler Türkçedir):

```http
GET /customers
```

```json
{
  "title": "Tenant required",
  "status": 403,
  "detail": "This resource belongs to a tenant, but no tenant could be determined for the request."
}
```

Filtre veritabanı sorgusunun parçasıdır; uçlarda kiracıyla ilgili ayrı bir kontrol yoktur. Bu yüzden kaynağa dokunan her
sorgu (sizin yazdığınız sorgular da) kiracıya göre süzülür. Bir sorguda filtreyi kaldırmanız gerekirse yalnızca onu
adıyla kaldırın:

<!-- fragment -->
```csharp
var all = await db.Customers.IgnoreQueryFilters([HaldusFilters.Tenant]).ToListAsync();
```

Parametresiz `IgnoreQueryFilters()` kiracı filtresiyle birlikte sahiplik, ebeveyn ve yumuşak silme filtrelerini de
kaldırır.

### Görünürlük ilişkilerle yayılır

Bir kaynağın kendisi `MultiTenant` olmasa da, kiracıya ait bir kayda bağlıysa onun görünürlüğünü devralır:

<!-- example: tenant-relations -->
```csharp
app.MapCrud<Customer>().MultiTenant();
app.MapCrud<Address>().Under<Customer>();
app.MapCrud<Tag>();
app.MapAssociation<Customer, Tag>();

public partial class Customer
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class Address
{
    [MaxLength(200)]
    public string Line { get; set; } = "";
}

public partial class Tag
{
    [MaxLength(50)]
    public string Name { get; set; } = "";
}
```

| İlişki | Başka bir kiracı ne görür |
|---|---|
| `.Under<Customer>()` | Ebeveyni göremediği adresleri de göremez (`HaldusFilters.Parent`). `/customers/{customerId}/addresses` listesi boş gelir; tekil okuma, oluşturma, güncelleme ve silme `404` döner. |
| `.References<Customer>()` | Referans her yazmada sorgu filtrelerinden geçerek doğrulanır. Başka kiracının kaydı bulunamamış sayılır ve alan düzeyinde `400` döner. |
| `MapAssociation<Customer, Tag>()` | Bağın iki ucundan biri görünmüyorsa bağ da görünmez (`HaldusFilters.Association`). Bağı okumak, kurmak ve kaldırmak `404` döner; iki yöndeki liste de boştur. |

Kendisi `MultiTenant` olmayan bir kaynakta (yukarıdaki `Address`) kiracısı çözülemeyen istek `403` almaz: ebeveyni
görünmediği için liste boş gelir, tekil istekler `404` alır.

Zincirleme silme kiracı sınırını aşmaz. Kiracıya ait kayıtlar herkesin gördüğü bir kaydın altındaysa veya ona bağlıysa,
genel kaydın silinmesi kiracıların kayıtlarını kendiliğinden silmez: varsayılan silme davranışı `Cascade` yerine
`Restrict` olur. Ayrıntısı [ilişkiler](relationships.md) sayfasındadır.

## Kullanıcıya ait kaynaklar

`OwnedByCurrentUser()` kaydı onu oluşturan kullanıcıya bağlar. Kaynak varsayılan olarak `/me` altında sunulur.

<!-- example: owned -->
```csharp
app.MapCrud<ShoppingList>().OwnedByCurrentUser();          // /me/shopping-lists
app.MapCrud<ShoppingListItem>().Under<ShoppingList>();     // /me/shopping-lists/{shoppingListId}/shopping-list-items
app.MapCrud<Product>();
app.MapCrud<ProductNote>()
    .Under<Product>()
    .OwnedByCurrentUser(underMe: false);                   // /products/{productId}/product-notes

public partial class ShoppingList
{
    [MaxLength(100)]
    public string Name { get; set; } = "";
}

public partial class ShoppingListItem
{
    [MaxLength(200)]
    public string Text { get; set; } = "";
}

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class ProductNote
{
    [MaxLength(2000)]
    public string Text { get; set; } = "";
}
```

Üretilen entity `IOwned` arayüzünü uygular ve bir `OwnerId` (`string`) alanı taşır.

| Uç | Ne yapar |
|---|---|
| `GET /me/shopping-lists` | İsteği yapanın listelerini döndürür. |
| `GET /me/shopping-lists/{id}` | İsteği yapanın kaydını döndürür; başkasınınki `404`. |
| `POST /me/shopping-lists` | Kaydı oluşturur; sahibi isteği yapandır. |
| `PUT`, `PATCH`, `DELETE /me/shopping-lists/{id}` | Yalnızca sahibi değiştirir ve siler; başkası `404` alır. |

Kurallar:

- Sahip oluştururken atanır. `OwnerId` istekte verilemez (gövdede gönderilse de yok sayılır), yanıtta yer almaz ve
  sonradan değişmez.
- Başkasının kaydı bulunamamış sayılır: okuma, güncelleme ve silme `404` döner, liste yalnızca isteği yapanın kayıtlarını
  içerir.
- Kullanıcısı çözülemeyen istek `401` alır (`HaldusUserRequiredFilter`). `/me` ile başlayan her adreste de aynı kural
  geçerlidir.
- Görünürlük sorgu filtresiyle sağlanır (`HaldusFilters.Owner`). Sahipli bir kaydın altına yerleşen kaynak
  (`ShoppingListItem`) onun adresini ve görünürlüğünü izler.
- `underMe: false` adresi değiştirmez ama aynı korumayı verir. Yukarıdaki notlar ürünün adresindedir; ürünü herkes görür,
  notları ise yalnızca yazanı.

```http
POST /me/shopping-lists
Content-Type: application/json

{ "name": "Weekend" }
```

```http
HTTP/1.1 201 Created
Location: /me/shopping-lists/0193c2a4-…
```

Kullanıcısı çözülemeyen istek:

```json
{
  "title": "Sign-in required",
  "status": 401,
  "detail": "This resource belongs to a user, but no user could be determined for the request."
}
```

`OwnedByCurrentUser` yalnızca `MapCrud`, `MapReadOnly`, `MapAppendOnly` ve `MapJob` ile birleşir. `.Under<T>()` ile
birlikte `underMe: false` yazılmalıdır; yazılmazsa derleme `HALDUS007` ile durur:

<!-- example: owned-under-me, error: HALDUS007 -->
```csharp
app.MapCrud<Product>();
app.MapCrud<ProductNote>()
    .Under<Product>()
    .OwnedByCurrentUser();

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class ProductNote
{
    [MaxLength(2000)]
    public string Text { get; set; } = "";
}
```

Bir kullanıcıyı bir kayda bağlayan işaretler (favoriler gibi) için ayrı bir bildirim vardır:
`app.MapAssociation<Favorite, Product>().ForCurrentUser()`. Uçları `/me/favorites` altındadır; ayrıntısı
[ilişkiler](relationships.md) sayfasındaki "Kullanıcıya bağlı bağ" bölümündedir.

> [!IMPORTANT]
> Sahiplik bir görünürlük kuralıdır, silme davranışını değiştirmez. Herkesin gördüğü bir ebeveyn (yukarıda `Product`)
> silindiğinde, altındaki ve başka kullanıcılara ait kayıtlar (`ProductNote`) da silinir.

## Yetkilendirme

`MultiTenant` ve `OwnedByCurrentUser` kimin hangi kaydı gördüğünü belirler; kimin hangi uca ulaşabildiğini belirlemez.
Bunun için ASP.NET Core'un yetkilendirmesini kullanırsınız.

Her `Map*` çağrısının döndürdüğü builder bir `IEndpointConventionBuilder`'dır. ASP.NET Core'un bütün uç
uzantıları onda çalışır ve kaynağın bütün uçlarına (standart uçlar, geçişler, aksiyonlar, trait'lerin eklediği uçlar)
uygulanır:

<!-- example: authorization -->
```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization(options =>
    options.AddPolicy("Editors", policy => policy.RequireRole("editor")));

var app = builder.Build();

app.UseHaldus();
app.UseAuthentication();
app.UseAuthorization();

app.MapCrud<Article>()
    .MultiTenant()
    .RequireAuthorization("Editors");

app.MapReadOnly<Announcement>()
    .AllowAnonymous();

app.Run();

public partial class Article
{
    [MaxLength(200)]
    public string Title { get; set; } = "";
}

public partial class Announcement
{
    [MaxLength(200)]
    public string Text { get; set; } = "";
}
```

Builder'ı bir değişkende tutup kuralı sonra da ekleyebilirsiniz (`guests.AllowAnonymous();`).
`RequireAuthorization`, `AllowAnonymous`, `RequireRateLimiting` gibi çağrılar Haldus'un derleme zamanında okuduğu
bildirimler değil, çalışma zamanında işleyen ASP.NET Core çağrılarıdır; bu yüzden zincirin statik olma kuralına
bağlı değildirler.

Bilinmesi gerekenler:

- `MapAssociation` de bir `IEndpointConventionBuilder` döndürür; kural bağın uçlarına uygulanır.
- `Taggable<TTag>()` bağın uçlarını kaynağın grubunun dışında eşler, ama kaynağa eklenen kurallar onlara da uygulanır.
- `Attachable` / `Commentable` için böyle değildir: takılı kaynağın uçlarını kendi `Map` çağrısı eşler
  (`app.MapFile<Attachment>()`); kuralı o çağrıya ekleyin. Hedefin builder'ına eklenen kural takılı kaynağa ulaşmaz.
- Token ile erişilen uçlar (`TokenAccess`, `SignedLinks`) kendileri `AllowAnonymous` ile işaretlidir.

Uygulamanızın tamamı giriş gerektiriyorsa ASP.NET Core'un varsayılan kuralını (`FallbackPolicy`) kullanın; açık kalması
gereken uçlar `AllowAnonymous` ile işaretlenir. Bu bir ASP.NET Core özelliğidir, Haldus'a özgü değildir.

## Kapsam metadatası ile kendi kuralınızı yazmak

Üretilen her uç grubu, kaynağın kayıtlarının kime ait olduğunu metadata olarak taşır: `HaldusResourceScope`.

| Özellik | Doğru olduğu durum |
|---|---|
| `TenantScoped` | Kaynak `MultiTenant`'tır veya öyle bir kaynağın altındadır (`Under`, kısa adresli ebeveyn, `AttachedTo`). |
| `OwnerScoped` | Kaynak `OwnedByCurrentUser`'dır veya öyle bir kaynağın altındadır. |
| `IsShared` | İkisi de yanlış: kayıtlar ne bir kiracıya ne bir kullanıcıya ait; onları gören herkes için ortaktır. |

Gövde taşıdığı için `POST` olan ama hiçbir şey yazmayan uçlar ayrıca `HaldusReadOnly` ile işaretlidir: arama
(`/search`), toplu okuma (`/batch-get`) ve önizleme (`/preview`, `/{id}/preview`). Okumayı adresin sonundan tanımaya
çalışmayın; bir aksiyonun veya istemcinin verdiği bir anahtarın adı da `search` olabilir.

Bu iki bilgiyle kaynakları tek tek saymadan bir yetki kuralı yazabilirsiniz; sonradan eklenen bir kaynak kendiliğinden
doğru sınıfa düşer. Örnek mağazanın kuralı şudur: kimseye ait olmayan, ortak kaynakları (ürünler, kategoriler) herkes
okur ama yalnızca personel yazar.

<!-- example: shared-writes -->
```csharp
using Microsoft.AspNetCore.Authorization;

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new SharedWrites())
        .Build());
builder.Services.AddSingleton<IAuthorizationHandler, SharedWritesHandler>();

var app = builder.Build();

app.UseHaldus();
app.UseAuthentication();
app.UseAuthorization();

app.MapCrud<Product>();                         // ortak: yazmak "staff" rolü ister
app.MapCrud<Cart>().OwnedByCurrentUser();       // kullanıcıya ait: kural uygulanmaz

app.Run();

public sealed class SharedWrites : IAuthorizationRequirement;

public sealed class SharedWritesHandler : AuthorizationHandler<SharedWrites>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SharedWrites requirement)
    {
        if (context.Resource is HttpContext http && (IsRead(http) || IsScoped(http) || context.User.IsInRole("staff")))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static bool IsRead(HttpContext http) =>
        HttpMethods.IsGet(http.Request.Method)
        || HttpMethods.IsHead(http.Request.Method)
        || HttpMethods.IsOptions(http.Request.Method)
        || http.GetEndpoint()?.Metadata.GetMetadata<HaldusReadOnly>() is not null;

    // Kural kapalı başlar: kapsamını bildirmeyen uç (entity'ye bağlı olmayan MapAction, elle yazılmış uç) ortak sayılır.
    private static bool IsScoped(HttpContext http) =>
        http.GetEndpoint()?.Metadata.GetMetadata<HaldusResourceScope>() is { IsShared: false };
}

public partial class Product
{
    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public partial class Cart
{
    [MaxLength(100)]
    public string Name { get; set; } = "";
}
```

`FallbackPolicy` yalnızca kendi yetki kuralını bildirmeyen uçlara uygulanır. Bir uç bu kuralın dışında kalacaksa kendi
kuralını bildirir (`.RequireAuthorization()`) veya açık kalır (`.AllowAnonymous()`). Çalışan hali örnek mağazadadır:
[SharedWrites.cs](../../samples/Haldus.Sample.Shop/Features/Auth/SharedWrites.cs).

Metadatanın sınırları ve istisnaları:

- Entity'ye bağlı olmayan `MapAction` ve `MapQuery` uçları `HaldusResourceScope` taşımaz. Kuralınız onları en kısıtlı
  sınıfta saymalıdır (yukarıdaki gibi "kapsamı bilinmeyen uç ortaktır").
- Bağın kapsamı uçlarından birininkidir; `ForCurrentUser()` ile kurulan bağ her zaman kullanıcıya aittir.
- Okundu işaretini yazan uçlar (`/read`, `/unread`, `/read-all`) kayıt ortak olsa da kullanıcıya aittir: işaret,
  işaretleyenindir.
- Yalnızca token ile ulaşılan (`TokenOnly`) bir kaynak "ortak" görünür; uçları zaten oturumsuzdur.

## Bir isteğin dışında çalışan kod

Sorgu filtreleri ve `TenantId` / `OwnerId` atamaları kimliği geçerli istekten okur. Bir isteğin dışında çalışan kodda
(arka plan servisi, yönetim komutu) istek yoktur; bir isteğin içinde başka bir kullanıcı adına kayıt oluşturmak
istediğinizde ise istekteki kimlik yanlıştır. İkisinde de kimliği `HaldusAmbient.Use(tenantId, userId)` ile verirsiniz:

<!-- example: ambient -->
```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();
builder.Services.AddScoped<WelcomeNotifier>();

var app = builder.Build();

app.UseHaldus();
app.MapCrud<Notification>().OwnedByCurrentUser();

app.Run();

public sealed class WelcomeNotifier(AppDbContext db)
{
    public async Task NotifyAsync(string userId, CancellationToken cancellationToken)
    {
        // Dönen nesne atılana kadar sorgu filtreleri ve sahip ataması bu kimliği kullanır.
        using var identity = HaldusAmbient.Use(tenantId: null, userId);

        db.Add(new Notification { Title = "Welcome" });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public partial class Notification
{
    [MaxLength(200)]
    public string Title { get; set; } = "";
}
```

Kimlik, `using` bloğu bitene kadar o asenkron akışta geçerlidir; sonra önceki kimliğe dönülür. İsteğin içinde
`OwnerId` veya `TenantId` alanını elle doldurmak işe yaramaz: geçerli bir kimlik varsa Haldus onu yazar ve elle verilen
değeri ezer.

Haldus'un kendisi de aynı yolu kullanır: `MapJob` işleri onları oluşturan kiracı ve kullanıcı adına çalışır, bakım
servisi zamanlanmış yayınları ve bakiye tutmalarını kaydın kiracısı adına işler.

## Kimlik doğrulama

Haldus kimlik doğrulama yapmaz: giriş, token, oturum ve parola onun işi değildir. ASP.NET Core'un herhangi bir şemasını
(JWT, çerez, OpenID Connect) kullanabilirsiniz; Haldus yalnızca sonucu, yani `HttpContext.User` içindeki claim'leri
okur. Claim'i hangi şemanın ürettiğine bakmaz.

Örnek mağaza ([samples/Haldus.Sample.Shop](../../samples/Haldus.Sample.Shop), `Features/Auth`) çalışan bir kimlik
doğrulama içerir. Kısaca:

- E-posta ve parola ile giriş, ASP.NET Core'un yerleşik taşıyıcı token şeması (`AddBearerToken`). Her hesap kendi
  kiracısıyla doğar; token `ClaimTypes.NameIdentifier` (hesabın kimliği) ve `tenant_id` claim'lerini taşır. Haldus'ta
  hiçbir ayar değiştirilmedi.
- İsteğe bağlı bir kimlik sağlayıcı (OpenID Connect), iki ayrı ve birbirinden bağımsız yoldan:
  - `Shop:Auth:Jwt`: API, sağlayıcının JWT erişim token'ını kabul eder (`AddJwtBearer`). Panel ve mobil uygulama
    sağlayıcıyla girişi kendileri yapar ve token'ı sunar.
  - `Shop:Auth:Oidc`: backend tarayıcıyı sağlayıcıya yönlendirir ve çerezle oturum açar (`AddOpenIdConnect`).

  Sağlayıcının tanıttığı kişi ilk gelişinde yerel bir hesaba ve kendi kiracısına çevrilir; çıkan claim'ler parola
  girişindekilerle aynıdır.
- Giriş yapılmadan hiçbir uca ulaşılmaz (`FallbackPolicy`); ortak kaynakları yalnızca personel yazar (yukarıdaki kural).

Ayrıntılar (oturumlar, yenileme, sağlayıcı ayarları, güvenlik kararları) örneğin kodundadır. Bunlar örneğin
seçimleridir; Haldus'un sizden beklediği tek şey, kiracıyı ve kullanıcıyı claim'lerde (veya kendi `ITenantProvider` /
`ICurrentUser` uygulamanızda) bulabilmesidir.

## Sınırlar

- Kiracı kimliği bir `Guid`'dir; kullanıcı kimliği bir metindir. Başka bir tip desteklenmez.
- Bir kaydın sahibi tek bir kullanıcıdır. Kaydı başka bir kullanıcıya devretmek, birden çok sahip veya paylaşım
  (bir kaydı belirli kullanıcılara açmak) yoktur.
- `/me` altında kullanıcı başına tek kayıt (`MapSingleton().OwnedByCurrentUser()`) yoktur.
- `/me` altındaki bir kaynak `MapAssociation`'ın ucu olamaz; kullanıcıyı bir kayda bağlamak için `ForCurrentUser()`
  kullanılır.
- Rol kontrolü yalnızca onay zincirinde (`ApprovalRoles`) Haldus'un içindedir. Başka her yetki kuralı uygulamanındır.
- Kayıt başına yetki (bir kullanıcının belirli bir kaydı düzenleyebilmesi) bildirilmez; görünürlük kiracı ve sahip
  düzeyindedir.

## Ayrıca bakın

- [İlişkiler](relationships.md): `Under`, `References`, `MapAssociation` ve silme davranışları.
- [Sırlar ve token ile erişim](secrets.md): oturumu olmayan birinin bir kayda token ile ulaşması.
- [Yapılandırma](../reference/configuration.md): `HaldusOptions`.
- [Uygulamanızı test etmek](testing.md): testte kimliği vermek.
- [Üretime hazırlık](production.md)
