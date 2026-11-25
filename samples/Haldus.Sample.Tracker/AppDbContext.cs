using Microsoft.EntityFrameworkCore;

namespace Tracker;

// Haldus, eşlenen her entity'nin DbSet özelliğini bu sınıfın öteki yarısına ekler.
public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
