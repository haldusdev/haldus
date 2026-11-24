using Haldus.Sample.Modular.Catalog;
using Haldus.Sample.Modular.Host;
using Haldus.Sample.Modular.Sales;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Haldus.IntegrationTests;

/// <summary>
/// Modüler örnek uygulamayı gerçek bir PostgreSQL konteynerine karşı ayağa kaldırır: bir ortak bağlam ve kendi
/// bağlamı olan iki modül aynı veritabanını, ayrı şemalarla paylaşır.
/// </summary>
public sealed class ModularFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();

    private WebApplicationFactory<ModularApp>? _factory;

    public IServiceProvider Services =>
        (_factory ?? throw new InvalidOperationException("The fixture has not been initialized.")).Services;

    public HttpClient CreateClient() =>
        (_factory ?? throw new InvalidOperationException("The fixture has not been initialized.")).CreateClient();

    public async Task WithScopeAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();

        _factory = new WebApplicationFactory<ModularApp>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Default", _database.GetConnectionString()));

        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        await services.GetRequiredService<HostDbContext>().Database.EnsureCreatedAsync();

        // Veritabanı artık var; diğer bağlamlar yalnızca kendi şemalarını ve tablolarını ekler.
        await services.GetRequiredService<CatalogDbContext>().GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        await services.GetRequiredService<SalesDbContext>().GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _database.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ModularCollection : ICollectionFixture<ModularFixture>
{
    public const string Name = "Modular";
}