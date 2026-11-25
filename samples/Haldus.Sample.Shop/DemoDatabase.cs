using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Haldus.Sample.Shop;

/// <summary>
/// <c>Shop:CreateDatabase</c>: örnek arayüzleri denemek için tabloları açılışta oluşturur. Örnekte migration yoktur ve
/// <c>EnsureCreated</c> var olan tabloları değiştirmez; örnek güncellendikten sonra (yeni bir kolon veya indeks
/// geldiyse) eski deneme veritabanı çalışma anında, anlaşılmaz hatalarla (bulunamayan kolon, 500) bozulurdu. Bu yüzden
/// veritabanı, hangi şemayla oluşturulduğunu kendisi taşır ve şema değiştiyse uygulama açılırken, nedenini söyleyerek
/// durur. Veritabanını silmek kullanıcının işidir: veri kendiliğinden silinmez.
/// </summary>
internal static class DemoDatabase
{
    public static async Task EnsureAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var created = await db.Database.EnsureCreatedAsync(cancellationToken);

        // Şemanın parmak izi: modelin tamamını kuran betiğin özeti. Aynı sürüm aynı betiği üretir.
        var schema = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(db.Database.GenerateCreateScript())));

        if (created)
        {
            await db.Database.ExecuteSqlRawAsync("""CREATE TABLE "ShopDemoSchema" ("Hash" text NOT NULL)""", cancellationToken);
            await db.Database.ExecuteSqlAsync($"""INSERT INTO "ShopDemoSchema" ("Hash") VALUES ({schema})""", cancellationToken);
            return;
        }

        // İşaret tablosu yoksa veritabanı bu denetimden önceki bir sürümle (ya da başka bir yoldan) oluşturulmuştur.
        var marked = await db.Database
            .SqlQueryRaw<bool>("""SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'ShopDemoSchema') AS "Value" """)
            .SingleAsync(cancellationToken);

        var recorded = marked
            ? await db.Database.SqlQueryRaw<string>("""SELECT "Hash" AS "Value" FROM "ShopDemoSchema" LIMIT 1""").SingleOrDefaultAsync(cancellationToken)
            : null;

        if (!string.Equals(recorded, schema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Shop:CreateDatabase found a database that was created by another version of the sample. "
                + "The sample has no migrations and existing tables are never changed; drop the demo database (or point ConnectionStrings:Default at a new one) and start again.");
        }
    }
}
