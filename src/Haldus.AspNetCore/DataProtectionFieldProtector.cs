using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;

namespace Haldus;

/// <summary>
/// Şifreli saklanan alanların (<c>.Encrypted(...)</c>) ASP.NET Data Protection ile korunması. Anahtarların kalıcı ve
/// sunucular arasında ortak olması uygulamanın sorumluluğundadır.
/// </summary>
internal sealed class DataProtectionFieldProtector(IDataProtectionProvider provider) : IHaldusFieldProtector
{
    private readonly ConcurrentDictionary<string, IDataProtector> _protectors = new(StringComparer.Ordinal);

    public string Protect(string purpose, string value) => For(purpose).Protect(value);

    public string Unprotect(string purpose, string value) => For(purpose).Unprotect(value);

    private IDataProtector For(string purpose) =>
        _protectors.GetOrAdd(purpose, static (key, provider) => provider.CreateProtector("Haldus.Field", key), provider);
}
