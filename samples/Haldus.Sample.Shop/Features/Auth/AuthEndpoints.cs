using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Haldus.Sample.Shop.Features.Auth;

/// <summary>
/// Örnek mağazanın hesabı. Haldus'un ürettiği bir kaynak değildir: kimlik doğrulama uygulamanın kendi işidir ve
/// Haldus yalnızca sonucunu okur (kullanıcı ve kiracı claim'leri). Her hesap kendi kiracısıyla doğar.
/// </summary>
public sealed class ShopAccount
{
    public Guid Id { get; set; }

    /// <summary>Küçük harfe çevrilmiş e-posta; benzersizdir.</summary>
    public string Email { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public Guid TenantId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record RegisterRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; init; } = "";

    [Required]
    [MinLength(8)]
    [MaxLength(128)]
    public string Password { get; init; } = "";
}

public sealed record LoginRequest
{
    [Required]
    [MaxLength(256)]
    public string Email { get; init; } = "";

    [Required]
    [MaxLength(128)]
    public string Password { get; init; } = "";
}

public sealed record RefreshRequest
{
    [Required]
    [MaxLength(4096)]
    public string RefreshToken { get; init; } = "";
}

public sealed record AccountInfo(Guid Id, string Email, Guid TenantId);

/// <summary>
/// Parola ile giriş ve taşıyıcı (bearer) token. ASP.NET Core'un yerleşik şeması kullanılır
/// (<c>AddBearerToken</c>): token'lar Data Protection ile korunan, sunucunun kendisinin ürettiği değerlerdir; ayrı bir
/// kimlik sağlayıcısı veya paket gerekmez. Gerçek bir uygulama burada kendi kimlik sağlayıcısını (OpenID Connect)
/// kullanır; Haldus açısından fark yoktur, çünkü o yalnızca claim'leri okur.
/// </summary>
public static class AuthEndpoints
{
    // Hesap bulunamadığında da bir parola doğrulaması yapılır; yoksa yanıt süresi e-postanın kayıtlı olup olmadığını söylerdi.
    private static readonly string DummyHash = new PasswordHasher<ShopAccount>().HashPassword(new ShopAccount(), Guid.NewGuid().ToString());

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/register", RegisterAsync)
            .WithName("Register")
            .AllowAnonymous()
            .AddEndpointFilter<HaldusValidationFilter<RegisterRequest>>()
            .Produces<AccessTokenResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/login", LoginAsync)
            .WithName("Login")
            .AllowAnonymous()
            .AddEndpointFilter<HaldusValidationFilter<LoginRequest>>()
            .Produces<AccessTokenResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/refresh", RefreshAsync)
            .WithName("RefreshSession")
            .AllowAnonymous()
            .AddEndpointFilter<HaldusValidationFilter<RefreshRequest>>()
            .Produces<AccessTokenResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapGet("/me", MeAsync)
            .WithName("GetCurrentAccount")
            .RequireAuthorization()
            .Produces<AccountInfo>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Reddedilen bir token sunan isteği 401 ile yanıtlar. Kimlik doğrulama başarısız olunca istek anonim sayılır ve
    /// aynı istek, vardığı kaynağa göre 401 (kullanıcıya ait), 403 (kiracıya ait) veya 200 (herkese açık) alırdı;
    /// istemci oturumunu yenilemesi gerektiğini bunlardan anlayamaz. Token istemeyen kimlik uçları (giriş, yenileme)
    /// bunun dışındadır: süresi dolmuş bir oturum onlara takılmamalıdır.
    /// </summary>
    public static IApplicationBuilder UseRejectedTokenChallenge(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated != true
                && context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is null)
            {
                var messages = context.RequestServices.GetRequiredService<IStringLocalizer<ShopMessages>>();
                context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
                await Results.Problem(detail: messages["SessionExpired"], statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
                return;
            }

            await next(context);
        });

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        AppDbContext db,
        TimeProvider time,
        IStringLocalizer<ShopMessages> messages,
        CancellationToken cancellationToken)
    {
        var email = Normalize(request.Email);

        if (await db.Set<ShopAccount>().AnyAsync(account => account.Email == email, cancellationToken))
        {
            return EmailTaken(messages);
        }

        var account = new ShopAccount
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            TenantId = Guid.CreateVersion7(),
            CreatedAt = HaldusTime.UtcNow(time),
        };
        account.PasswordHash = new PasswordHasher<ShopAccount>().HashPassword(account, request.Password);
        db.Add(account);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (HaldusDb.IsUniqueViolation(exception, EmailIndex))
        {
            // Aynı e-postayla aynı anda gelen iki kayıttan ikincisi.
            return EmailTaken(messages);
        }

        return SignIn(account);
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        AppDbContext db,
        IStringLocalizer<ShopMessages> messages,
        CancellationToken cancellationToken)
    {
        var email = Normalize(request.Email);
        var account = await db.Set<ShopAccount>().FirstOrDefaultAsync(candidate => candidate.Email == email, cancellationToken);
        var hasher = new PasswordHasher<ShopAccount>();
        var verified = hasher.VerifyHashedPassword(account ?? new ShopAccount(), account?.PasswordHash ?? DummyHash, request.Password);

        if (account is null || verified == PasswordVerificationResult.Failed)
        {
            // Hangisinin yanlış olduğu söylenmez.
            return Results.Problem(detail: messages["InvalidCredentials"], statusCode: StatusCodes.Status401Unauthorized);
        }

        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = hasher.HashPassword(account, request.Password);
            await db.SaveChangesAsync(cancellationToken);
        }

        return SignIn(account);
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        AppDbContext db,
        TimeProvider time,
        IOptionsMonitor<BearerTokenOptions> options,
        IStringLocalizer<ShopMessages> messages,
        CancellationToken cancellationToken)
    {
        // Yenileme token'ı da sunucunun ürettiği korunmuş bir değerdir; bozuksa veya süresi dolduysa açılmaz.
        var ticket = options.Get(BearerTokenDefaults.AuthenticationScheme).RefreshTokenProtector.Unprotect(request.RefreshToken);

        if (ticket?.Properties.ExpiresUtc is not { } expiresAt
            || time.GetUtcNow() >= expiresAt
            || !Guid.TryParse(ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            || await db.Set<ShopAccount>().FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken) is not { } account)
        {
            return Results.Problem(detail: messages["SessionExpired"], statusCode: StatusCodes.Status401Unauthorized);
        }

        // Claim'ler hesaptan yeniden kurulur; silinmiş bir hesabın oturumu yenilenmez.
        return SignIn(account);
    }

    private static async Task<IResult> MeAsync(ClaimsPrincipal user, AppDbContext db, CancellationToken cancellationToken) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        && await db.Set<ShopAccount>().AsNoTracking().FirstOrDefaultAsync(account => account.Id == id, cancellationToken) is { } account
            ? Results.Ok(new AccountInfo(account.Id, account.Email, account.TenantId))
            : Results.Problem(statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>Haldus kullanıcıyı ve kiracıyı bu claim'lerden okur (<c>HaldusOptions</c>).</summary>
    private static IResult SignIn(ShopAccount account)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
                new Claim(ClaimTypes.Email, account.Email),
                new Claim("tenant_id", account.TenantId.ToString()),
            ],
            BearerTokenDefaults.AuthenticationScheme);

        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: BearerTokenDefaults.AuthenticationScheme);
    }

    private static IResult EmailTaken(IStringLocalizer<ShopMessages> messages) =>
        Results.Problem(detail: messages["EmailTaken"], statusCode: StatusCodes.Status409Conflict);

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    internal const string EmailIndex = "UX_ShopAccounts_Email";
}