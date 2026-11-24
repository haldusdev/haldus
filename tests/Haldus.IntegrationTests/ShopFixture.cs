using Haldus.Sample.Shop;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Haldus.IntegrationTests;

/// <summary>Örnek uygulamayı gerçek bir PostgreSQL konteynerine karşı ayağa kaldırır.</summary>
public sealed class ShopFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();

    private WebApplicationFactory<Program>? _factory;

    public HttpClient CreateClient() =>
        (_factory ?? throw new InvalidOperationException("The fixture has not been initialized.")).CreateClient();

    /// <summary>
    /// Aynı veritabanına bağlanan, servisleri değiştirilmiş ikinci bir uygulama (ör. başka bir iş altyapısıyla).
    /// </summary>
    public HttpClient CreateClient(Action<IServiceCollection> configureServices) => CreateFactory(configureServices).CreateClient();

    public WebApplicationFactory<Program> CreateFactory(Action<IServiceCollection> configureServices) =>
        (_factory ?? throw new InvalidOperationException("The fixture has not been initialized."))
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(configureServices));

    /// <summary>Uygulamanın servis sağlayıcısı; bakım gibi, bir isteğe bağlı olmayan işleri çalıştırmak için.</summary>
    public IServiceProvider Services =>
        (_factory ?? throw new InvalidOperationException("The fixture has not been initialized.")).Services;

    /// <summary>İstemcinin yazamadığı verileri (ör. salt-okunur kaynaklar) doğrudan veritabanına koymak için.</summary>
    public async Task WithDbContextAsync(Func<AppDbContext, Task> action)
    {
        var factory = _factory ?? throw new InvalidOperationException("The fixture has not been initialized.");

        await using var scope = factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Default", _database.GetConnectionString()));

        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
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
public sealed class ShopCollection : ICollectionFixture<ShopFixture>
{
    public const string Name = "Shop";
}
