using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Haldus.Sample.Shop.Features.Auth;

/// <summary>
/// Bir kimlik sağlayıcının verdiği JWT erişim token'larının kabul edilmesi (<c>Shop:Auth:Jwt</c>). Verilmezse kapalıdır.
/// Panel ve mobil uygulama sağlayıcıyla girişi kendileri yapar (Authorization Code + PKCE) ve aldıkları token'ı
/// bu API'ye sunar; istemcilerin kullanacağı ayarlar <c>/auth/config</c> ucundan okunur.
/// </summary>
public sealed class ShopJwtOptions
{
    /// <summary>Sağlayıcının adresi (issuer); keşif belgesi ve imza anahtarları buradan okunur.</summary>
    public string? Authority { get; set; }

    /// <summary>Token'ın bu API için verildiğini gösteren değer (<c>aud</c>). Zorunludur: başka bir API için verilmiş token kabul edilmez.</summary>
    public string? Audience { get; set; }

    /// <summary>Panel ve mobil uygulamanın sağlayıcıdaki (gizli anahtarı olmayan, PKCE kullanan) istemcisinin kimliği.</summary>
    public string? ClientId { get; set; }

    /// <summary>İstemcilerin isteyeceği kapsamlar.</summary>
    public string Scopes { get; set; } = "openid profile email";

    /// <summary>Keşif belgesi yalnızca HTTPS'ten okunur; yalnızca geliştirme ortamında kapatılabilir.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <inheritdoc cref="ShopOidcOptions.TrustEmailVerified"/>
    public bool TrustEmailVerified { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority);
}

/// <summary>
/// Backend'in kendisinin tarayıcıyı sağlayıcıya yönlendirip çerezle oturum açması (<c>Shop:Auth:Oidc</c>). Verilmezse
/// kapalıdır ve <see cref="ShopJwtOptions"/> ile ilgisi yoktur: biri ötekini gerektirmez.
/// </summary>
public sealed class ShopOidcOptions
{
    public string? Authority { get; set; }

    /// <summary>Backend'in sağlayıcıdaki (gizli anahtarı olan) istemcisi.</summary>
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string Scopes { get; set; } = "openid profile email";

    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Sağlayıcının "bu adres doğrulandı" beyanına (<c>email_verified</c>) güvenilir: adres hesaba doğrulanmış olarak
    /// yazılır ve personel listesindeyse rol getirir. Varsayılan olarak kapalıdır: herkesin hesap açabildiği veya adresi
    /// doğrulamadan beyan eden bir sağlayıcıda bu, personel adresini yazan herkese rol verirdi. Yalnızca kuruluşun
    /// kendi sağlayıcısı gibi, adresleri gerçekten doğrulayan bir sağlayıcı için açılmalıdır. Kapalıyken dış kimliğin
    /// hesabı adressiz (yer tutucu bir adresle) ve doğrulanmamış olarak açılır.
    /// </summary>
    public bool TrustEmailVerified { get; set; }

    /// <summary>
    /// Çerezle açılan oturumun ömrü (dakika). Sağlayıcıdaki oturum kapandığında backend'e haber gelmez; çerez en geç bu
    /// süre sonunda biter. Kısa tutmak, sağlayıcıda kapatılan bir hesabın burada açık kaldığı süreyi kısaltır.
    /// </summary>
    public int SessionMinutes { get; set; } = 480;

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority);
}

/// <summary>Bir kimlik sağlayıcıdan gelen kimliğin örnek mağazanın hesabına bağlanması ve şemaların kurulması.</summary>
public static partial class ExternalSignIn
{
    /// <summary>Gelen isteğe bakıp doğru şemayı seçen şema; yalnızca dış kimlik açıkken kurulur.</summary>
    public const string SelectorScheme = "Shop";

    public const string JwtScheme = "ShopJwt";
    public const string CookieScheme = "ShopCookie";
    public const string OidcScheme = "ShopOidc";
    public const string CookieName = "shop.session";

    /// <summary>
    /// Çerezle kimliği doğrulanan ve yazan (güvenli olmayan) her istek bu başlığı taşımalıdır. Başka bir siteden
    /// gönderilen bir form onu ekleyemez; çerezin kendiliğinden gönderilmesine dayanan istek sahteciliğini (CSRF) keser.
    /// </summary>
    public const string RequestHeader = "X-Shop-Request";

    /// <summary>Sağlayıcının token'ının verildiği an (saniye); "her yerden çık" isteği yapan token'ı buradan tanır.</summary>
    internal const string IssuedAtClaim = "provider_iat";

    // Yönlendirmeli girişte hesabın yazılamadığını (bizim arızamız) sağlayıcının hatasından ayıran işaret.
    private static readonly object AccountFailure = new();

    public static void AddExternalSignIn(this AuthenticationBuilder builder, ShopJwtOptions jwt, ShopOidcOptions oidc, bool development)
    {
        // Hesabın adresi, doğrulanmış olarak ilk geldiği yoldan yazılır; aynı sağlayıcıya iki yoldan farklı
        // güvenilseydi, kullanıcı girişini güvenilen yoldan yaparak ötekinin kuralını aşardı.
        Require(
            !jwt.Enabled || !oidc.Enabled || !SameAuthority(jwt.Authority!, oidc.Authority!) || jwt.TrustEmailVerified == oidc.TrustEmailVerified,
            "Shop:Auth:Jwt and Shop:Auth:Oidc point at the same provider; TrustEmailVerified must be the same in both.");

        if (jwt.Enabled)
        {
            Require(!string.IsNullOrWhiteSpace(jwt.Audience), "Shop:Auth:Jwt:Audience is required when Shop:Auth:Jwt:Authority is set.");

            // Kimlik token'ının (id_token) audience'ı istemcinin kimliğidir. API'nin audience'ı da o olsaydı, erişim
            // token'ı yerine kimlik token'ı da kabul edilirdi.
            // Sondaki eğik çizgi sayılmaz: token doğrulayıcı audience'ı karşılaştırırken onu yok sayar.
            // Öteki bölümün istemcisi de sayılır, adresler farklı yazılmış olsa bile: aynı sağlayıcının iki adı olabilir.
            Require(
                !SameName(jwt.Audience, jwt.ClientId) && !(oidc.Enabled && SameName(jwt.Audience, oidc.ClientId)),
                "Shop:Auth:Jwt:Audience must name the API, not a client: with a client id as the audience, ID tokens would be accepted as access tokens.");
            RequireAuthority("Shop:Auth:Jwt", jwt.Authority!, jwt.RequireHttpsMetadata, development);

            builder.AddJwtBearer(JwtScheme, options =>
            {
                options.Authority = jwt.Authority;
                options.Audience = jwt.Audience;
                options.RequireHttpsMetadata = jwt.RequireHttpsMetadata;

                // Claim adları sağlayıcının yazdığı gibi kalır (sub, email); eski XML adlarına çevrilmez.
                options.MapInboundClaims = false;
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        if (await ResolveAsync(context.HttpContext, context.Principal, JwtScheme, jwt.TrustEmailVerified) is { } principal)
                        {
                            context.Principal = principal;
                        }
                        else
                        {
                            context.Fail("The token does not identify an account.");
                        }
                    },
                };
            });
        }

        if (oidc.Enabled)
        {
            Require(
                !string.IsNullOrWhiteSpace(oidc.ClientId) && !string.IsNullOrWhiteSpace(oidc.ClientSecret),
                "Shop:Auth:Oidc:ClientId and ClientSecret are required when Shop:Auth:Oidc:Authority is set.");
            RequireAuthority("Shop:Auth:Oidc", oidc.Authority!, oidc.RequireHttpsMetadata, development);
            Require(oidc.SessionMinutes is >= 1 and <= 1440, "Shop:Auth:Oidc:SessionMinutes must be between 1 and 1440.");

            builder.AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(oidc.SessionMinutes);
                options.SlidingExpiration = false;

                // Çerez kendi başına geçerli değildir: sunucudaki oturumuna bağlıdır (çıkış ve "her yerden çık" onu
                // hemen geçersiz kılar) ve kimliği her istekte hesaptan yeniden kurulur (rol ve doğrulama bilgisi
                // çerezin ömrü boyunca donmaz).
                options.Events.OnValidatePrincipal = async context =>
                {
                    if (await RestoreAsync(context.HttpContext, context.Principal) is { } principal)
                    {
                        context.ReplacePrincipal(principal);
                    }
                    else
                    {
                        // Oturumu kapanmış çerez tarayıcıda durmaz: yoksa her istekte yeniden gönderilir ve sınanırdı.
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieScheme);
                    }
                };

                // Bu bir API'dir: girişi olmayan istek giriş sayfasına yönlendirilmez, 401 / 403 alır.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

            builder.AddOpenIdConnect(OidcScheme, options =>
            {
                options.Authority = oidc.Authority;
                options.ClientId = oidc.ClientId;
                options.ClientSecret = oidc.ClientSecret;
                options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.SaveTokens = false;
                options.MapInboundClaims = false;
                options.SignInScheme = CookieScheme;
                options.CallbackPath = "/auth/oidc/callback";

                options.Scope.Clear();
                foreach (var scope in oidc.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    options.Scope.Add(scope);
                }

                options.Events = new OpenIdConnectEvents
                {
                    // Sağlayıcının reddi, bozuk veya sahte bir dönüş isteği: işlenmeyen bir hata (500) değil, 400.
                    OnRemoteFailure = context =>
                    {
                        // Bizim arızamız (hesap yazılamadı) sağlayıcının veya isteğin hatası değildir: 400 ile
                        // örtülmez, işlenmeyen hata olarak sürer (500) ve günlüğe ayrıntısıyla düşer.
                        if (context.HttpContext.Items.ContainsKey(AccountFailure))
                        {
                            return Task.CompletedTask;
                        }

                        // Neden istemciye yazılmaz. Günlüğe yalnızca hatanın türü yazılır: bu uca herkes istek
                        // gönderebilir ve hatanın metni (sağlayıcının `error_description` alanı) isteği yapanın
                        // yazdığı bir metin olabilir. Ayrıntıyı ASP.NET Core'un kendi günlüğü taşır.
                        LogRemoteFailure(Logger(context.HttpContext), context.Failure?.GetType().Name ?? "unknown");
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        context.HandleResponse();
                        return Task.CompletedTask;
                    },

                    // Sağlayıcının ön kanal çıkışı (`/signout-oidc`) kullanılmaz: oturum çerezi `SameSite=Lax`
                    // olduğu için sağlayıcının iframe'inden gelen isteğe zaten eklenmez; çerezsiz gelen istek ise
                    // yanıtında çerezi sildirirdi (başka bir site kullanıcıyı oturumdan düşürebilirdi).
                    OnRemoteSignOut = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        context.HandleResponse();
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        ClaimsPrincipal? principal;

                        try
                        {
                            principal = await ResolveAsync(context.HttpContext, context.Principal, CookieScheme, oidc.TrustEmailVerified, openSession: true);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            context.HttpContext.Items[AccountFailure] = true;
                            throw;
                        }

                        if (principal is not null)
                        {
                            context.Principal = principal;
                        }
                        else
                        {
                            context.Fail("The identity token does not identify an account.");
                        }
                    },
                };
            });
        }
    }

    /// <summary>
    /// İsteğin taşıdığına göre şema: nokta ile ayrılmış üç parçalı bir taşıyıcı token bir JWT'dir (sağlayıcının token'ı),
    /// öteki taşıyıcı token'lar örnek mağazanın kendi token'ıdır; başlık yoksa ve oturum çerezi varsa çerez.
    /// </summary>
    public static string Select(HttpContext context, bool jwt, bool cookie)
    {
        var header = context.Request.Headers.Authorization.ToString();

        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return jwt && header.AsSpan(7).Count('.') == 2 ? JwtScheme : BearerTokenDefaults.AuthenticationScheme;
        }

        return cookie && context.Request.Cookies.ContainsKey(CookieName) ? CookieScheme : BearerTokenDefaults.AuthenticationScheme;
    }

    /// <summary>
    /// Sağlayıcının tanıttığı kişiyi (<c>iss</c> + <c>sub</c>) yerel hesaba çevirir; ilk gelişinde hesap ve kiracısı
    /// oluşturulur. Haldus kullanıcıyı ve kiracıyı buradan çıkan claim'lerden okur; parola girişindekilerle aynıdır.
    /// </summary>
    internal static async Task<ClaimsPrincipal?> ResolveAsync(HttpContext http, ClaimsPrincipal? external, string scheme, bool trustEmail, bool openSession = false)
    {
        if (external is null)
        {
            return null;
        }

        var issuer = external.FindFirstValue("iss");
        var subject = external.FindFirstValue("sub");

        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(subject) || issuer.Length > 512 || subject.Length > 256)
        {
            return null;
        }

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var options = http.RequestServices.GetRequiredService<IOptions<ShopAuthOptions>>().Value;
        var cancellationToken = http.RequestAborted;

        long? issuedAt = null;

        if (!openSession)
        {
            // Erişim token'ı verildiği anı söylemelidir (`iat`; RFC 9068 zorunlu tutar): "her yerden çık" buna dayanır.
            // Söylemeyen token ilk istekten reddedilir; yoksa hesap çalışır görünür ve ilk "her yerden çık"tan
            // sonra kalıcı olarak kilitlenirdi. Uyumsuz bir sağlayıcı böylece kurulurken fark edilir.
            // Kabul edilen saat farkından daha ileride verilmiş görünen token da reddedilir: doğrulayıcı `iat`'e
            // bakmaz ve öyle bir token "her yerden çık"ın koyabildiği her sınırın üstünde kalırdı.
            issuedAt = IssuedAt(external);
            var latest = (HaldusTime.UtcNow(http.RequestServices.GetRequiredService<TimeProvider>()) + AuthEndpoints.MaxClockSkew).ToUnixTimeSeconds();

            if (issuedAt is null || issuedAt > latest)
            {
                LogNoIssuedAt(Logger(http));
                return null;
            }
        }

        var account = await FindAsync(db, issuer, subject, cancellationToken);

        // "Her yerden çık"tan önce verilmiş bir token artık kimlik değildir.
        // Çerezle girişte bu sınanmaz: orada her giriş yeni bir oturum açar ve çıkış o oturumları kapatır.
        if (issuedAt is { } issued && account?.SignedOutEverywhereAt is { } signedOut && issued <= signedOut.ToUnixTimeSeconds())
        {
            return null;
        }

        // Adresin doğrulandığını söyleyen sağlayıcıdır; ona ancak açıkça güvenildiyse inanılır (TrustEmailVerified).
        // Adres yalnızca sağlayıcı onu doğruladıysa ve başka bir hesapta değilse alınır. Var olan bir hesaba adres
        // üzerinden bağlanılmaz: aynı adresi taşıdığını söyleyen bir dış kimlik, parolayla açılmış hesabı ele
        // geçirmemelidir. Öyle bir kimlik kendi ayrı hesabını (ve kiracısını) alır.
        var email = account is null || account.EmailVerifiedAt is null ? VerifiedAddress(external, trustEmail) : null;
        var usable = email is not null && !await db.Set<ShopAccount>().AnyAsync(candidate => candidate.Email == email, cancellationToken);

        if (account is not null && usable)
        {
            // Hesap ilk gelişinde adresini alamamış olabilir (erişim token'ı adresi taşımıyordu, sağlayıcı onu henüz
            // doğrulamamıştı ya da adres o sırada başkasındaydı). Doğrulanmış adres sonradan gelirse yazılır; yoksa
            // hesap, adresini bizim kod akışımızla da doğrulayamadığı için sonsuza kadar doğrulanmamış kalırdı.
            db.Attach(account);
            account.Email = email!;
            account.EmailVerifiedAt = HaldusTime.UtcNow(http.RequestServices.GetRequiredService<TimeProvider>());

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                db.Entry(account).State = EntityState.Detached;
            }
            catch (DbUpdateException exception) when (HaldusDb.IsUniqueViolation(exception, AuthModel.EmailIndex))
            {
                // Adresi bu arada başkası aldı: hesap olduğu gibi kalır.
                db.Entry(account).State = EntityState.Detached;
                account = await FindAsync(db, issuer, subject, cancellationToken);

                if (account is null)
                {
                    return null;
                }
            }
        }

        if (account is null)
        {
            var now = HaldusTime.UtcNow(http.RequestServices.GetRequiredService<TimeProvider>());

            account = new ShopAccount
            {
                Id = Guid.CreateVersion7(),
                Email = usable ? email! : Placeholder(),
                TenantId = Guid.CreateVersion7(),
                CreatedAt = now,
                EmailVerifiedAt = usable ? now : null,
                ExternalIssuer = issuer,
                ExternalSubject = subject,
            };

            db.Add(account);

            if (!usable && trustEmail && options.RequireVerifiedEmail)
            {
                // Açılış denetimi bunu göremez: sağlayıcıya güveniliyor, ama token doğrulanmış bir adres taşımıyor
                // (erişim token'ları çoğu zaman taşımaz) ya da adres başka bir hesapta. Hesap, adres gelene kadar
                // kimlik uçlarının dışında hiçbir şey yapamaz.
                LogUnverifiable(Logger(http));
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
                when (HaldusDb.IsUniqueViolation(exception, AuthModel.ExternalIndex) || HaldusDb.IsUniqueViolation(exception, AuthModel.EmailIndex))
            {
                // Yalnızca iki çakışma karşılanır; başka bir hata (geçici bir arıza) yutulmaz ve hesabı adressiz
                // bırakmaz: istek hata verir, bir sonraki istek yeniden dener.
                // Aynı kişinin aynı anda gelen iki ilk isteğinden ikincisi: hesap yeniden okunur.
                db.Entry(account).State = EntityState.Detached;
                var existing = await FindAsync(db, issuer, subject, cancellationToken);

                if (existing is null)
                {
                    // Çakışan adresti (kontrol ile yazma arasında başkası aldı): kimlik, adressiz olarak yine açılır.
                    account.Email = Placeholder();
                    account.EmailVerifiedAt = null;
                    db.Add(account);

                    try
                    {
                        await db.SaveChangesAsync(cancellationToken);
                        existing = account;
                    }
                    catch (DbUpdateException retry) when (HaldusDb.IsUniqueViolation(retry, AuthModel.ExternalIndex))
                    {
                        // Aynı anda yeniden deneyen öteki istek hesabı açtı.
                        db.Entry(account).State = EntityState.Detached;
                        existing = await FindAsync(db, issuer, subject, cancellationToken);
                    }
                }

                if (existing is null)
                {
                    return null;
                }

                account = existing;
            }
        }

        Guid? sessionId = null;

        if (openSession)
        {
            // Çerezle açılan oturum sunucuda da bir kayıttır; çıkış onu iptal eder.
            var now = HaldusTime.UtcNow(http.RequestServices.GetRequiredService<TimeProvider>());
            var lifetime = TimeSpan.FromMinutes(http.RequestServices.GetRequiredService<ShopOidcOptions>().SessionMinutes);
            var session = new ShopSession { Id = Guid.CreateVersion7(), AccountId = account.Id, CreatedAt = now, ExpiresAt = now + lifetime };
            db.Add(session);
            await db.SaveChangesAsync(cancellationToken);
            sessionId = session.Id;
        }

        return Principal(account, options, scheme, sessionId, trustEmail, issuedAt);
    }

    /// <summary>Çerezdeki kimliği oturumuyla sınar ve hesaptan yeniden kurar; oturum kapandıysa veya hesap yoksa null.</summary>
    private static async Task<ClaimsPrincipal?> RestoreAsync(HttpContext http, ClaimsPrincipal? stored)
    {
        if (!Guid.TryParse(stored?.FindFirstValue(AuthEndpoints.SessionClaim), out var sessionId))
        {
            return null;
        }

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var now = HaldusTime.UtcNow(http.RequestServices.GetRequiredService<TimeProvider>());

        var accountId = await AuthEndpoints.Live(db.Set<ShopSession>(), now)
            .Where(session => session.Id == sessionId)
            .Select(session => (Guid?)session.AccountId)
            .FirstOrDefaultAsync(http.RequestAborted);

        return accountId is not null
            && await db.Set<ShopAccount>().AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == accountId, http.RequestAborted) is { } account
                ? Principal(
                    account,
                    http.RequestServices.GetRequiredService<IOptions<ShopAuthOptions>>().Value,
                    CookieScheme,
                    sessionId,
                    http.RequestServices.GetRequiredService<ShopOidcOptions>().TrustEmailVerified)
                : null;
    }

    private static ClaimsPrincipal Principal(ShopAccount account, ShopAuthOptions options, string scheme, Guid? sessionId, bool trustEmail, long? issuedAt = null)
    {
        // Adres, sağlayıcıya güvenildiği sürece doğrulanmış sayılır: güven sonradan kapatılırsa, daha önce doğrulanmış
        // yazılmış bir hesap da rolünü kaybeder.
        var verified = trustEmail && account.EmailVerifiedAt is not null;

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
                new Claim(ClaimTypes.Email, account.Email),
                new Claim("tenant_id", account.TenantId.ToString()),
                new Claim(AuthEndpoints.VerifiedClaim, verified ? "true" : "false"),
            ],
            scheme);

        if (sessionId is { } id)
        {
            identity.AddClaim(new Claim(AuthEndpoints.SessionClaim, id.ToString()));
        }

        if (issuedAt is { } issued)
        {
            identity.AddClaim(new Claim(IssuedAtClaim, issued.ToString(CultureInfo.InvariantCulture)));
        }

        if (verified && options.IsStaff(account))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, SharedWrites.StaffRole));
        }

        return new ClaimsPrincipal(identity);
    }

    private static Task<ShopAccount?> FindAsync(AppDbContext db, string issuer, string subject, CancellationToken cancellationToken) =>
        db.Set<ShopAccount>().AsNoTracking().FirstOrDefaultAsync(
            candidate => candidate.ExternalIssuer == issuer && candidate.ExternalSubject == subject,
            cancellationToken);

    // Hesabın adresi benzersiz ve dolu olmalıdır; doğrulanmış bir adresi olmayan dış kimlik, kimseye ait olamayacak
    // (ve e-posta gönderilemeyecek) bir adres alır.
    // Rastgeledir: kimlikten türetilseydi, bir kişinin sağlayıcıdaki kimliğini bilen biri o adresle önceden kayıt
    // olup kişinin hesabının açılmasını engelleyebilirdi.
    private static string Placeholder() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + "@external.invalid";

    /// <summary>Sağlayıcının doğruladığını söylediği adres; sağlayıcıya bu konuda güvenilmiyorsa ya da adres yoksa null.</summary>
    private static string? VerifiedAddress(ClaimsPrincipal external, bool trustEmail) =>
        trustEmail
        && string.Equals(external.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase)
        && external.FindFirstValue("email")?.Trim().ToLowerInvariant() is { Length: > 0 and <= 256 } email
            ? email
            : null;

    /// <summary>
    /// Token'ın verildiği an (saniye): yalnızca <c>iat</c>. Başka bir alan onun yerine geçmez: <c>nbf</c> sabit veya
    /// geriye çekilmiş olabilir, <c>auth_time</c> yenilenen token'da eskidir; ikisi de hesabı ilk "her yerden çık"tan
    /// sonra kalıcı olarak kilitlerdi. Kesirli yazılmış değeri (RFC 7519 izin verir) token kitaplığı en yakın tam
    /// sayıya yuvarlayarak verir; öyle bir sağlayıcıda sınır yarım saniyeye kadar kayabilir.
    /// </summary>
    private static long? IssuedAt(ClaimsPrincipal external) =>
        decimal.TryParse(external.FindFirstValue("iat"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
        && seconds is >= 0 and < 253402300800m
            ? (long)decimal.Floor(seconds)
            : null;

    // Adresler yazılışlarıyla değil anlamlarıyla karşılaştırılır: varsayılan bağlantı noktası, büyük-küçük harf,
    // sondaki eğik çizgi, sunucu adının sonundaki nokta ve uluslararası adların iki yazımı (punycode) aynı sayılır.
    private static bool SameAuthority(string left, string right) =>
        Uri.TryCreate(left, UriKind.Absolute, out var first)
        && Uri.TryCreate(right, UriKind.Absolute, out var second)
        && string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.IdnHost.TrimEnd('.'), second.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
        && first.Port == second.Port
        && string.Equals(first.AbsolutePath.TrimEnd('/'), second.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim().TrimEnd('/'), right.Trim().TrimEnd('/'), StringComparison.Ordinal);

    private static ILogger Logger(HttpContext http) =>
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ExternalSignIn).FullName!);

    [LoggerMessage(Level = LogLevel.Information, Message = "The sign-in at the identity provider did not complete ({Reason}).")]
    private static partial void LogRemoteFailure(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An access token of the identity provider was rejected: it does not say when it was issued (iat), or says a time further ahead than the accepted clock skew.")]
    private static partial void LogNoIssuedAt(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An account of the identity provider was opened without a verified address while Shop:Auth:RequireVerifiedEmail is on; it can do nothing until a token carries email and email_verified.")]
    private static partial void LogUnverifiable(ILogger logger);

    private static void RequireAuthority(string section, string authority, bool requireHttps, bool development)
    {
        Require(
            Uri.TryCreate(authority, UriKind.Absolute, out var address) && address.Scheme is "https" or "http",
            $"{section}:Authority must be an absolute http or https address.");
        Require(
            address!.Scheme == "https" || !requireHttps,
            $"{section}:Authority must use https (RequireHttpsMetadata can be turned off only for development).");
        Require(
            requireHttps || development,
            $"{section}:RequireHttpsMetadata can be turned off only in the Development environment.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

/// <summary>İstemcilerin giriş ekranını kurmak için okuduğu ayarlar.</summary>
public sealed record AuthConfig(bool PasswordSignIn, ExternalProviderConfig? Provider, bool ServerSignIn);

/// <summary>Panel ve mobil uygulamanın sağlayıcıyla giriş için kullanacağı ayarlar (gizli bir şey taşımaz).</summary>
public sealed record ExternalProviderConfig(string Authority, string ClientId, string Scopes);
