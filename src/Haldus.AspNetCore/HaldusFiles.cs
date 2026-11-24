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
    /// işidir. İçerik kabul edilmezse nedenini söyleyen yanıtı döndürür ve kayıt değişmez.
    /// </summary>
    /// <param name="maxBytes">Kabul edilen en büyük içerik.</param>
    /// <param name="contentTypes">Kabul edilen türler (<c>image/png</c>, <c>image/*</c>); boşsa her tür kabul edilir.</param>
    public static async Task<ProblemHttpResult?> ReceiveAsync<TEntity>(
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

        if (contentTypes.Count > 0 && !contentTypes.Any(allowed => Matches(allowed, contentType)))
        {
            return HaldusProblems.FileTypeNotAllowed(messages, contentType ?? string.Empty);
        }

        if (request.ContentLength > maxBytes)
        {
            return HaldusProblems.FileTooLarge(messages, maxBytes);
        }

        // Sunucunun kendi gövde sınırı daha küçükse bu uç için yükseltilir; daha büyükse bizimki geçerlidir.
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = maxBytes + 1;
        }

        var storage = context.GetService<IHaldusFileStorage>();
        var key = file.StorageKey ?? $"{typeof(TEntity).Name}/{Guid.NewGuid():N}";

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var content = new MeasuredStream(request.Body, maxBytes, hash);

        try
        {
            await storage.WriteAsync(context, key, content, cancellationToken);
        }
        catch (FileTooLargeException)
        {
            return HaldusProblems.FileTooLarge(messages, maxBytes);
        }

        if (content.Length == 0)
        {
            return HaldusProblems.FileEmpty(messages);
        }

        file.StorageKey = key;
        file.ContentType = contentType;
        file.Size = content.Length;
        file.Checksum = Convert.ToHexStringLower(hash.GetHashAndReset());
        file.Status = FileStatus.Uploaded;
        file.UploadedAt = HaldusTime.UtcNow(context.GetService<TimeProvider>());
        return null;
    }

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