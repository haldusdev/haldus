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

    private WebApplicationFactory<ShopMessages>? _factory;

    public HttpClient CreateClient() =>
        (_factory ?? throw new InvalidOperationException("The fixture has not been initialized.")).CreateClient();

    /// <summary>
    /// Aynı veritabanına bağlanan, servisleri değiştirilmiş ikinci bir uygulama (ör. başka bir iş altyapısıyla).
    /// </summary>
    public HttpClient CreateClient(Action<IServiceCollection> configureServices) => CreateFactory(configureServices).CreateClient();

    /// <summary>
    /// Aynı veritabanına bağlanan, gerçek kimlik doğrulamayla çalışan ikinci bir uygulama: kimlik başlıklardan değil,
    /// giriş yapılarak alınan token'dan okunur.
    /// </summary>
    public HttpClient CreateAuthenticatedAppClient()
    {
        _authenticatedFactory ??= new WebApplicationFactory<ShopMessages>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Default", _database.GetConnectionString()));

        return _authenticatedFactory.CreateClient();
    }

    private WebApplicationFactory<ShopMessages>? _authenticatedFactory;
    public WebApplicationFactory<ShopMessages> CreateFactory(Action<IServiceCollection> configureServices) =>
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

        // Testler kimliği başlıklarla verir; kimlik doğrulamanın kendisi AuthEndpointTests'te ayrı bir uygulamayla sınanır.
        _factory = new WebApplicationFactory<ShopMessages>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", _database.GetConnectionString())
            .UseSetting("Shop:DemoIdentity", "true"));

        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_authenticatedFactory is not null)
        {
            await _authenticatedFactory.DisposeAsync();
        }

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
