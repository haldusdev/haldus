using Microsoft.EntityFrameworkCore;

namespace Haldus;

/// <summary>
/// Üretilen endpoint'lerin bir entity için kullanacağı bağlamı verir. Üretilen kod somut
/// <see cref="DbContext"/> tipini bilmez; ortak ve modül başına bağlam modları bu sayede aynı kodu kullanır.
/// </summary>
public interface IHaldusDbContextProvider
{
    DbContext For<TEntity>() where TEntity : class;
}

public sealed class SingleDbContextProvider<TContext>(TContext context) : IHaldusDbContextProvider
    where TContext : DbContext
{
    public DbContext For<TEntity>() where TEntity : class => context;
}
