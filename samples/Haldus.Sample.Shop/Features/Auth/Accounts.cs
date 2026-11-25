using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>E-postanın, gönderilen kodla doğrulandığı an; doğrulanmadıysa boştur.</summary>
    public DateTimeOffset? EmailVerifiedAt { get; set; }

    /// <summary>Art arda başarısız giriş denemeleri; başarılı giriş ve parola sıfırlama sıfırlar.</summary>
    public int FailedLogins { get; set; }

    /// <summary>Hesap bu ana kadar kilitlidir: doğru parola da kabul edilmez.</summary>
    public DateTimeOffset? LockedUntil { get; set; }
}

/// <summary>
/// Bir giriş: token'lar bu kayda bağlıdır (<c>sid</c> claim'i). Çıkış kaydı iptal eder; iptal edilmiş bir oturumun
/// erişim token'ı da yenileme token'ı da artık kabul edilmez.
/// </summary>
public sealed class ShopSession
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Oturumun en geç biteceği an; yenilemeler bunu uzatmaz.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Geçerli yenileme token'ının sırası. Her yenilemede artar; eski bir yenileme token'ı yeniden sunulursa (çalınmış
    /// olabilir) oturum iptal edilir.
    /// </summary>
    public int RefreshVersion { get; set; }
}

public enum AccountCodePurpose
{
    EmailVerification,
    PasswordReset,
}

/// <summary>
/// E-postayla gönderilen tek kullanımlık kod. Kodun kendisi saklanmaz, özeti saklanır; hesap ve amaç başına tek kod
/// vardır (yenisi eskisinin yerine geçer). Deneme ve gönderim sayıları koda değil pencereye aittir: yeni bir kod
/// istemek deneme hakkını yenilemez, yoksa altı haneli kod "yeni kod iste, beş kez dene" döngüsüyle bulunurdu.
/// </summary>
public sealed class ShopAccountCode
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public AccountCodePurpose Purpose { get; set; }

    public string CodeHash { get; set; } = "";

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Sayımların başladığı an; pencere dolunca sayımlar sıfırlanır.</summary>
    public DateTimeOffset WindowStart { get; set; }

    /// <summary>Bu pencerede gönderilen kod sayısı.</summary>
    public int Issued { get; set; }

    /// <summary>Bu pencerede yapılan deneme sayısı (bütün kodlar için).</summary>
    public int Attempts { get; set; }
}

/// <summary>Kimlik doğrulamanın ayarları (<c>Shop:Auth</c>).</summary>
public sealed class ShopAuthOptions
{
    /// <summary>Bu kadar art arda başarısız girişten sonra hesap kilitlenir.</summary>
    public int MaxFailedLogins { get; set; } = 5;

    public int LockoutSeconds { get; set; } = 900;

    /// <summary>E-postayla gönderilen kodun ömrü.</summary>
    public int CodeMinutes { get; set; } = 15;

    /// <summary>Bir hesabın, bir amaç için, bir pencerede en çok kaç kod deneyebileceği.</summary>
    public int MaxCodeAttempts { get; set; } = 5;

    /// <summary>Bir hesaba, bir amaç için, bir pencerede en çok kaç kod gönderileceği.</summary>
    public int MaxCodesPerWindow { get; set; } = 3;

    /// <summary>Kod deneme ve gönderim sayılarının sıfırlandığı aralık.</summary>
    public int CodeWindowSeconds { get; set; } = 3600;

    /// <summary>Bir oturumun en uzun ömrü; dolunca yeniden giriş gerekir.</summary>
    public int SessionDays { get; set; } = 30;

    /// <summary>Giriş, kayıt ve kod uçlarına bir adresten dakikada en çok kaç istek gelebileceği.</summary>
    public int RequestsPerMinute { get; set; } = 30;

    /// <summary>Açıksa e-postası doğrulanmamış hesap yalnızca kimlik uçlarını kullanabilir.</summary>
    public bool RequireVerifiedEmail { get; set; }
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

public sealed record ForgotPasswordRequest
{
    [Required]
    [MaxLength(256)]
    public string Email { get; init; } = "";
}

public sealed record ResetPasswordRequest
{
    [Required]
    [MaxLength(256)]
    public string Email { get; init; } = "";

    [Required]
    [MaxLength(16)]
    public string Code { get; init; } = "";

    [Required]
    [MinLength(8)]
    [MaxLength(128)]
    public string NewPassword { get; init; } = "";
}

public sealed record ChangePasswordRequest
{
    [Required]
    [MaxLength(128)]
    public string CurrentPassword { get; init; } = "";

    [Required]
    [MinLength(8)]
    [MaxLength(128)]
    public string NewPassword { get; init; } = "";
}

public sealed record VerifyEmailRequest
{
    [Required]
    [MaxLength(16)]
    public string Code { get; init; } = "";
}

public sealed record AccountInfo(Guid Id, string Email, Guid TenantId, bool EmailVerified);

/// <summary>Hesap tablolarının modeli; <see cref="AppDbContext"/> çağırır.</summary>
public static class AuthModel
{
    internal const string EmailIndex = "UX_ShopAccounts_Email";
    internal const string CodeIndex = "UX_ShopAccountCodes_Account_Purpose";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShopAccount>(entity =>
        {
            entity.ToTable("ShopAccounts");
            entity.Property(account => account.Id).ValueGeneratedNever();
            entity.Property(account => account.Email).HasMaxLength(256);
            entity.Property(account => account.PasswordHash).HasMaxLength(512);
            entity.HasIndex(account => account.Email).IsUnique().HasDatabaseName(EmailIndex);
        });

        modelBuilder.Entity<ShopSession>(entity =>
        {
            entity.ToTable("ShopSessions");
            entity.Property(session => session.Id).ValueGeneratedNever();
            entity.HasIndex(session => session.AccountId);
            entity.HasOne<ShopAccount>().WithMany().HasForeignKey(session => session.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ShopAccountCode>(entity =>
        {
            entity.ToTable("ShopAccountCodes");
            entity.Property(code => code.Id).ValueGeneratedNever();
            entity.Property(code => code.Purpose).HasConversion<string>().HasMaxLength(32);
            entity.Property(code => code.CodeHash).HasMaxLength(512);
            entity.HasIndex(code => new { code.AccountId, code.Purpose }).IsUnique().HasDatabaseName(CodeIndex);
            entity.HasOne<ShopAccount>().WithMany().HasForeignKey(code => code.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
