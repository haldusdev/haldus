using System.Collections.Concurrent;
using Haldus.Sample.Shop;
using Haldus.Sample.Shop.Features.Auth;
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
        // Testler tek bir adresten çok sayıda giriş yapar; hız sınırı kendi testinde, ayrı bir uygulamayla sınanır.
        _authenticatedFactory ??= CreateAuthenticatedFactory(("Shop:Auth:RequestsPerMinute", "100000"));
        return _authenticatedFactory.CreateClient();
    }

    /// <summary>
    /// Gerçek kimlik doğrulamayla çalışan, ayarları verilen ayrı bir uygulama (ör. kısa bir hesap kilidiyle). Hesap
    /// e-postaları gönderilmez, <see cref="Mail"/> içinde toplanır. Çağıran atar.
    /// </summary>
    /// <summary>Testlerin PostgreSQL'inin bağlantı dizesi; ayrı bir veritabanı isteyen test adını değiştirerek kullanır.</summary>
    public string ConnectionString => _database.GetConnectionString();

    public WebApplicationFactory<ShopMessages> CreateAuthenticatedFactory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<ShopMessages>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", _database.GetConnectionString());

            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services => services.AddSingleton<IAccountMailer>(Mail));
        });

    /// <summary>Gerçek kimlik doğrulamayla çalışan uygulamaların "gönderdiği" hesap e-postaları.</summary>
    public CapturedMail Mail { get; } = new();

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

/// <summary>E-posta göndermek yerine kodları saklayan gönderici; test, kullanıcının e-postasından okuyacağı kodu buradan alır.</summary>
public sealed class CapturedMail : IAccountMailer
{
    private readonly ConcurrentDictionary<(string Email, AccountCodePurpose Purpose), string> _codes = new();
    private readonly ConcurrentDictionary<(string Email, AccountCodePurpose Purpose), int> _counts = new();

    public Task SendCodeAsync(string email, AccountCodePurpose purpose, string code, CancellationToken cancellationToken)
    {
        _codes[(email, purpose)] = code;
        _counts.AddOrUpdate((email, purpose), 1, (_, count) => count + 1);
        return Task.CompletedTask;
    }

    /// <summary>Adrese bu amaçla kaç e-posta gönderildiği.</summary>
    public int CountFor(string email, AccountCodePurpose purpose) => _counts.GetValueOrDefault((email, purpose));

    /// <summary>Adrese bu amaçla gönderilen son kod; hiç gönderilmediyse <c>null</c>.</summary>
    public string? CodeFor(string email, AccountCodePurpose purpose) => _codes.GetValueOrDefault((email, purpose));
}

[CollectionDefinition(Name)]
public sealed class ShopCollection : ICollectionFixture<ShopFixture>
{
    public const string Name = "Shop";
}
