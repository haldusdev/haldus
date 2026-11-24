using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Haldus;

/// <summary>
/// <c>.Idempotent()</c> trait'inin endpoint filtresi. İstek <c>Idempotency-Key</c> taşıyorsa anahtar, işlemin
/// kendisiyle aynı transaction'da kaydedilir; böylece işlem ya yanıtıyla birlikte kalıcı olur ya da hiç
/// olmamış sayılır. Aynı anahtar yeniden geldiğinde işlem çalıştırılmaz, saklanan yanıt döner.
/// </summary>
/// <param name="requestType">İstek gövdesinin tipi; gövdesiz uçlarda <c>null</c>.</param>
/// <param name="required">Başlık zorunlu mu.</param>
public sealed class HaldusIdempotencyFilter<TEntity>(Type? requestType, bool required) : IEndpointFilter
    where TEntity : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        var services = httpContext.RequestServices;
        var messages = services.GetRequiredService<IHaldusMessages>();
        var key = httpContext.Request.Headers[HaldusIdempotencyHeaders.Key].ToString().Trim();

        if (key.Length == 0 && !required)
        {
            return await next(context);
        }

        if (key.Length is 0 or > HaldusIdempotency.MaxKeyLength)
        {
            return HaldusProblems.IdempotencyKeyInvalid(messages);
        }

        var cancellationToken = httpContext.RequestAborted;
        var database = services.GetRequiredService<IHaldusDbContextProvider>().For<TEntity>();
        var now = HaldusTime.UtcNow(services.GetRequiredService<TimeProvider>());
        var scope = Scope(httpContext, services);
        var fingerprint = Fingerprint(context, services);
        var records = database.Set<HaldusIdempotencyRecord>();

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        // Saklama süresi dolmuş bir kayıt yok sayılır; aynı anahtar yeni bir istek olarak işlenir.
        await records
            .Where(candidate => candidate.Scope == scope && candidate.Key == key && candidate.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);

        var record = new HaldusIdempotencyRecord
        {
            Scope = scope,
            Key = key,
            Fingerprint = fingerprint,
            CreatedAt = now,
            ExpiresAt = now + services.GetRequiredService<IOptions<HaldusOptions>>().Value.IdempotencyRetention,
        };

        records.Add(record);

        try
        {
            // Aynı anahtarla süren başka bir istek varsa bu ekleme o bitene kadar bekler: o kalıcı olduysa
            // çakışır, geri alındıysa bu istek ilk istek olarak devam eder.
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.Entry(record).State = EntityState.Detached;

            var stored = await records.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Scope == scope && candidate.Key == key, cancellationToken);

            if (stored is null)
            {
                throw;
            }

            return stored.Fingerprint == fingerprint
                ? new ReplayedResponse(stored)
                : HaldusProblems.IdempotencyKeyReused(messages);
        }

        if (await next(context) is not IResult result)
        {
            throw new InvalidOperationException("An idempotent endpoint must return an IResult so its response can be stored.");
        }

        var response = await BufferedResponse.CaptureAsync(result, httpContext);

        // Yalnızca başarılı işlem saklanır. Başarısız istek, anahtarla birlikte geri alınır; istemci aynı
        // anahtarla düzeltip yeniden deneyebilir.
        if (response.StatusCode is >= 200 and < 300)
        {
            record.StatusCode = response.StatusCode;
            record.ContentType = httpContext.Response.ContentType;
            record.Body = Encoding.UTF8.GetString(response.Body);
            record.Location = NullIfEmpty(httpContext.Response.Headers.Location.ToString());
            record.ETag = NullIfEmpty(httpContext.Response.Headers.ETag.ToString());

            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return response;
    }

    /// <summary>Anahtar kiracıya, kullanıcıya ve adrese özeldir; başka birinin anahtarıyla yanıtı okunamaz.</summary>
    private static string Scope(HttpContext httpContext, IServiceProvider services)
    {
        var tenant = services.GetRequiredService<ITenantProvider>().TenantId?.ToString("N");
        var user = services.GetRequiredService<ICurrentUser>().UserId;
        var request = httpContext.Request;

        return Hash(Encoding.UTF8.GetBytes($"{tenant}\n{user}\n{request.Method}\n{request.PathBase}{request.Path}"));
    }

    private string Fingerprint(EndpointFilterInvocationContext context, IServiceProvider services)
    {
        if (requestType is null || context.Arguments.FirstOrDefault(argument => argument?.GetType() == requestType) is not { } body)
        {
            return Hash([]);
        }

        var options = services.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions ?? JsonSerializerOptions.Web;
        return Hash(JsonSerializer.SerializeToUtf8Bytes(body, requestType, options));
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>
    /// Yanıtı belleğe yazdırır; böylece durumu ve gövdesi istemciye gitmeden önce saklanabilir. Sonra aynı
    /// yanıtı gerçek akışa yazar.
    /// </summary>
    private sealed class BufferedResponse(int statusCode, byte[] body) : IResult
    {
        public int StatusCode { get; } = statusCode;

        public byte[] Body { get; } = body;

        public static async Task<BufferedResponse> CaptureAsync(IResult result, HttpContext httpContext)
        {
            var original = httpContext.Response.Body;
            using var buffer = new MemoryStream();
            httpContext.Response.Body = buffer;

            try
            {
                await result.ExecuteAsync(httpContext);
            }
            finally
            {
                httpContext.Response.Body = original;
            }

            return new BufferedResponse(httpContext.Response.StatusCode, buffer.ToArray());
        }

        public Task ExecuteAsync(HttpContext httpContext) =>
            Body.Length == 0 ? Task.CompletedTask : httpContext.Response.Body.WriteAsync(Body).AsTask();
    }

    private sealed class ReplayedResponse(HaldusIdempotencyRecord record) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = record.StatusCode;
            response.Headers[HaldusIdempotencyHeaders.Replayed] = "true";

            if (record.ContentType is not null)
            {
                response.ContentType = record.ContentType;
            }

            if (record.Location is not null)
            {
                response.Headers.Location = record.Location;
            }

            if (record.ETag is not null)
            {
                response.Headers.ETag = record.ETag;
            }

            return string.IsNullOrEmpty(record.Body) ? Task.CompletedTask : response.WriteAsync(record.Body);
        }
    }
}

public static class HaldusIdempotencyHeaders
{
    /// <summary>İstemcinin, isteği tekrara karşı korumak için gönderdiği başlık.</summary>
    public const string Key = "Idempotency-Key";

    /// <summary>Yanıt saklanan bir yanıtsa <c>true</c> değeriyle eklenen başlık.</summary>
    public const string Replayed = "Idempotent-Replayed";
}
