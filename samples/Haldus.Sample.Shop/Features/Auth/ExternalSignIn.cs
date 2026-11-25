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

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority);
}

/// <summary>Bir kimlik sağlayıcıdan gelen kimliğin örnek mağazanın hesabına bağlanması ve şemaların kurulması.</summary>
public static class ExternalSignIn
{
    /// <summary>Gelen isteğe bakıp doğru şemayı seçen şema; yalnızca dış kimlik açıkken kurulur.</summary>
    public const string SelectorScheme = "Shop";

    public const string JwtScheme = "ShopJwt";
    public const string CookieScheme = "ShopCookie";
    public const string OidcScheme = "ShopOidc";
    public const string CookieName = "shop.session";

    private static readonly TimeSpan CookieLifetime = TimeSpan.FromHours(8);

    /// <summary>
    /// Çerezle kimliği doğrulanan ve yazan (güvenli olmayan) her istek bu başlığı taşımalıdır. Başka bir siteden
    /// gönderilen bir form onu ekleyemez; çerezin kendiliğinden gönderilmesine dayanan istek sahteciliğini (CSRF) keser.
    /// </summary>
    public const string RequestHeader = "X-Shop-Request";

    public static void AddExternalSignIn(this AuthenticationBuilder builder, ShopJwtOptions jwt, ShopOidcOptions oidc, bool development)
    {
        // Hesabın adresi ilk gelişte yazılır; aynı sağlayıcıya iki yoldan farklı güvenilseydi, kullanıcı ilk girişini
        // güvenilen yoldan yaparak ötekinin kuralını aşardı.
        Require(
            !jwt.Enabled || !oidc.Enabled || !SameAuthority(jwt.Authority!, oidc.Authority!) || jwt.TrustEmailVerified == oidc.TrustEmailVerified,
            "Shop:Auth:Jwt and Shop:Auth:Oidc point at the same provider; TrustEmailVerified must be the same in both.");

        if (jwt.Enabled)
        {
            Require(!string.IsNullOrWhiteSpace(jwt.Audience), "Shop:Auth:Jwt:Audience is required when Shop:Auth:Jwt:Authority is set.");
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

            builder.AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = CookieLifetime;
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
                        context.RejectPrincipal();
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
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        context.HandleResponse();
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        if (await ResolveAsync(context.HttpContext, context.Principal, CookieScheme, oidc.TrustEmailVerified, openSession: true) is { } principal)
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

        var account = await FindAsync(db, issuer, subject, cancellationToken);

        if (account is null)
        {
            var now = HaldusTime.UtcNow(http.RequestServices.GetRequiredService<TimeProvider>());
            var email = external.FindFirstValue("email")?.Trim().ToLowerInvariant();
            // Adresin doğrulandığını söyleyen sağlayıcıdır; ona ancak açıkça güvenildiyse inanılır (TrustEmailVerified).
            var verified = trustEmail && string.Equals(external.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);

            // Adres yalnızca sağlayıcı onu doğruladıysa ve başka bir hesapta değilse alınır. Var olan bir hesaba adres
            // üzerinden bağlanılmaz: aynı adresi taşıdığını söyleyen bir dış kimlik, parolayla açılmış hesabı ele
            // geçirmemelidir. Öyle bir kimlik kendi ayrı hesabını (ve kiracısını) alır.
            var usable = verified
                && email is { Length: > 0 and <= 256 }
                && !await db.Set<ShopAccount>().AnyAsync(candidate => candidate.Email == email, cancellationToken);

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

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Aynı kişinin aynı anda gelen iki ilk isteğinden ikincisi: hesap yeniden okunur.
                db.Entry(account).State = EntityState.Detached;
                var existing = await FindAsync(db, issuer, subject, cancellationToken);

                if (existing is null)
                {
                    // Çakışan adresti (kontrol ile yazma arasında başkası aldı): kimlik, adressiz olarak yine açılır.
                    account.Email = Placeholder();
                    account.EmailVerifiedAt = null;
                    db.Add(account);
                    await db.SaveChangesAsync(cancellationToken);
                }
                else
                {
                    account = existing;
                }
            }
        }

        Guid? sessionId = null;

        if (openSession)
        {
            // Çerezle açılan oturum sunucuda da bir kayıttır; çıkış onu iptal eder.
            var now = HaldusTime.UtcNow(http.RequestServices.GetRequiredService<TimeProvider>());
            var session = new ShopSession { Id = Guid.CreateVersion7(), AccountId = account.Id, CreatedAt = now, ExpiresAt = now + CookieLifetime };
            db.Add(session);
            await db.SaveChangesAsync(cancellationToken);
            sessionId = session.Id;
        }

        return Principal(account, options, scheme, sessionId, trustEmail);
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

    private static ClaimsPrincipal Principal(ShopAccount account, ShopAuthOptions options, string scheme, Guid? sessionId, bool trustEmail)
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

    private static bool SameAuthority(string left, string right) =>
        string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

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
