using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Haldus;

/// <summary>
/// Veritabanında şifreli saklanan alanların (<c>.Encrypted(...)</c>) şifrelenmesi ve açılması. Uygulaması
/// <c>AddHaldus</c> ile kaydedilir (ASP.NET Data Protection); uygulama kendi anahtar yönetimini kullanmak isterse bu
/// servisi değiştirir.
/// </summary>
public interface IHaldusFieldProtector
{
    /// <param name="purpose">Kaynağı ve alanı ayıran amaç; bir alanın şifreli değeri başka bir amaçla açılamaz.</param>
    string Protect(string purpose, string value);

    string Unprotect(string purpose, string value);
}

/// <summary>Şifreli alanların model yapılandırması.</summary>
public static class HaldusEncryption
{
    /// <summary>
    /// Alanı bir değer dönüştürücüsüyle şifreler: veritabanına şifreli metin yazılır, entity'de açık metin durur.
    /// Dönüştürücü sorgu izdüşümlerinde de çalışır. Şifreli metin açık metinden uzundur; bu yüzden alanın uzunluk
    /// sınırı (istekte doğrulanmaya devam eder) kolona uygulanmaz.
    /// </summary>
    public static void Configure(PropertyBuilder property, DbContext context, string purpose)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(context);

        // Model bir kez kurulur ve saklanır; koruyucu da uygulama boyunca tektir.
        var protector = ((IInfrastructure<IServiceProvider>)context).Instance.GetService(typeof(IHaldusFieldProtector)) as IHaldusFieldProtector
            ?? context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider
                ?.GetService(typeof(IHaldusFieldProtector)) as IHaldusFieldProtector
            ?? new MissingProtector();

        property.HasConversion(new ValueConverter<string, string>(
            value => protector.Protect(purpose, value),
            value => protector.Unprotect(purpose, value)));

        property.Metadata.SetMaxLength(null);
    }

    /// <summary>
    /// Bağlam Haldus servisleri olmadan kurulduysa (ör. tasarım zamanı araçları) model yine kurulur; hata, şifreli bir
    /// alan gerçekten okunur veya yazılırken verilir.
    /// </summary>
    private sealed class MissingProtector : IHaldusFieldProtector
    {
        private const string Message =
            "An encrypted field needs IHaldusFieldProtector; register the Haldus services with AddHaldus, or register your own protector.";

        public string Protect(string purpose, string value) => throw new InvalidOperationException(Message);

        public string Unprotect(string purpose, string value) => throw new InvalidOperationException(Message);
    }
}
