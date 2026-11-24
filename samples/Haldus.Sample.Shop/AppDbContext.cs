using Haldus.Sample.Shop.Features.Auth;
using Microsoft.EntityFrameworkCore;

namespace Haldus.Sample.Shop;

// partial: Haldus, eşlenen her entity için DbSet özelliğini bu sınıfın diğer yarısına ekler.
public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // Haldus'un katkıları bundan önce uygulanır; uygulamanın elle yazdığı entity'ler burada, her zamanki gibi eklenir.
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShopAccount>(entity =>
        {
            entity.ToTable("ShopAccounts");
            entity.Property(account => account.Id).ValueGeneratedNever();
            entity.Property(account => account.Email).HasMaxLength(256);
            entity.Property(account => account.PasswordHash).HasMaxLength(512);
            entity.HasIndex(account => account.Email).IsUnique().HasDatabaseName(AuthEndpoints.EmailIndex);
        });
    }
}