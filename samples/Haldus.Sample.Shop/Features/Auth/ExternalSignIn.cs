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

    /// <summary>
    /// Çerezle kimliği doğrulanan ve yazan (güvenli olmayan) her istek bu başlığı taşımalıdır. Başka bir siteden
    /// gönderilen bir form onu ekleyemez; çerezin kendiliğinden gönderilmesine dayanan istek sahteciliğini (CSRF) keser.
    /// </summary>
    public const string RequestHeader = "X-Shop-Request";

    public static void AddExternalSignIn(this AuthenticationBuilder builder, ShopJwtOptions jwt, ShopOidcOptions oidc, bool development)
    {
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
                        if (await ResolveAsync(context.HttpContext, context.Principal, JwtScheme) is { } principal)
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
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;

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
                    OnTokenValidated = async context =>
                    {
                        if (await ResolveAsync(context.HttpContext, context.Principal, CookieScheme) is { } principal)
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
    internal static async Task<ClaimsPrincipal?> ResolveAsync(HttpContext http, ClaimsPrincipal? external, string scheme)
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
            var verified = string.Equals(external.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);

            // Adres yalnızca sağlayıcı onu doğruladıysa ve başka bir hesapta değilse alınır. Var olan bir hesaba adres
            // üzerinden bağlanılmaz: aynı adresi taşıdığını söyleyen bir dış kimlik, parolayla açılmış hesabı ele
            // geçirmemelidir. Öyle bir kimlik kendi ayrı hesabını (ve kiracısını) alır.
            var usable = verified
                && email is { Length: > 0 and <= 256 }
                && !await db.Set<ShopAccount>().AnyAsync(candidate => candidate.Email == email, cancellationToken);

            account = new ShopAccount
            {
                Id = Guid.CreateVersion7(),
                Email = usable ? email! : Placeholder(issuer, subject),
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
                // Aynı kişinin aynı anda gelen iki ilk isteğinden ikincisi (ya da adres bu arada alındı): hesap yeniden okunur.
                db.Entry(account).State = EntityState.Detached;
                account = await FindAsync(db, issuer, subject, cancellationToken);

                if (account is null)
                {
                    return null;
                }
            }
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
                new Claim(ClaimTypes.Email, account.Email),
                new Claim("tenant_id", account.TenantId.ToString()),
                new Claim(AuthEndpoints.VerifiedClaim, account.EmailVerifiedAt is null ? "false" : "true"),
            ],
            scheme);

        if (options.IsStaff(account))
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
    private static string Placeholder(string issuer, string subject) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer}\n{subject}")))[..32] + "@external.invalid";

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
