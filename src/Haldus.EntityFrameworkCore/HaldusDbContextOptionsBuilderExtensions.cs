using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Haldus;

public static class HaldusDbContextOptionsBuilderExtensions
{
    private static readonly HaldusSaveChangesInterceptor Interceptor = new();
    private static readonly HaldusFileTransactionInterceptor FileTransactions = new();

    /// <summary>
    /// Haldus'un ürettiği entity'leri ve yapılandırmalarını bu bağlamın modeline ekler; trait alanlarını
    /// dolduran interceptor'ı bağlar.
    /// </summary>
    public static DbContextOptionsBuilder UseHaldus(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        return optionsBuilder
            .ReplaceService<IModelCustomizer, HaldusModelCustomizer>()
            .AddInterceptors(Interceptor, FileTransactions);
    }
}
