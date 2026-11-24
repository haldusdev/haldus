using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Haldus;

internal sealed class HaldusModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        // Haldus katkıları önce uygulanır ki kullanıcının OnModelCreating'i bunları ezebilsin.
        HaldusModelRegistry.Apply(modelBuilder);
        base.Customize(modelBuilder, context);
    }
}
