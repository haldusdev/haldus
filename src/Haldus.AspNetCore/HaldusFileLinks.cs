using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus;

/// <summary>Bir dosyanın içeriğine, oturumu olmayan birinin de açabildiği süreli bağlantı.</summary>
/// <param name="Url">Bağlantının adresi; sunucunun köküne görelidir.</param>
public sealed record FileLink(string Url, DateTimeOffset ExpiresAt);

/// <summary>
/// Dosyaların süreli indirme bağlantıları (<c>MapFile().SignedLinks()</c>). Bağlantı, kaydın kimliğini ve bağlantı
/// üretildiği andaki içeriğin özetini taşıyan, ASP.NET Data Protection ile imzalanıp şifrelenmiş bir değerdir:
/// tahmin edilemez, süresi dolunca geçersiz olur ve içerik değişince artık hiçbir şeyi açmaz. Anahtarların kalıcı ve
/// sunucular arasında ortak olması uygulamanın sorumluluğundadır.
/// </summary>
public static class HaldusFileLinks
{
    /// <summary>Süre verilmezse bağlantının ömrü, saniye olarak.</summary>
    public const int DefaultSeconds = 300;

    /// <summary>Bir bağlantının en uzun ömrü, saniye olarak.</summary>
    public const int MaxSeconds = 86_400;

    private const char Separator = '\n';

    /// <summary>
    /// Kaydın şimdiki içeriği için bir bağlantı üretir. Bağlantının adresi, isteğin geldiği adresten kurulur
    /// (<c>…/{id}/content/link</c> → <c>…/shared-content/{token}</c>); böylece uygulama uçlarını bir önek altında
    /// eşlediyse bağlantı da o önekin altındadır.
    /// </summary>
    public static FileLink Create<TEntity>(HttpContext httpContext, string id, string checksum, int? expiresInSeconds)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(checksum);

        var path = httpContext.Request.Path.Value ?? string.Empty;
        var suffix = path.LastIndexOf($"/{id}/content/link", StringComparison.OrdinalIgnoreCase);
        var collection = suffix >= 0 ? path.Substring(0, suffix) : path;

        var lifetime = TimeSpan.FromSeconds(Math.Clamp(expiresInSeconds ?? DefaultSeconds, 1, MaxSeconds));
        var expiresAt = HaldusTime.UtcNow(httpContext.RequestServices.GetRequiredService<TimeProvider>()) + lifetime;
        var token = Protector<TEntity>(httpContext).Protect(id + Separator + checksum, expiresAt);

        return new FileLink($"{httpContext.Request.PathBase}{collection.TrimEnd('/')}/shared-content/{token}", expiresAt);
    }

    /// <summary>Bağlantıyı doğrular: geçerliyse ve süresi dolmadıysa taşıdığı kimliği ve içerik özetini verir.</summary>
    public static bool TryRead<TEntity>(HttpContext httpContext, string token, out string id, out string checksum)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        id = string.Empty;
        checksum = string.Empty;

        // Çok uzun bir değer bir bağlantı olamaz; çözmeye çalışmak yalnızca iş yükü olurdu.
        if (string.IsNullOrEmpty(token) || token.Length > 2048)
        {
            return false;
        }

        try
        {
            var parts = Protector<TEntity>(httpContext).Unprotect(token).Split(Separator);

            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                return false;
            }

            (id, checksum) = (parts[0], parts[1]);
            return true;
        }
        catch (CryptographicException)
        {
            // Bozuk, başka bir kaynak için üretilmiş veya süresi dolmuş.
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Amaç dizesi kaynağı ayırır: bir kaynağın bağlantısı başka bir kaynağın içeriğini açamaz.
    private static ITimeLimitedDataProtector Protector<TEntity>(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Haldus.FileLink", typeof(TEntity).FullName ?? typeof(TEntity).Name)
            .ToTimeLimitedDataProtector();
}