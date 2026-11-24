using Microsoft.EntityFrameworkCore;

namespace Haldus.Sample.Shop;

// partial: Haldus, eşlenen her entity için DbSet özelliğini bu sınıfın diğer yarısına ekler.
public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
