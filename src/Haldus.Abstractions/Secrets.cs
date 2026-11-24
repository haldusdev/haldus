using System.Security.Cryptography;
using System.Text;

namespace Haldus;

/// <summary>
/// Kayıt, yalnızca bir kez gösterilen bir sır taşır (<c>.SecretOnce()</c>). Sırrın kendisi saklanmaz; özeti ve,
/// listede tanınabilsin diye, son karakterleri saklanır.
/// </summary>
public interface IHasSecret
{
    string SecretHash { get; set; }

    string SecretHint { get; set; }
}

/// <summary>Sır döndürüldükten sonra eskisi bir süre daha geçerli kalır (<c>.Rotatable(overlap: ...)</c>).</summary>
public interface IHasRotatingSecret : IHasSecret
{
    string? PreviousSecretHash { get; set; }

    DateTimeOffset? PreviousSecretExpiresAt { get; set; }
}

/// <summary>Sır, sunucunun kendisi kullanabilsin diye şifrelenmiş olarak da saklanır (<c>recoverable: true</c>).</summary>
public interface IHasRecoverableSecret : IHasSecret
{
    string SecretCipher { get; set; }
}

/// <summary>Kayıt, oturumu olmayan birinin ona ulaşmasını sağlayan gizli bir token taşır (<c>.TokenAccess()</c>).</summary>
public interface IHasAccessToken
{
    string TokenHash { get; set; }
}

/// <summary>Sırların ve tokenların üretilmesi ve özetlenmesi.</summary>
public static class HaldusSecrets
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>Gövdenin uzunluğu: 62 karakterlik alfabeden 43 karakter, 256 bitten biraz fazla rastgelelik taşır.</summary>
    public const int BodyLength = 43;

    /// <summary>
    /// <c>{önek}_{gövde}</c> biçiminde yeni bir sır üretir. Gövde işletim sisteminin kriptografik rastgele
    /// kaynağından gelir; önek sırrın türünü (ve sızdığında nerede aranacağını) gösterir.
    /// </summary>
    public static string Generate(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        return $"{prefix}_{RandomNumberGenerator.GetString(Alphabet, BodyLength)}";
    }

    /// <summary>
    /// Sırrın saklanan özeti (SHA-256). Sır yüksek entropili ve rastgele olduğu için parola özetlerindeki gibi yavaş
    /// bir fonksiyona gerek yoktur; özet, sırrı veritabanında aramak için de kullanılır.
    /// </summary>
    public static string Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    /// <summary>Sırrın, listede hangisi olduğunu ayırt etmeye yeten son dört karakteri.</summary>
    public static string Hint(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return secret.Length <= 4 ? secret : secret[^4..];
    }
}
