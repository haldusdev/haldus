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

    /// <summary>
    /// Sıranın en son ilerlediği an. Hemen ardından bir önceki token'ı sunan istek (aynı anda yenileyen ikinci sekme)
    /// çalınmış sayılmaz: oturum kapatılmaz, ama o isteğe yeni token da verilmez.
    /// </summary>
    public DateTimeOffset? RotatedAt { get; set; }
}

public enum AccountCodePurpose
{
    EmailVerification,
    PasswordReset,
}

/// <summary>
/// E-postayla gönderilen tek kullanımlık kod. Kodun kendisi saklanmaz, özeti saklanır; hesap ve amaç başına tek kod
/// vardır (yenisi eskisinin yerine geçer). Kod sekiz karakterdir (40 bit): denenerek bulunamayacak kadar uzun olduğu
/// için hesap başına bir deneme sınırı yoktur. Öyle bir sınır, başkasının yanlış kodlar göndererek hesabın parola
/// sıfırlamasını engellemesine izin verirdi; kısa (altı haneli) kod ise sınırsız denemeyle bulunurdu.
/// </summary>
public sealed class ShopAccountCode
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public AccountCodePurpose Purpose { get; set; }

    public string CodeHash { get; set; } = "";

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Gönderim sayımının başladığı an; pencere dolunca sayım sıfırlanır.</summary>
    public DateTimeOffset WindowStart { get; set; }

    /// <summary>Bu pencerede gönderilen kod sayısı; adrese e-posta yağdırılmasını sınırlar.</summary>
    public int Issued { get; set; }
}

/// <summary>Kimlik doğrulamanın ayarları (<c>Shop:Auth</c>).</summary>
public sealed class ShopAuthOptions
{
    /// <summary>Bu kadar art arda başarısız girişten sonra hesap kilitlenir.</summary>
    public int MaxFailedLogins { get; set; } = 5;

    public int LockoutSeconds { get; set; } = 900;

    /// <summary>E-postayla gönderilen kodun ömrü.</summary>
    public int CodeMinutes { get; set; } = 15;

    /// <summary>Bir hesaba, bir amaç için, bir pencerede en çok kaç kod gönderileceği.</summary>
    public int MaxCodesPerWindow { get; set; } = 3;

    /// <summary>Kod gönderim sayısının sıfırlandığı aralık.</summary>
    public int CodeWindowSeconds { get; set; } = 3600;

    /// <summary>Yenilemeden sonra bir önceki yenileme token'ının oturumu kapatmadığı süre (yeni token yine verilmez).</summary>
    public int RefreshGraceSeconds { get; set; } = 30;

    /// <summary>Bir oturumun en uzun ömrü; dolunca yeniden giriş gerekir.</summary>
    public int SessionDays { get; set; } = 30;

    /// <summary>Giriş, kayıt ve kod uçlarına bir adresten dakikada en çok kaç istek gelebileceği.</summary>
    public int RequestsPerMinute { get; set; } = 30;

    /// <summary>Açıksa e-postası doğrulanmamış hesap yalnızca kimlik uçlarını kullanabilir.</summary>
    public bool RequireVerifiedEmail { get; set; }

    /// <summary>
    /// Personel hesaplarının e-postaları. Kimseye ait olmayan, ortak kaynakları (ürünler, kategoriler…) yalnızca
    /// personel yazar; kayıt herkese açık olduğu için bu liste boşken onları kimse yazamaz. Rol, hesap e-postasını
    /// doğruladıktan sonra geçerli olur.
    /// </summary>
    public IList<string> Staff { get; } = [];

    /// <summary>
    /// Hesap personel mi. E-posta listede olsa da doğrulanana kadar rol verilmez: kayıt herkese açıktır ve adres
    /// kullanıcının yazdığı bir metindir; doğrulanmadan rol verilseydi, personel adresiyle ilk kaydolan kişi (adresin
    /// sahibi olmasa da) personel olurdu.
    /// </summary>
    public bool IsStaff(ShopAccount account) =>
        account.EmailVerifiedAt is not null
        && Staff.Any(candidate => string.Equals(candidate?.Trim(), account.Email, StringComparison.OrdinalIgnoreCase));
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

public sealed record LogoutRequest
{
    internal const int MaxTokenLength = 4096;

    /// <summary>Kapatılacak oturumun yenileme token'ı; erişim token'ı artık geçmiyorsa oturumu bu tanıtır.</summary>
    public string? RefreshToken { get; init; }
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
    [MaxLength(32)]
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
    [MaxLength(32)]
    public string Code { get; init; } = "";
}

public sealed record AccountInfo(Guid Id, string Email, Guid TenantId, bool EmailVerified, bool IsStaff);

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
