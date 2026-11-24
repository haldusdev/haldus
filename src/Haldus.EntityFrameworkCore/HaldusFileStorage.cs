using Microsoft.EntityFrameworkCore;

namespace Haldus;

/// <summary>
/// Dosya içeriklerinin saklandığı yer. Varsayılan uygulama içeriği veritabanında tutar
/// (<see cref="HaldusDatabaseFileStorage"/>); <see cref="HaldusFileSystemStorage"/> bir klasöre yazar. Başka bir
/// depo (nesne deposu gibi) bu arayüzü uygulayıp servis olarak kaydedilerek kullanılır.
/// </summary>
public interface IHaldusFileStorage
{
    /// <summary>
    /// İçeriği verilen anahtarla yazar; anahtarda içerik varsa değiştirir. Çağıran, dosya kaydını aynı bağlamla
    /// hemen ardından kaydeder; depo bağlamı kullanıyorsa içerik ve kayıt birlikte kalıcı olur.
    /// </summary>
    Task WriteAsync(DbContext context, string key, Stream content, CancellationToken cancellationToken);

    /// <summary>İçeriği okumak için açar; anahtarda içerik yoksa <c>null</c> döner. Akışı çağıran kapatır.</summary>
    Task<Stream?> OpenReadAsync(DbContext context, string key, CancellationToken cancellationToken);

    /// <summary>İçeriği siler; yoksa bir şey yapmaz. Dosya kaydı silindikten sonra çağrılır.</summary>
    Task DeleteAsync(DbContext context, string key, CancellationToken cancellationToken);
}

/// <summary>Veritabanında saklanan dosya içeriği.</summary>
public sealed class HaldusFileContent
{
    public string Key { get; set; } = "";

    public byte[] Content { get; set; } = [];
}

/// <summary>Dosya içeriklerinin tablosu; yalnızca bir dosya kaynağı varsa modele girer.</summary>
public static class HaldusFileStore
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HaldusFileContent>(entity =>
        {
            entity.ToTable("HaldusFileContents");
            entity.HasKey(content => content.Key);
            entity.Property(content => content.Key).HasMaxLength(256);
        });
    }
}

/// <summary>
/// İçeriği dosya kaydıyla aynı veritabanında tutar: içerik ve kayıt tek kaydetmede birlikte kalıcı olur, ayrı bir
/// altyapı gerekmez. İçerik belleğe alındığı için küçük ve orta boy dosyalar içindir.
/// </summary>
public sealed class HaldusDatabaseFileStorage : IHaldusFileStorage
{
    public async Task WriteAsync(DbContext context, string key, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(content);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        var existing = await context.Set<HaldusFileContent>().FindAsync([key], cancellationToken);

        if (existing is null)
        {
            context.Set<HaldusFileContent>().Add(new HaldusFileContent { Key = key, Content = buffer.ToArray() });
        }
        else
        {
            existing.Content = buffer.ToArray();
        }
    }

    public async Task<Stream?> OpenReadAsync(DbContext context, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var content = await context.Set<HaldusFileContent>()
            .AsNoTracking()
            .Where(stored => stored.Key == key)
            .Select(stored => stored.Content)
            .FirstOrDefaultAsync(cancellationToken);

        return content is null ? null : new MemoryStream(content, writable: false);
    }

    public Task DeleteAsync(DbContext context, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Set<HaldusFileContent>().Where(stored => stored.Key == key).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>
/// İçeriği bir klasörde tutar. İçerik önce geçici bir dosyaya yazılır ve yazma bitince yerine taşınır; yarım kalmış
/// bir yükleme eski içeriğin yerine geçmez. Birden çok sunucu aynı klasörü (ortak bir disk) görmelidir.
/// </summary>
public sealed class HaldusFileSystemStorage : IHaldusFileStorage
{
    private readonly string _root;

    public HaldusFileSystemStorage(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public async Task WriteAsync(DbContext context, string key, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public Task<Stream?> OpenReadAsync(DbContext context, string key, CancellationToken cancellationToken)
    {
        var path = PathOf(key);

        return Task.FromResult<Stream?>(File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null);
    }

    public Task DeleteAsync(DbContext context, string key, CancellationToken cancellationToken)
    {
        File.Delete(PathOf(key));
        return Task.CompletedTask;
    }

    // Anahtarı Haldus üretir; yine de klasörün dışını gösteren bir anahtar hiçbir koşulda kabul edilmez.
    private string PathOf(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (key.Split('/').Any(segment => segment.Length == 0 || !segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("A storage key is made of letters, digits, '-' and '_' separated by '/'.", nameof(key));
        }

        var path = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));

        return path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? path
            : throw new ArgumentException("The storage key points outside the storage root.", nameof(key));
    }
}