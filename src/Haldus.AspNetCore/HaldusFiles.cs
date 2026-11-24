using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Net.Http.Headers;

namespace Haldus;

/// <summary>
/// Dosya kaynaklarının (<c>MapFile</c>) içeriğini alır ve gönderir. Üretilen uçlar kaydı bulur, yazma kurallarından
/// geçirir ve kaydeder; boyut ve tür sınırları, özetin hesaplanması ve depoyla konuşmak buradadır.
/// </summary>
public static class HaldusFiles
{
    /// <summary>
    /// İsteğin gövdesini dosyanın içeriği olarak depoya yazar ve kaydın alanlarını doldurur; kaydetmek çağıranın
    /// işidir. İçerik her zaman yeni bir anahtara yazılır ve kayıt ancak içerik kabul edildikten sonra ona geçer:
    /// reddedilen bir yükleme (boş, çok büyük) var olan içeriğe dokunmaz. Çağıran, kaydı kaydettikten sonra
    /// <see cref="FileUpload.CompleteAsync"/> ile eski içeriği kaldırır.
    /// </summary>
    /// <param name="maxBytes">Kabul edilen en büyük içerik.</param>
    /// <param name="contentTypes">Kabul edilen türler (<c>image/png</c>, <c>image/*</c>); boşsa her tür kabul edilir.</param>
    public static async Task<FileUpload> ReceiveAsync<TEntity>(
        HttpContext httpContext,
        DbContext context,
        TEntity file,
        long maxBytes,
        IReadOnlyList<string> contentTypes,
        IHaldusMessages messages,
        CancellationToken cancellationToken)
        where TEntity : class, IHaldusFile
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(contentTypes);

        var request = httpContext.Request;
        var contentType = MediaTypeHeaderValue.TryParse(request.ContentType, out var media) ? media.MediaType.Value : null;

        // İçerik gövdenin kendisi olabilir ya da bir multipart formun dosya parçası (tarayıcıların ve çoğu istemci
        // kütüphanesinin yüklediği biçim). Formun başlıkları ve sınır satırları içerikten sayılmaz.
        var multipart = string.Equals(contentType, "multipart/form-data", StringComparison.OrdinalIgnoreCase);
        var overhead = multipart ? MultipartOverhead : 0;

        if (request.ContentLength > maxBytes + overhead)
        {
            return FileUpload.Reject(HaldusProblems.FileTooLarge(messages, maxBytes));
        }

        // Sunucunun kendi gövde sınırı daha küçükse bu uç için yükseltilir; daha büyükse bizimki geçerlidir.
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = maxBytes + 1 + overhead;
        }

        var body = request.Body;

        if (multipart)
        {
            var boundary = HeaderUtilities.RemoveQuotes(media!.Boundary).Value;

            if (string.IsNullOrEmpty(boundary) || boundary.Length > 200)
            {
                return FileUpload.Reject(HaldusProblems.FileEmpty(messages));
            }

            // Formun ilk dosya parçası içeriktir; ondan önceki alanlar atlanır, sonrakiler okunmaz.
            var reader = new Microsoft.AspNetCore.WebUtilities.MultipartReader(boundary, request.Body);
            Microsoft.AspNetCore.WebUtilities.MultipartSection? section;

            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) && disposition.IsFileDisposition())
                {
                    break;
                }
            }

            if (section is null)
            {
                return FileUpload.Reject(HaldusProblems.FileEmpty(messages));
            }

            body = section.Body;
            contentType = MediaTypeHeaderValue.TryParse(section.ContentType, out var part) ? part.MediaType.Value : null;
        }

        if (contentTypes.Count > 0 && !contentTypes.Any(allowed => Matches(allowed, contentType)))
        {
            return FileUpload.Reject(HaldusProblems.FileTypeNotAllowed(messages, contentType ?? string.Empty));
        }

        var storage = context.GetService<IHaldusFileStorage>();
        var key = $"{typeof(TEntity).Name}/{Guid.NewGuid():N}";
        var replaced = file.StorageKey;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var content = new MeasuredStream(body, maxBytes, hash);

        try
        {
            await storage.WriteAsync(context, key, content, cancellationToken);
        }
        catch (FileTooLargeException)
        {
            await storage.DeleteAsync(context, key, cancellationToken);
            return FileUpload.Reject(HaldusProblems.FileTooLarge(messages, maxBytes));
        }

        if (content.Length == 0)
        {
            await storage.DeleteAsync(context, key, cancellationToken);
            return FileUpload.Reject(HaldusProblems.FileEmpty(messages));
        }

        file.StorageKey = key;
        file.ContentType = contentType;
        file.Size = content.Length;
        file.Checksum = Convert.ToHexStringLower(hash.GetHashAndReset());
        file.Status = FileStatus.Uploaded;
        file.UploadedAt = HaldusTime.UtcNow(context.GetService<TimeProvider>());
        return new FileUpload(null, storage, context, replaced);
    }

    /// <summary>Bir multipart formun, dosya içeriği dışında (sınır satırları, parça başlıkları) taşıyabildiği en çok bayt.</summary>
    private const long MultipartOverhead = 64 * 1024;

    /// <summary>Dosyanın içeriğini gönderir; içerik henüz yüklenmediyse 404 döner.</summary>
    public static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> SendAsync<TEntity>(
        HttpContext httpContext,
        DbContext context,
        TEntity file,
        IHaldusMessages messages,
        string resourceName,
        object id,
        CancellationToken cancellationToken)
        where TEntity : class, IHaldusFile
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);

        var content = file is { Status: FileStatus.Uploaded, StorageKey: { } key }
            ? await context.GetService<IHaldusFileStorage>().OpenReadAsync(context, key, cancellationToken)
            : null;

        if (content is null)
        {
            return HaldusProblems.FileContentMissing(messages, resourceName, id);
        }

        // Tür yükleyenin beyanıdır; tarayıcı onu tahminle değiştirmemeli ve içerik sayfanın içinde açılmamalıdır.
        httpContext.Response.Headers.XContentTypeOptions = "nosniff";

        return TypedResults.Stream(
            content,
            file.ContentType ?? "application/octet-stream",
            fileDownloadName: file.FileName,
            lastModified: file.UploadedAt,
            entityTag: file.Checksum is null ? null : new EntityTagHeaderValue($"\"{file.Checksum}\""),
            enableRangeProcessing: content.CanSeek);
    }

    /// <summary>Silinmiş bir dosya kaydının içeriğini depodan kaldırır.</summary>
    public static Task ReleaseAsync<TEntity>(DbContext context, TEntity file, CancellationToken cancellationToken)
        where TEntity : class, IHaldusFile
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);

        return file.StorageKey is { } key
            ? context.GetService<IHaldusFileStorage>().DeleteAsync(context, key, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>Bir yüklemenin sonucu: reddedildiyse nedeni; kabul edildiyse yerine geçtiği eski içerik.</summary>
    public readonly struct FileUpload
    {
        private readonly IHaldusFileStorage? _storage;
        private readonly DbContext? _context;
        private readonly string? _replaced;

        internal FileUpload(ProblemHttpResult? rejected, IHaldusFileStorage? storage, DbContext? context, string? replaced)
        {
            Rejected = rejected;
            _storage = storage;
            _context = context;
            _replaced = replaced;
        }

        /// <summary>İçerik kabul edilmediyse nedenini söyleyen yanıt; kabul edildiyse <c>null</c>.</summary>
        public ProblemHttpResult? Rejected { get; }

        internal static FileUpload Reject(ProblemHttpResult problem) => new(problem, null, null, null);

        /// <summary>Kayıt kaydedildikten sonra çağrılır: yeni içeriğin yerine geçtiği eski içeriği depodan kaldırır.</summary>
        public Task CompleteAsync(CancellationToken cancellationToken) =>
            _replaced is not null && _storage is not null && _context is not null
                ? _storage.DeleteAsync(_context, _replaced, cancellationToken)
                : Task.CompletedTask;
    }

    private static bool Matches(string allowed, string? contentType)
    {
        if (contentType is null)
        {
            return false;
        }

        return allowed.EndsWith("/*", StringComparison.Ordinal)
            ? contentType.StartsWith(allowed[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(allowed, contentType, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FileTooLargeException : Exception;

    /// <summary>Okunan baytları sayar ve özetler; sınır aşılınca okumayı keser.</summary>
    private sealed class MeasuredStream(Stream inner, long maxBytes, IncrementalHash hash) : Stream
    {
        private long _length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Measure(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The request body is read asynchronously.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Measure(ReadOnlySpan<byte> chunk)
        {
            _length += chunk.Length;

            if (_length > maxBytes)
            {
                throw new FileTooLargeException();
            }

            hash.AppendData(chunk);
        }
    }
}