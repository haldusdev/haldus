using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Haldus.Sample.Shop.Features.Auth;

/// <summary>
/// Parola ile giriş ve taşıyıcı (bearer) token. ASP.NET Core'un yerleşik şeması kullanılır
/// (<c>AddBearerToken</c>): token'lar Data Protection ile korunan, sunucunun kendisinin ürettiği değerlerdir; ayrı bir
/// kimlik sağlayıcısı veya paket gerekmez. Gerçek bir uygulama burada kendi kimlik sağlayıcısını (OpenID Connect)
/// kullanabilir; Haldus açısından fark yoktur, çünkü o yalnızca claim'leri okur.
/// </summary>
public static partial class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    /// <summary>Token'ın bağlı olduğu oturum (<see cref="ShopSession"/>).</summary>
    internal const string SessionClaim = "sid";

    /// <summary>Yenileme token'ının sırası (<see cref="ShopSession.RefreshVersion"/>).</summary>
    internal const string RefreshClaim = "rv";

    internal const string VerifiedClaim = "email_verified";

    // Crockford base32: birbirine karışan harfler (I, L, O, U) yoktur. Sekiz karakter 40 bittir.
    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int CodeLength = 8;

    private static readonly TimeSpan MailTimeout = TimeSpan.FromSeconds(30);

    private static readonly PasswordHasher<ShopAccount> Hasher = new();

    // Hesap bulunamadığında da bir parola doğrulaması yapılır; yoksa yanıt süresi e-postanın kayıtlı olup olmadığını söylerdi.
    private static readonly string DummyHash = Hasher.HashPassword(new ShopAccount(), Guid.NewGuid().ToString());

    public static IServiceCollection AddShopAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<ShopAuthOptions>(configuration.GetSection("Shop:Auth"));
        services.Configure<SmtpOptions>(configuration.GetSection("Shop:Smtp"));

        // Parola girişi her zaman vardır. Bir kimlik sağlayıcı iki ayrı yoldan, her biri kendi ayarıyla eklenir ve biri
        // ötekini gerektirmez: sağlayıcının JWT erişim token'larının kabulü (Shop:Auth:Jwt) ve backend'in kendisinin
        // sağlayıcıya yönlendirip çerezle oturum açması (Shop:Auth:Oidc). İkisi de verilmezse kimlik doğrulama,
        // yalnızca kendi taşıyıcı token şemamızla, eskisi gibi kurulur.
        var jwt = configuration.GetSection("Shop:Auth:Jwt").Get<ShopJwtOptions>() ?? new ShopJwtOptions();
        var oidc = configuration.GetSection("Shop:Auth:Oidc").Get<ShopOidcOptions>() ?? new ShopOidcOptions();
        services.AddSingleton(jwt);
        services.AddSingleton(oidc);

        // Doğrulanmış adres zorunluyken adresine güvenilmeyen bir sağlayıcının hesapları hiçbir şey yapamazdı
        // (adreslerini bizim kod akışımızla da doğrulayamazlar).
        if (configuration.GetValue<bool>("Shop:Auth:RequireVerifiedEmail")
            && ((jwt.Enabled && !jwt.TrustEmailVerified) || (oidc.Enabled && !oidc.TrustEmailVerified)))
        {
            throw new InvalidOperationException(
                "Shop:Auth:RequireVerifiedEmail needs TrustEmailVerified for every configured identity provider; their accounts could never be verified otherwise.");
        }

        if (jwt.Enabled || oidc.Enabled)
        {
            services.AddAuthentication(ExternalSignIn.SelectorScheme)
                .AddPolicyScheme(
                    ExternalSignIn.SelectorScheme,
                    displayName: null,
                    options => options.ForwardDefaultSelector = context => ExternalSignIn.Select(context, jwt.Enabled, oidc.Enabled))
                .AddBearerToken()
                .AddExternalSignIn(jwt, oidc, environment.IsDevelopment());
        }
        else
        {
            services.AddAuthentication(BearerTokenDefaults.AuthenticationScheme).AddBearerToken();
        }

        // Giriş yapılmadan hiçbir uca ulaşılmaz; açık kalması gerekenler bunu kendileri söyler (AllowAnonymous: giriş ve
        // kayıt, token ile erişilen kayıtlar, süreli indirme bağlantıları, misafir sepeti). Bu olmadan kiracıya ait
        // olmayan kaynaklar (ürünler, kategoriler) token'sız da yazılabilirdi. Demo kimliği kendi başına bir kimlik
        // doğrulama olmadığı için o kipte bu kural konmaz.
        // Giriş yapmış olmak yetmez: kimseye ait olmayan, ortak kaynakları yalnızca personel yazar (SharedWrites).
        var demo = configuration.GetValue<bool>("Shop:DemoIdentity");
        services.AddAuthorization(options =>
        {
            if (!demo)
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new SharedWrites())
                    .Build();
            }
        });
        services.AddSingleton<IAuthorizationHandler, SharedWritesHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ForbiddenProblemHandler>();

        if (string.IsNullOrEmpty(configuration["Shop:Smtp:Host"]))
        {
            services.AddSingleton<IAccountMailer, LogAccountMailer>();
        }
        else
        {
            services.AddSingleton<IAccountMailer, SmtpAccountMailer>();
        }

        // Parola ve kod deneyen uçlar adres başına sınırlanır. Hesap kilidi ve kod bütçesi tek bir hesabı korur; bu
        // sınır, birçok hesabı deneyen tek bir kaynağı yavaşlatır. Bir vekil sunucunun arkasında adres vekilin
        // adresidir; vekil tanıtılmalıdır (Shop:KnownProxies), yoksa herkes tek bir sınırı paylaşır.
        services.AddRateLimiter(limiter =>
        {
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var messages = context.HttpContext.RequestServices.GetRequiredService<IStringLocalizer<ShopMessages>>();
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await Results.Problem(detail: messages["TooManyAttempts"], statusCode: StatusCodes.Status429TooManyRequests)
                    .ExecuteAsync(context.HttpContext);
            };

            limiter.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                PartitionOf(context.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = context.RequestServices.GetRequiredService<IOptions<ShopAuthOptions>>().Value.RequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                }));
        });

        return services;
    }

    /// <summary>
    /// Hız sınırının kimi saydığı. IPv6'da bir abone tek bir adres değil bütün bir /64 bloğu alır; adresin tamamı
    /// sayılsaydı sınır, bloğun içinde adres değiştirerek aşılırdı.
    /// </summary>
    public static string PartitionOf(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? Convert.ToHexString(address.GetAddressBytes().AsSpan(0, 8))
            : address.ToString();
    }

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/register", RegisterAsync)
            .WithName("Register")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .AddEndpointFilter<HaldusValidationFilter<RegisterRequest>>()
            .Produces<AccessTokenResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/login", LoginAsync)
            .WithName("Login")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .AddEndpointFilter<HaldusValidationFilter<LoginRequest>>()
            .Produces<AccessTokenResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/refresh", RefreshAsync)
            .WithName("RefreshSession")
            .AllowAnonymous()
            .AddEndpointFilter<HaldusValidationFilter<RefreshRequest>>()
            .Produces<AccessTokenResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Çıkış oturumsuz da çağrılabilir: erişim token'ı reddedilen (süresi dolmuş, yenilenemeyen) bir istemci de
        // oturumunu yenileme token'ıyla kapatabilmelidir. Yanıt her durumda aynıdır.
        auth.MapPost("/logout", LogoutAsync)
            .WithName("Logout")
            .AllowAnonymous()
            .Produces(StatusCodes.Status204NoContent);

        auth.MapPost("/logout-all", LogoutEverywhereAsync)
            .WithName("LogoutEverywhere")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapGet("/me", MeAsync)
            .WithName("GetCurrentAccount")
            .RequireAuthorization()
            .Produces<AccountInfo>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/password/forgot", ForgotPasswordAsync)
            .WithName("ForgotPassword")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .AddEndpointFilter<HaldusValidationFilter<ForgotPasswordRequest>>()
            .Produces(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/password/reset", ResetPasswordAsync)
            .WithName("ResetPassword")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .AddEndpointFilter<HaldusValidationFilter<ResetPasswordRequest>>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/password/change", ChangePasswordAsync)
            .WithName("ChangePassword")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicy)
            .AddEndpointFilter<HaldusValidationFilter<ChangePasswordRequest>>()
            .Produces<AccessTokenResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/email/verify", VerifyEmailAsync)
            .WithName("VerifyEmail")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicy)
            .AddEndpointFilter<HaldusValidationFilter<VerifyEmailRequest>>()
            .Produces<AccessTokenResponse>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // İstemciler giriş ekranını buna göre kurar: sağlayıcıyla giriş ancak ayarlandıysa sunulur.
        auth.MapGet("/config", (ShopJwtOptions jwt, ShopOidcOptions oidc) => new AuthConfig(
                PasswordSignIn: true,
                Provider: jwt.Enabled && !string.IsNullOrWhiteSpace(jwt.ClientId)
                    ? new ExternalProviderConfig(jwt.Authority!, jwt.ClientId, jwt.Scopes)
                    : null,
                ServerSignIn: oidc.Enabled))
            .WithName("GetAuthConfig")
            .AllowAnonymous()
            .Produces<AuthConfig>();

        if (app.ServiceProvider.GetRequiredService<ShopOidcOptions>().Enabled)
        {
            // Backend'in kendi yönlendirmeli girişi: tarayıcı sağlayıcıya gider, dönüşte oturum çerezi yazılır.
            // Yalnızca ayarlandıysa vardır; şemaya (openapi.json) girmez.
            auth.MapGet("/oidc/sign-in", (string? returnUrl) => Results.Challenge(
                    new AuthenticationProperties { RedirectUri = LocalPath(returnUrl) },
                    [ExternalSignIn.OidcScheme]))
                .AllowAnonymous()
                .ExcludeFromDescription();

            // Çerez silinir ve oturumu sunucuda da kapanır: kopyalanmış bir çerez de artık geçmez.
            auth.MapPost("/oidc/sign-out", async (ClaimsPrincipal user, AppDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
                {
                    // Çerez isteğe eklenmediyse (başka bir siteden gönderilen form: `SameSite=Lax`) kapatılacak bir
                    // oturum yoktur ve yanıt çerezi sildirmez; yoksa başka bir site kullanıcıyı oturumdan düşürebilirdi.
                    if (user.Identity?.AuthenticationType != ExternalSignIn.CookieScheme)
                    {
                        return Results.NoContent();
                    }

                    if (Guid.TryParse(user.FindFirstValue(SessionClaim), out var sessionId))
                    {
                        await RevokeAsync(db.Set<ShopSession>().Where(session => session.Id == sessionId), HaldusTime.UtcNow(time), cancellationToken);
                    }

                    return Results.SignOut(authenticationSchemes: [ExternalSignIn.CookieScheme]);
                })
                .AllowAnonymous()
                .WithMetadata(new ChangesSession())
                .ExcludeFromDescription();
        }

        auth.MapPost("/email/resend", ResendVerificationAsync)
            .WithName("ResendEmailVerification")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicy)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// Sunulan token'ı oturumuyla birlikte sınar ve reddedilen token'ı her kaynakta 401 ile yanıtlar.
    /// <list type="bullet">
    /// <item>Reddedilen bir token sunan istek, token sunmayan istekle karıştırılmaz: istemci oturumunu yenilemesi
    /// gerektiğini bu yanıttan anlar.</item>
    /// <item>Token kendi başına geçerlidir, ama oturumu iptal edilmiş veya süresi dolmuş olabilir (çıkış, parola
    /// değişimi); bu yüzden oturum her istekte veritabanına sorulur. Bedeli istek başına bir birincil anahtar
    /// sorgusudur.</item>
    /// <item><c>Shop:Auth:RequireVerifiedEmail</c> açıksa e-postası doğrulanmamış hesap yalnızca kimlik uçlarını
    /// kullanabilir.</item>
    /// </list>
    /// Token istemeyen uçlar (giriş, yenileme, token ile erişim) reddedilen bir token yüzünden kapanmaz: süresi dolmuş
    /// bir oturum onlara takılmamalıdır. Ama iptal edilmiş bir oturumun token'ı onlarda da kimlik sayılmaz; istek
    /// kimliksiz sürer (yoksa çıkış yapılmış bir oturumun token'ı, süresi dolana kadar bu uçlarda kullanıcıyı ve
    /// kiracıyı tanıtmaya devam ederdi).
    /// </summary>
    public static IApplicationBuilder UseSessionValidation(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var open = context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var messages = context.RequestServices.GetRequiredService<IStringLocalizer<ShopMessages>>();

            if (context.User.Identity?.IsAuthenticated != true)
            {
                if (!open && context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    await RejectAsync(context, messages);
                    return;
                }

                await next(context);
                return;
            }

            // Token bir oturuma bağlıdır; başka bir şemayla (ör. sonradan eklenen bir kimlik sağlayıcı) gelen kimlik buraya girmez.
            if (context.User.Identity.AuthenticationType == BearerTokenDefaults.AuthenticationScheme)
            {
                var db = context.RequestServices.GetRequiredService<AppDbContext>();
                var now = HaldusTime.UtcNow(context.RequestServices.GetRequiredService<TimeProvider>());

                if (!Guid.TryParse(context.User.FindFirstValue(SessionClaim), out var sessionId)
                    || !await Live(db.Set<ShopSession>(), now).AnyAsync(session => session.Id == sessionId, context.RequestAborted))
                {
                    if (open)
                    {
                        context.User = new ClaimsPrincipal(new ClaimsIdentity());
                        await next(context);
                        return;
                    }

                    await RejectAsync(context, messages);
                    return;
                }

            }

            // Çerez kendiliğinden gönderilir; yazan bir isteğin bu uygulamanın kendi kodundan geldiğini, başka bir
            // sitenin ekleyemeyeceği başlık gösterir. Token taşıyan isteklerde gerek yoktur: token kendiliğinden gitmez.
            // Giriş istemeyen uçlardan yalnızca çerezin oturumunu değiştireni (çerezle çıkış) başlığı ister.
            if (context.User.Identity.AuthenticationType == ExternalSignIn.CookieScheme
                && !HttpMethods.IsGet(context.Request.Method)
                && !HttpMethods.IsHead(context.Request.Method)
                && !HttpMethods.IsOptions(context.Request.Method)
                && !context.Request.Headers.ContainsKey(ExternalSignIn.RequestHeader))
            {
                if (!open || context.GetEndpoint()?.Metadata.GetMetadata<ChangesSession>() is not null)
                {
                    await Results.Problem(detail: messages["RequestHeaderRequired"], statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
                    return;
                }

                // Giriş istemeyen öteki uçlar (parola girişi, kayıt, yenileme, misafir sepeti) çerez tarayıcıda dursa
                // da çalışmalıdır: çerez orada kimlik sayılmaz, istek kimliksiz sürer.
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
            }

            // Hangi yoldan girilmiş olursa olsun (parola, sağlayıcı) geçerlidir.
            if (!open
                && context.RequestServices.GetRequiredService<IOptions<ShopAuthOptions>>().Value.RequireVerifiedEmail
                && context.User.FindFirstValue(VerifiedClaim) != "true"
                && !context.Request.Path.StartsWithSegments("/auth"))
            {
                await Results.Problem(detail: messages["EmailNotVerified"], statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
                return;
            }

            await next(context);
        });

    // Girişten sonra yalnızca bu uygulamanın içindeki bir yola dönülür; dışarıyı gösteren adres (açık yönlendirme) köke çevrilir.
    private static string LocalPath(string? returnUrl) =>
        returnUrl is { Length: > 0 and <= 2048 }
        && returnUrl[0] == '/'
        && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
        && !returnUrl.Any(char.IsControl)
            ? returnUrl
            : "/";

    private static async Task RejectAsync(HttpContext context, IStringLocalizer<ShopMessages> messages)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        await Results.Problem(detail: messages["SessionExpired"], statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        AppDbContext db,
        TimeProvider time,
        IAccountMailer mailer,
        IOptions<ShopAuthOptions> options,
        IStringLocalizer<ShopMessages> messages,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var email = Normalize(request.Email);

        // Kayıt, adresin alınmış olduğunu söyler (409). Bu bilinçli bir seçimdir: aksi, kullanıcıya hiçbir şey
        // söylemeden kaydı yutmak olurdu. Adresin kayıtlı olup olmadığını gizlemesi gereken bir uygulama burada da
        // aynı yanıtı verip adrese bir e-posta göndermelidir.
        if (await db.Set<ShopAccount>().AnyAsync(account => account.Email == email, cancellationToken))
        {
            return EmailTaken(messages);
        }

        var now = HaldusTime.UtcNow(time);
        var account = new ShopAccount
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            TenantId = Guid.CreateVersion7(),
            CreatedAt = now,
        };
        account.PasswordHash = Hasher.HashPassword(account, request.Password);

        var session = NewSession(account, now, options.Value);
        db.Add(account);
        db.Add(session);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (HaldusDb.IsUniqueViolation(exception, AuthModel.EmailIndex))
        {
            // Aynı e-postayla aynı anda gelen iki kayıttan ikincisi.
            return EmailTaken(messages);
        }

        await SendCodeAsync(db, account, AccountCodePurpose.EmailVerification, now, mailer, options.Value, loggers, cancellationToken);
        return SignIn(account, session, options.Value);
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        AppDbContext db,
        TimeProvider time,
        IOptions<ShopAuthOptions> options,
        IStringLocalizer<ShopMessages> messages,
        CancellationToken cancellationToken)
    {
        var email = Normalize(request.Email);
        var now = HaldusTime.UtcNow(time);
        var account = await PasswordAccounts(db).FirstOrDefaultAsync(candidate => candidate.Email == email, cancellationToken);

        // Hesap yoksa da aynı iş yapılır (bir güncelleme ve bir parola doğrulaması); yanıtın içeriği aynıdır ve süresi
        // belirgin biçimde ayrışmaz. Kilitli hesapta doğru parola da kabul edilmez.
        var counted = await CountAttemptAsync(db, account?.Id ?? Guid.Empty, now, options.Value, cancellationToken);
        var verified = Hasher.VerifyHashedPassword(
            account ?? new ShopAccount(),
            counted ? account!.PasswordHash : DummyHash,
            request.Password);

        if (account is null || !counted || verified == PasswordVerificationResult.Failed)
        {
            return InvalidCredentials(messages);
        }

        // Oturum önce yazılır, sonra sayım sıfırlanır; sıfırlama yalnızca parola bu arada değişmediyse geçer. Sıra
        // tersine olsaydı, tam bu sırada yapılan bir parola değişimi oturumları kapattıktan sonra eski parolayla
        // doğrulanmış bu giriş yeni bir oturum açabilirdi.
        var session = NewSession(account, now, options.Value);
        db.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        var hash = verified == PasswordVerificationResult.SuccessRehashNeeded
            ? Hasher.HashPassword(account, request.Password)
            : account.PasswordHash;

        var unchanged = await db.Set<ShopAccount>()
            .Where(candidate => candidate.Id == account.Id && candidate.PasswordHash == account.PasswordHash)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.FailedLogins, 0)
                    .SetProperty(candidate => candidate.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(candidate => candidate.PasswordHash, hash),
                cancellationToken) == 1;

        if (!unchanged)
        {
            await RevokeAsync(db.Set<ShopSession>().Where(candidate => candidate.Id == session.Id), now, cancellationToken);
            return InvalidCredentials(messages);
        }

        return SignIn(account, session, options.Value);
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        AppDbContext db,
        TimeProvider time,
        IOptionsMonitor<BearerTokenOptions> options,
        IOptions<ShopAuthOptions> refresh,
        IStringLocalizer<ShopMessages> messages,
        CancellationToken cancellationToken)
    {
        // Yenileme token'ı da sunucunun ürettiği korunmuş bir değerdir; bozuksa veya süresi dolduysa açılmaz.
        var ticket = options.Get(BearerTokenDefaults.AuthenticationScheme).RefreshTokenProtector.Unprotect(request.RefreshToken);
        var now = HaldusTime.UtcNow(time);

        if (ticket?.Properties.ExpiresUtc is not { } expiresAt
            || now >= expiresAt
            || !Guid.TryParse(ticket.Principal.FindFirstValue(SessionClaim), out var sessionId)
            || !int.TryParse(ticket.Principal.FindFirstValue(RefreshClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            return SessionExpired(messages);
        }

        // Yenileme token'ı bir kez kullanılır: sıra, token'daki değerle aynıysa bir ilerler. Aynı token'ı sunan iki
        // istekten yalnızca biri bu güncellemeyi yapabilir.
        var rotated = await RotateAsync(
            Live(db.Set<ShopSession>(), now).Where(candidate => candidate.Id == sessionId && candidate.RefreshVersion == version),
            now,
            cancellationToken);

        var session = await Live(db.Set<ShopSession>(), now).AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == sessionId, cancellationToken);

        if (!rotated || session is null)
        {
            // Eskimiş bir yenileme token'ı sunuldu. Sıra az önce ilerlediyse bu büyük olasılıkla aynı anda yenileyen
            // ikinci bir istektir (başka bir sekme): oturum kapatılmaz, ama eskimiş token'a yeni kimlik bilgisi de
            // verilmez (verilseydi, çalınmış bir token bu pencerede geçerli bir oturuma çevrilebilirdi); istemci yeni
            // token'ları öteki istekten alır. Bundan eski bir token ise ya çalınmıştır ya da çalan kişi asıl
            // sahibinden önce davranmıştır; hangisi olduğu bilinemez, oturum kapatılır.
            // Yalnızca bir önceki sıra hoş görülür; daha eskisi her zaman yeniden kullanım sayılır.
            var grace = now.AddSeconds(-refresh.Value.RefreshGraceSeconds);
            var justRotated = session is not null && session.RefreshVersion == version + 1 && session.RotatedAt > grace;

            if (!justRotated)
            {
                await RevokeAsync(db.Set<ShopSession>().Where(candidate => candidate.Id == sessionId), now, cancellationToken);
                return SessionExpired(messages);
            }

            // Ayrı bir yanıt: istemci oturumunu kapatmaz, yeni token'ları yenilemeyi yapan öteki istekten bekler.
            return Results.Problem(detail: messages["SessionRenewedElsewhere"], statusCode: StatusCodes.Status409Conflict);
        }

        if (await db.Set<ShopAccount>().AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == session.AccountId, cancellationToken) is not { } account)
        {
            return SessionExpired(messages);
        }

        // Claim'ler hesaptan yeniden kurulur.
        return SignIn(account, session, refresh.Value);
    }

    private static async Task<IResult> LogoutAsync(
        [FromBody] LogoutRequest? request,
        ClaimsPrincipal user,
        AppDbContext db,
        TimeProvider time,
        IOptionsMonitor<BearerTokenOptions> options,
        CancellationToken cancellationToken)
    {
        // Oturumu, sunulan erişim token'ı da yenileme token'ı da tanıtabilir. Yenileme token'ının sırası eskimiş veya
        // süresi dolmuş olabilir; kapatmak için yeter: token'ı elinde tutan zaten o oturumun bir tarafıdır. Bu olmadan
        // yenilemesi reddedilen bir istemci (ör. token'ı çalan kişi önce yenilediyse) oturumu sunucuda kapatamazdı.
        var sessions = new HashSet<Guid>();

        if (Guid.TryParse(user.FindFirstValue(SessionClaim), out var current))
        {
            sessions.Add(current);
        }

        if (request?.RefreshToken is { Length: > 0 and <= LogoutRequest.MaxTokenLength } token
            && options.Get(BearerTokenDefaults.AuthenticationScheme).RefreshTokenProtector.Unprotect(token) is { } ticket
            && Guid.TryParse(ticket.Principal.FindFirstValue(SessionClaim), out var presented))
        {
            sessions.Add(presented);
        }

        if (sessions.Count > 0)
        {
            await RevokeAsync(db.Set<ShopSession>().Where(session => sessions.Contains(session.Id)), HaldusTime.UtcNow(time), cancellationToken);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> LogoutEverywhereAsync(ClaimsPrincipal user, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (AccountId(user) is { } accountId)
        {
            var now = HaldusTime.UtcNow(time);
            await RevokeAsync(db.Set<ShopSession>().Where(session => session.AccountId == accountId), now, cancellationToken);

            // Kimlik sağlayıcının verdiği token'ların sunucuda bir oturumu yoktur; onları bu an geçersiz kılar
            // (ExternalSignIn.ResolveAsync). Sağlayıcıda yeniden alınan token yine geçer.
            // Sağlayıcının saati bizimkinden ilerideyse isteği yapan token "şimdi"den sonra verilmiş görünür ve
            // sınırın üstünde kalırdı; sınır o yüzden en az o token'ın verildiği andır (kabul edilen saat farkıyla
            // sınırlı). Sınır geri de alınmaz.
            DateTimeOffset? cutoff = now;

            if (long.TryParse(user.FindFirstValue(ExternalSignIn.IssuedAtClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                && seconds > now.ToUnixTimeSeconds())
            {
                cutoff = DateTimeOffset.FromUnixTimeSeconds(Math.Min(seconds, (now + MaxClockSkew).ToUnixTimeSeconds()));
            }

            await db.Set<ShopAccount>()
                .Where(account => account.Id == accountId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        account => account.SignedOutEverywhereAt,
                        account => account.SignedOutEverywhereAt > cutoff ? account.SignedOutEverywhereAt : cutoff),
                    cancellationToken);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(ClaimsPrincipal user, AppDbContext db, IOptions<ShopAuthOptions> options, CancellationToken cancellationToken) =>
        await FindAccountAsync(user, db, cancellationToken) is { } account
            ? Results.Ok(account.ExternalIssuer is null
                ? new AccountInfo(account.Id, account.Email, account.TenantId, account.EmailVerifiedAt is not null, options.Value.IsStaff(account))

                // Dış hesabın adresine ne kadar güvenildiği ayara bağlıdır ve her istekte uygulanır; yanıt, isteğin
                // gerçekten taşıdığı yetkiyi söyler (kayıttaki eski değeri değil).
                : new AccountInfo(account.Id, account.Email, account.TenantId, user.FindFirstValue(VerifiedClaim) == "true", user.IsInRole(SharedWrites.StaffRole)))
            : Results.Problem(statusCode: StatusCodes.Status401Unauthorized);

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        AppDbContext db,
        TimeProvider time,
        IAccountMailer mailer,
        IOptions<ShopAuthOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var email = Normalize(request.Email);

        if (await PasswordAccounts(db).FirstOrDefaultAsync(candidate => candidate.Email == email, cancellationToken) is { } account)
        {
            // E-posta yanıt beklemeden gönderilir: posta sunucusunun süresi yanıta yansısaydı, kayıtlı adres yanıtın
            // gecikmesinden anlaşılırdı.
            await SendCodeAsync(db, account, AccountCodePurpose.PasswordReset, HaldusTime.UtcNow(time), mailer, options.Value, loggers, cancellationToken, background: true);
        }
        else
        {
            // Kayıtlı adreste kodun özeti hesaplanır; aynı iş burada da yapılır ki iki yanıtın süresi belirgin biçimde ayrışmasın.
            Hasher.HashPassword(new ShopAccount(), "000000");
        }

        // Yanıt, e-postanın kayıtlı olup olmadığını söylemez.
        return Results.Accepted();
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        AppDbContext db,
        TimeProvider time,
        IOptions<ShopAuthOptions> options,
        IStringLocalizer<ShopMessages> messages,
        CancellationToken cancellationToken)
    {
        var email = Normalize(request.Email);
        var now = HaldusTime.UtcNow(time);
        var account = await PasswordAccounts(db).FirstOrDefaultAsync(candidate => candidate.Email == email, cancellationToken);

        // Hesap yoksa da bir kod doğrulaması yapılır (ConsumeCodeAsync); yanıtın süresi adresin kayıtlı olduğunu söylemez.
        if (!await ConsumeCodeAsync(db, account, AccountCodePurpose.PasswordReset, request.Code, now, cancellationToken) || account is null)
        {
            return InvalidCode(messages);
        }

        // Kodu e-postasından okuyan kişi adresin sahibidir: sıfırlama e-postayı da doğrular ve kilidi açar.
        var hash = Hasher.HashPassword(account, request.NewPassword);
        await db.Set<ShopAccount>()
            .Where(candidate => candidate.Id == account.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.PasswordHash, hash)
                    .SetProperty(candidate => candidate.FailedLogins, 0)
                    .SetProperty(candidate => candidate.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(candidate => candidate.EmailVerifiedAt, candidate => candidate.EmailVerifiedAt ?? now),
                cancellationToken);

        // Parola önce değişir, oturumlar sonra kapanır (girişteki sıranın karşılığı). Eski oturumların hepsi kapanır.
        await RevokeAsync(db.Set<ShopSession>().Where(session => session.AccountId == account.Id), now, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        TimeProvider time,
        IOptions<ShopAuthOptions> options,
        IStringLocalizer<ShopMessages> messages,
        CancellationToken cancellationToken)
    {
        if (await FindAccountAsync(user, db, cancellationToken) is not { } account)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        if (account.ExternalIssuer is not null)
        {
            return ManagedByProvider(messages);
        }

        // Geçerli parolayı sormak da bir parola denemesidir ve girişle aynı sayaca yazılır; yoksa ele geçirilmiş bir
        // erişim token'ı parolayı burada, kilide takılmadan deneyebilirdi.
        var now = HaldusTime.UtcNow(time);
        var counted = await CountAttemptAsync(db, account.Id, now, options.Value, cancellationToken);

        if (!counted || Hasher.VerifyHashedPassword(account, account.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = [messages["CurrentPasswordIncorrect"]] });
        }

        var hash = Hasher.HashPassword(account, request.NewPassword);
        await db.Set<ShopAccount>()
            .Where(candidate => candidate.Id == account.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.PasswordHash, hash)
                    .SetProperty(candidate => candidate.FailedLogins, 0)
                    .SetProperty(candidate => candidate.LockedUntil, (DateTimeOffset?)null),
                cancellationToken);

        // Bütün oturumlar kapanır, isteği yapan da: token'lar kopyalanmışsa kopyalanan, parolayı değiştirenin kendi
        // oturumudur ve açık kalsaydı kopya da geçerli kalırdı. İsteği yapana yeni bir oturum açılır.
        await RevokeAsync(db.Set<ShopSession>().Where(session => session.AccountId == account.Id), now, cancellationToken);

        var session = NewSession(account, now, options.Value);
        db.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return SignIn(account, session, options.Value);
    }

    private static async Task<IResult> VerifyEmailAsync(
        VerifyEmailRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        TimeProvider time,
        IOptions<ShopAuthOptions> options,
        IStringLocalizer<ShopMessages> messages,
        CancellationToken cancellationToken)
    {
        if (await FindAccountAsync(user, db, cancellationToken) is not { } account)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        // Sağlayıcı hesabının adresini sağlayıcı doğrular; bu uç ona bizim token'larımızı da vermemelidir.
        if (account.ExternalIssuer is not null)
        {
            return ManagedByProvider(messages);
        }

        if (!Guid.TryParse(user.FindFirstValue(SessionClaim), out var sessionId))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        if (account.EmailVerifiedAt is not null)
        {
            // Zaten doğrulanmış: kod sınanmadığı için yeni token da verilmez (yoksa bu uç, elinde yalnızca bir erişim
            // token'ı olan birine kodsuz bir yenileme token'ı dağıtırdı). İstemci güncel claim'i oturumunu yenileyerek alır.
            return Results.NoContent();
        }

        var now = HaldusTime.UtcNow(time);

        if (!await ConsumeCodeAsync(db, account, AccountCodePurpose.EmailVerification, request.Code, now, cancellationToken))
        {
            return InvalidCode(messages);
        }

        await db.Set<ShopAccount>()
            .Where(candidate => candidate.Id == account.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.EmailVerifiedAt, now), cancellationToken);
        account.EmailVerifiedAt = now;

        // Token'daki claim eskidir; doğrulanmış haliyle yenisi verilir. Yenileme sırası ilerletilmez: sırayı yalnızca
        // yenileme ilerletir, yoksa doğrulamayla aynı anda gelen sıradan bir yenileme "yeniden kullanım" sayılırdı.
        // İstemcinin elindeki önceki yenileme token'ı aynı sırayı taşır; ikisinden ilk kullanılan sırayı ilerletir.
        return await Live(db.Set<ShopSession>(), now).AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == sessionId, cancellationToken) is { } session
            ? SignIn(account, session, options.Value)
            : SessionExpired(messages);
    }

    private static async Task<IResult> ResendVerificationAsync(
        ClaimsPrincipal user,
        AppDbContext db,
        TimeProvider time,
        IAccountMailer mailer,
        IOptions<ShopAuthOptions> options,
        IStringLocalizer<ShopMessages> messages,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (await FindAccountAsync(user, db, cancellationToken) is not { } account)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        if (account.ExternalIssuer is not null)
        {
            return ManagedByProvider(messages);
        }

        if (account.EmailVerifiedAt is null)
        {
            await SendCodeAsync(db, account, AccountCodePurpose.EmailVerification, HaldusTime.UtcNow(time), mailer, options.Value, loggers, cancellationToken);
        }

        return Results.Accepted();
    }

    /// <summary>
    /// Bir parola denemesini sayar ve denemenin yapılıp yapılamayacağını söyler. Sayım, parola sınanmadan önce ve tek
    /// bir ifadeyle yapılır: aynı anda gelen yüz deneme de sınırı aşamaz. Kilitli hesapta hiçbir satır güncellenmez.
    /// Süresi dolmuş bir kilitten sonra sayım baştan başlar.
    /// </summary>
    private static async Task<bool> CountAttemptAsync(AppDbContext db, Guid accountId, DateTimeOffset now, ShopAuthOptions options, CancellationToken cancellationToken)
    {
        var maxFailures = options.MaxFailedLogins;
        var lockedUntil = now.AddSeconds(options.LockoutSeconds);

        return await db.Set<ShopAccount>()
            .Where(candidate => candidate.Id == accountId && (candidate.LockedUntil == null || candidate.LockedUntil <= now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.FailedLogins, candidate => candidate.LockedUntil != null ? 1 : candidate.FailedLogins + 1)
                    .SetProperty(
                        candidate => candidate.LockedUntil,
                        candidate => (candidate.LockedUntil != null ? 1 : candidate.FailedLogins + 1) >= maxFailures ? lockedUntil : (DateTimeOffset?)null),
                cancellationToken) == 1;
    }

    /// <summary>
    /// Yeni bir kod üretir (eskisinin yerine geçer) ve e-postayla gönderir. Pencerenin gönderim hakkı dolduysa kod
    /// üretilmez ve e-posta gönderilmez. Gönderim hatası isteği düşürmez.
    /// </summary>
    private static async Task SendCodeAsync(
        AppDbContext db,
        ShopAccount account,
        AccountCodePurpose purpose,
        DateTimeOffset now,
        IAccountMailer mailer,
        ShopAuthOptions options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken,
        bool background = false)
    {
        var code = new string(RandomNumberGenerator.GetItems<char>(CodeAlphabet, CodeLength));
        var hash = Hasher.HashPassword(account, code);
        var expiresAt = now.AddMinutes(options.CodeMinutes);
        var windowEnded = now.AddSeconds(-options.CodeWindowSeconds);
        var maxIssued = options.MaxCodesPerWindow;
        var codes = db.Set<ShopAccountCode>().Where(candidate => candidate.AccountId == account.Id && candidate.Purpose == purpose);

        // Tek ifade: pencere dolduysa sayım sıfırlanır, dolmadıysa gönderim sayılır.
        var renewed = await codes
            .Where(candidate => candidate.WindowStart <= windowEnded || candidate.Issued < maxIssued)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.CodeHash, hash)
                    .SetProperty(candidate => candidate.ExpiresAt, expiresAt)
                    .SetProperty(candidate => candidate.Issued, candidate => candidate.WindowStart <= windowEnded ? 1 : candidate.Issued + 1)

                    .SetProperty(candidate => candidate.WindowStart, candidate => candidate.WindowStart <= windowEnded ? now : candidate.WindowStart),
                cancellationToken) == 1;

        if (!renewed)
        {
            if (await codes.AnyAsync(cancellationToken))
            {
                // Bu pencerenin gönderim hakkı doldu.
                return;
            }

            var record = new ShopAccountCode
            {
                Id = Guid.CreateVersion7(),
                AccountId = account.Id,
                Purpose = purpose,
                CodeHash = hash,
                ExpiresAt = expiresAt,
                WindowStart = now,
                Issued = 1,
            };
            db.Add(record);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (HaldusDb.IsUniqueViolation(exception, AuthModel.CodeIndex))
            {
                // Aynı anda gelen ikinci istek: öteki isteğin kodu geçerlidir, bu gönderilmez.
                db.Entry(record).State = EntityState.Detached;
                return;
            }
        }

        var logger = loggers.CreateLogger(typeof(AuthEndpoints));

        if (background)
        {
            // İstek bittikten sonra da sürer; bu yüzden isteğin iptal belirtecine değil kendi süre sınırına bağlanır
            // (yanıt vermeyen bir posta sunucusu görev ve bağlantı biriktirmesin). Dil, çalışma bağlamıyla taşınır.
            // Süreç bu sırada kapanırsa e-posta gitmez; kullanıcı yeni bir kod ister.
            _ = Task.Run(
                async () =>
                {
                    using var timeout = new CancellationTokenSource(MailTimeout);

                    try
                    {
                        await DeliverAsync(mailer, account.Email, purpose, code, logger, timeout.Token);
                    }
                    catch (OperationCanceledException exception)
                    {
                        LogMailFailed(logger, exception, purpose);
                    }
                },
                CancellationToken.None);
        }
        else
        {
            await DeliverAsync(mailer, account.Email, purpose, code, logger, cancellationToken);
        }
    }

    private static async Task DeliverAsync(
        IAccountMailer mailer,
        string email,
        AccountCodePurpose purpose,
        string code,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await mailer.SendCodeAsync(email, purpose, code, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Yanıt, e-postanın gönderilip gönderilemediğini söylememelidir (parola sıfırlamada hesabın varlığını söylerdi).
            LogMailFailed(logger, exception, purpose);
        }
    }

    /// <summary>
    /// Kodu sınar ve doğruysa tüketir; kod bir kez kullanılır. Hesap veya geçerli bir kod yoksa da bir doğrulama
    /// yapılır, böylece yanıtın süresi hangisi olduğunu söylemez.
    /// </summary>
    private static async Task<bool> ConsumeCodeAsync(
        AppDbContext db,
        ShopAccount? account,
        AccountCodePurpose purpose,
        string code,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var record = account is null
            ? null
            : await db.Set<ShopAccountCode>().AsNoTracking().FirstOrDefaultAsync(
                candidate => candidate.AccountId == account.Id && candidate.Purpose == purpose && candidate.ExpiresAt > now,
                cancellationToken);

        var matches = Hasher.VerifyHashedPassword(account ?? new ShopAccount(), record?.CodeHash ?? DummyHash, NormalizeCode(code)) != PasswordVerificationResult.Failed;

        // Silme, aynı kodu aynı anda sunan iki istekten yalnızca birini geçirir.
        return record is not null
            && matches
            && await db.Set<ShopAccountCode>().Where(candidate => candidate.Id == record.Id).ExecuteDeleteAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Kodu yazıldığı biçimden bağımsız kılar: boşluk ve tire atılır, harfler büyütülür ve alfabede olmayan, birbirine
    /// benzeyen harfler karşılıklarına çevrilir (O → 0, I ve L → 1).
    /// </summary>
    private static string NormalizeCode(string code) =>
        string.Concat(code.Where(character => character is not (' ' or '-')).Select(character => char.ToUpperInvariant(character) switch
        {
            'O' => '0',
            'I' or 'L' => '1',
            var other => other,
        }));
    private static async Task<bool> RotateAsync(IQueryable<ShopSession> sessions, DateTimeOffset now, CancellationToken cancellationToken) =>
        await sessions.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(session => session.RefreshVersion, session => session.RefreshVersion + 1)
                .SetProperty(session => session.RotatedAt, now),
            cancellationToken) == 1;

    /// <summary>İptal edilmemiş ve süresi dolmamış oturumlar.</summary>
    internal static IQueryable<ShopSession> Live(IQueryable<ShopSession> sessions, DateTimeOffset now) =>
        sessions.Where(session => session.RevokedAt == null && session.ExpiresAt > now);

    internal static Task<int> RevokeAsync(IQueryable<ShopSession> sessions, DateTimeOffset now, CancellationToken cancellationToken) =>
        sessions
            .Where(session => session.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAt, now), cancellationToken);

    private static Guid? AccountId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static async Task<ShopAccount?> FindAccountAsync(ClaimsPrincipal user, AppDbContext db, CancellationToken cancellationToken) =>
        AccountId(user) is { } id
            ? await db.Set<ShopAccount>().AsNoTracking().FirstOrDefaultAsync(account => account.Id == id, cancellationToken)
            : null;

    private static ShopSession NewSession(ShopAccount account, DateTimeOffset now, ShopAuthOptions options) =>
        new() { Id = Guid.CreateVersion7(), AccountId = account.Id, CreatedAt = now, ExpiresAt = now.AddDays(options.SessionDays) };

    /// <summary>Haldus kullanıcıyı ve kiracıyı bu claim'lerden okur (<c>HaldusOptions</c>).</summary>
    private static IResult SignIn(ShopAccount account, ShopSession session, ShopAuthOptions options)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
                new Claim(ClaimTypes.Email, account.Email),
                new Claim("tenant_id", account.TenantId.ToString()),
                new Claim(SessionClaim, session.Id.ToString()),
                new Claim(RefreshClaim, session.RefreshVersion.ToString(CultureInfo.InvariantCulture)),
                new Claim(VerifiedClaim, account.EmailVerifiedAt is null ? "false" : "true"),
            ],
            BearerTokenDefaults.AuthenticationScheme);

        // Rol token'a yazılır; personel listesinden çıkarılan bir hesap, erişim token'ı yenilenince rolünü kaybeder.
        // Yalnızca e-postası doğrulanmış hesaba verilir (ShopAuthOptions.IsStaff).
        if (options.IsStaff(account))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, SharedWrites.StaffRole));
        }

        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: BearerTokenDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Parolası olan hesaplar. Bir kimlik sağlayıcıyla açılmış hesabın parolası yoktur ve parola uçlarından (giriş,
    /// unutma, sıfırlama) bir parola da edinemez: edinseydi, adresinin posta kutusunu bir kez okuyabilen biri hesaba
    /// kalıcı bir parola koyar ve sağlayıcının denetimlerini (iki adımlı doğrulama, hesabın kapatılması) aşardı. Bu
    /// uçlar öyle bir hesabı, hiç yokmuş gibi yanıtlar.
    /// </summary>
    private static IQueryable<ShopAccount> PasswordAccounts(AppDbContext db) =>
        db.Set<ShopAccount>().AsNoTracking().Where(account => account.ExternalIssuer == null);

    // Token doğrulayıcının kabul ettiği saat farkıyla aynıdır (TokenValidationParameters.DefaultClockSkew).
    internal static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Çerezin oturumunu değiştiren, giriş istemeyen uç: çerezle gelen istek başlığı burada da taşımalıdır.</summary>
    internal sealed class ChangesSession;

    private static IResult ManagedByProvider(IStringLocalizer<ShopMessages> messages) =>
        Results.Problem(detail: messages["ManagedByProvider"], statusCode: StatusCodes.Status409Conflict);

    private static IResult EmailTaken(IStringLocalizer<ShopMessages> messages) =>
        Results.Problem(detail: messages["EmailTaken"], statusCode: StatusCodes.Status409Conflict);

    private static IResult InvalidCredentials(IStringLocalizer<ShopMessages> messages) =>
        Results.Problem(detail: messages["InvalidCredentials"], statusCode: StatusCodes.Status401Unauthorized);

    private static IResult SessionExpired(IStringLocalizer<ShopMessages> messages) =>
        Results.Problem(detail: messages["SessionExpired"], statusCode: StatusCodes.Status401Unauthorized);

    private static IResult InvalidCode(IStringLocalizer<ShopMessages> messages) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = [messages["InvalidCode"]] });

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    [LoggerMessage(Level = LogLevel.Error, Message = "The {Purpose} message could not be sent.")]
    private static partial void LogMailFailed(ILogger logger, Exception exception, AccountCodePurpose purpose);
}
