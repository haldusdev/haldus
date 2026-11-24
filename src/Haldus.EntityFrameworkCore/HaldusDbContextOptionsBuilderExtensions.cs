using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Haldus;

public static class HaldusDbContextOptionsBuilderExtensions
{
    /// <summary>Haldus'un ürettiği entity'leri ve yapılandırmalarını bu bağlamın modeline ekler.</summary>
    public static DbContextOptionsBuilder UseHaldus(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        return optionsBuilder.ReplaceService<IModelCustomizer, HaldusModelCustomizer>();
    }
}
