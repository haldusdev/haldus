using System.Data.Common;
using System.Net;
using Haldus.Sample.Shop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus.IntegrationTests;

/// <summary>
/// <c>Shop:CreateDatabase</c>: deneme veritabanı açılışta oluşturulur; örneğin başka bir sürümüyle oluşturulmuş
/// veritabanı ise sessizce kullanılmaz (çalışma anında bulunamayan kolonlarla bozulurdu), uygulama nedenini söyleyerek
/// açılmaz.
/// </summary>
[Collection(ShopCollection.Name)]
public sealed class DemoDatabaseTests(ShopFixture fixture)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DatabaseIsCreatedOnce_AndADatabaseOfAnotherVersionStopsTheApplication()
    {
        var database = new DbConnectionStringBuilder { ConnectionString = fixture.ConnectionString };
        database["Database"] = $"demo_{Guid.NewGuid():N}";
        (string, string)[] settings = [("ConnectionStrings:Default", database.ConnectionString), ("Shop:CreateDatabase", "true")];

        // 1. Boş sunucuda veritabanı ve tablolar oluşturulur; uygulama çalışır.
        await using (var first = fixture.CreateAuthenticatedFactory(settings))
        {
            Assert.Equal(HttpStatusCode.OK, (await first.CreateClient().GetAsync("/auth/config", CancellationToken)).StatusCode);
        }

        // 2. Aynı sürüm aynı veritabanıyla yeniden açılır (hiçbir şey yeniden oluşturulmaz, veri kalır).
        await using (var second = fixture.CreateAuthenticatedFactory(settings))
        {
            Assert.Equal(HttpStatusCode.OK, (await second.CreateClient().GetAsync("/auth/config", CancellationToken)).StatusCode);

            // Veritabanı örneğin başka bir sürümüyle oluşturulmuş olsun.
            await using var scope = second.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
                .ExecuteSqlRawAsync("""UPDATE "ShopDemoSchema" SET "Hash" = 'another-version'""", CancellationToken);
        }

        // 3. Şeması değişmiş veritabanı kullanılmaz: uygulama açılmaz ve ne yapılacağını söyler.
        await using (var stale = fixture.CreateAuthenticatedFactory(settings))
        {
            Assert.Contains(Chain(Assert.ThrowsAny<Exception>(() => stale.CreateClient())), error => error.Message.StartsWith("Shop:CreateDatabase found a database", StringComparison.Ordinal));
        }

        // 4. Bu denetimden önce (ya da başka bir yoldan) oluşturulmuş, işaret taşımayan veritabanı da öyledir: testlerin
        // kendi veritabanı böyle oluşturulur.
        await using var unmarked = fixture.CreateAuthenticatedFactory(("Shop:CreateDatabase", "true"));
        Assert.Contains(Chain(Assert.ThrowsAny<Exception>(() => unmarked.CreateClient())), error => error.Message.StartsWith("Shop:CreateDatabase found a database", StringComparison.Ordinal));

        static IEnumerable<Exception> Chain(Exception? error)
        {
            for (; error is not null; error = error.InnerException)
            {
                yield return error;
            }
        }
    }
}
