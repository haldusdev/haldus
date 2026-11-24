using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus;

/// <summary>
/// Geri okunabilen sırların (<c>.SecretOnce(recoverable: true)</c>) şifrelenmesi. ASP.NET Data Protection kullanılır;
/// anahtarların kalıcı ve sunucular arasında ortak olması uygulamanın sorumluluğundadır, yoksa yeniden başlatılan
/// ya da başka bir sunucuda çalışan uygulama sırları açamaz.
/// </summary>
public static class HaldusSecretProtection
{
    /// <summary>Sırrı, veritabanına yazılabilecek biçimde şifreler.</summary>
    public static string Protect<TEntity>(IServiceProvider services, string secret)
        where TEntity : class, IHasRecoverableSecret
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(secret);

        return Protector<TEntity>(services).Protect(secret);
    }

    /// <summary>Kaydın sırrını açar; sunucunun onu kullanması (ör. bir gönderiyi imzalaması) için.</summary>
    public static string Reveal<TEntity>(IServiceProvider services, TEntity entity)
        where TEntity : class, IHasRecoverableSecret
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(entity);

        return Protector<TEntity>(services).Unprotect(entity.SecretCipher);
    }

    // Amaç dizesi kaynağı ayırır: bir kaynağın şifreli sırrı başka bir kaynağın alanına kopyalanarak açılamaz.
    private static IDataProtector Protector<TEntity>(IServiceProvider services) =>
        services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Haldus.Secret", typeof(TEntity).FullName ?? typeof(TEntity).Name);
}

/// <summary>
/// Token ile erişilen uçların işareti. Bu uçlara oturumu olmayan biri de gelebilir; kiracıyı ve kullanıcıyı zorunlu
/// kılan filtreler onları atlar, çünkü isteği yapanı kayıt üzerindeki token tanıtır.
/// </summary>
public sealed class HaldusTokenAccess
{
    /// <summary>Token'ın taşındığı başlık. Adrese yazılmaz; böylece erişim günlüklerine ve tarayıcı geçmişine girmez.</summary>
    public const string Header = "X-Access-Token";

    public static HaldusTokenAccess Instance { get; } = new();

    /// <summary>İstekteki token'ın özeti; başlık yoksa veya boşsa <c>null</c>.</summary>
    public static string? ReadHash(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var token = httpContext.Request.Headers[Header].ToString();
        return token.Length is 0 or > 512 ? null : HaldusSecrets.Hash(token);
    }

    internal static bool Applies(HttpContext httpContext) =>
        httpContext.GetEndpoint()?.Metadata.GetMetadata<HaldusTokenAccess>() is not null;
}
