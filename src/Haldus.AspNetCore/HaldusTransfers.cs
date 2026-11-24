using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus;

/// <summary>
/// İçe ve dışa aktarma işlerinin (<c>Exports</c>, <c>Imports</c>) mantığı. Üretilen handler kaynağın kayıtlarını
/// ve tekil oluşturma kuralını verir; biçimler (CSV, JSON), sınırlar ve hata raporu buradadır.
/// </summary>
public static class HaldusTransfers
{
    /// <summary>Bir içe aktarmada kabul edilen en çok satır.</summary>
    public const int MaxImportRows = 10_000;

    /// <summary>Hata raporuna yazılan en çok satır.</summary>
    public const int MaxReportedErrors = 50;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // CSV'de her değer metindir; sayılar ve evet/hayır değerleri metinden okunur.
    private static readonly JsonSerializerOptions Lenient = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter(), new LenientBooleanConverter() },
    };

    /// <summary>
    /// Kayıtları işin istediği biçimde dosyaya döker, dosyayı depoya yazar ve işin çıktı alanlarını doldurur.
    /// Kayıtlar, işi başlatan kiracı ve kullanıcının görebildikleridir: çağıran onları kaynağın kendi
    /// filtrelerinden geçirerek verir.
    /// </summary>
    public static async Task ExportAsync<TJob, TRecord>(
        TJob job,
        IAsyncEnumerable<TRecord> records,
        JobContext jobContext,
        CancellationToken cancellationToken)
        where TJob : class, IHaldusExportJob
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(jobContext);

        var json = FormatOf(job.Format, jobContext.Services) == "json";
        var rows = 0;

        using var content = new MemoryStream();

        if (json)
        {
            await using var writer = new Utf8JsonWriter(content);
            writer.WriteStartArray();

            await foreach (var record in records.WithCancellation(cancellationToken))
            {
                JsonSerializer.Serialize(writer, record, Web);
                rows++;
            }

            writer.WriteEndArray();
        }
        else
        {
            // Excel dosyayı UTF-8 olarak açsın diye BOM yazılır.
            await using var writer = new StreamWriter(content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
            string[]? columns = null;

            await foreach (var record in records.WithCancellation(cancellationToken))
            {
                var element = JsonSerializer.SerializeToElement(record, Web);

                if (columns is null)
                {
                    columns = [.. element.EnumerateObject().Select(property => property.Name)];
                    await writer.WriteLineAsync(string.Join(',', columns.Select(column => Cell(column, text: true))));
                }

                await writer.WriteLineAsync(string.Join(',', columns.Select(column =>
                    element.TryGetProperty(column, out var value) ? Cell(value) : string.Empty)));
                rows++;
            }
        }

        content.Position = 0;

        var context = jobContext.Services.GetRequiredService<IHaldusDbContextProvider>().For<TJob>();
        var key = $"{typeof(TJob).Name}/{Guid.NewGuid():N}";
        await context.GetService<IHaldusFileStorage>().WriteAsync(context, key, content, cancellationToken);

        job.Rows = rows;
        job.Size = content.Length;
        job.FileName = $"{typeof(TJob).Name}-{HaldusTime.UtcNow(jobContext.Services.GetRequiredService<TimeProvider>()):yyyyMMddHHmmss}.{(json ? "json" : "csv")}";
        job.StorageKey = key;
    }

    /// <summary>Bitmiş bir dışa aktarma işinin dosyasını gönderir; iş henüz bitmediyse (veya başarısız olduysa) 404 döner.</summary>
    public static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> SendAsync<TJob>(
        HttpContext httpContext,
        DbContext context,
        TJob job,
        IHaldusMessages messages,
        string resourceName,
        object id,
        CancellationToken cancellationToken)
        where TJob : class, IHaldusExportJob
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(job);

        var content = job.StorageKey is { } key
            ? await context.GetService<IHaldusFileStorage>().OpenReadAsync(context, key, cancellationToken)
            : null;

        if (content is null)
        {
            return HaldusProblems.FileContentMissing(messages, resourceName, id);
        }

        httpContext.Response.Headers.XContentTypeOptions = "nosniff";

        return TypedResults.Stream(
            content,
            job.FileName?.EndsWith(".json", StringComparison.Ordinal) == true ? "application/json" : "text/csv",
            fileDownloadName: job.FileName,
            enableRangeProcessing: content.CanSeek);
    }

    /// <summary>
    /// İşin içeriğindeki satırları tek tek oluşturur. Bütün satırlar tek bir transaction içindedir: biri bile
    /// reddedilirse hiçbiri oluşturulmaz ve nedenleri işin <c>Errors</c> alanına yazılır. Böylece yarıda kalıp
    /// yeniden çalışan bir iş aynı kayıtları iki kez eklemez ve içe aktarma yarım bir veri bırakmaz.
    /// </summary>
    /// <param name="create">Tek bir kaydı, tekil oluşturma ucunun kurallarıyla oluşturur; reddederse nedenini döndürür.</param>
    public static async Task ImportAsync<TJob, TEntity, TRequest>(
        TJob job,
        Func<TRequest, IServiceProvider, CancellationToken, Task<ProblemHttpResult?>> create,
        JobContext jobContext,
        CancellationToken cancellationToken)
        where TJob : class, IHaldusImportJob
        where TEntity : class
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(jobContext);

        var services = jobContext.Services;
        var messages = services.GetRequiredService<IHaldusMessages>();
        var rows = Read<TRequest>(job.Content, FormatOf(job.Format, services), messages);

        if (rows.Count > MaxImportRows)
        {
            throw new JobFailedException(messages.Format(HaldusMessageKeys.ImportTooLarge, MaxImportRows));
        }

        var context = services.GetRequiredService<IHaldusDbContextProvider>().For<TEntity>();
        var errors = new List<string>();
        var failed = 0;
        var imported = 0;

        await using (var all = await HaldusBatch.BeginAsync(context, cancellationToken))
        {
            for (var index = 0; index < rows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (request, malformed) = rows[index];
                var reason = malformed
                    ?? (request is null ? messages.Format(HaldusMessageKeys.BatchItemMissingDetail) : Describe(await create(request, services, cancellationToken)));

                if (reason is null)
                {
                    imported++;
                }
                else
                {
                    failed++;

                    if (errors.Count < MaxReportedErrors)
                    {
                        errors.Add(messages.Format(HaldusMessageKeys.ImportRow, index + 1, reason));
                    }
                }

                if (rows.Count >= 20 && (index + 1) % (rows.Count / 20) == 0)
                {
                    await jobContext.ReportProgressAsync((index + 1) * 100 / rows.Count, cancellationToken);
                }
            }

            if (failed == 0)
            {
                await all.CommitAsync(cancellationToken);
            }
        }

        job.Imported = failed == 0 ? imported : 0;
        job.Failed = failed;
        job.Errors = errors.Count == 0 ? null : string.Join('\n', errors);

        // İçerik işin girdisiydi; iş bittikten sonra saklanmaz.
        job.Content = string.Empty;
    }

    private static string FormatOf(string? format, IServiceProvider services) => format?.Trim().ToLowerInvariant() switch
    {
        null or "" or "csv" => "csv",
        "json" => "json",
        _ => throw new JobFailedException(services.GetRequiredService<IHaldusMessages>().Format(HaldusMessageKeys.TransferFormatInvalid)),
    };

    /// <summary>İçeriği satırlara ayırır; okunamayan satır, nedeniyle birlikte döner.</summary>
    private static List<(TRequest? Request, string? Malformed)> Read<TRequest>(string content, string format, IHaldusMessages messages)
        where TRequest : class
    {
        var rows = new List<(TRequest?, string?)>();

        if (format == "json")
        {
            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(content);
            }
            catch (JsonException)
            {
                throw new JobFailedException(messages.Format(HaldusMessageKeys.ImportMalformed, "JSON"));
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new JobFailedException(messages.Format(HaldusMessageKeys.ImportMalformed, "JSON"));
                }

                foreach (var element in document.RootElement.EnumerateArray())
                {
                    rows.Add(Convert<TRequest>(element, Web, messages));
                }
            }

            return rows;
        }

        var lines = ParseCsv(content);

        if (lines.Count == 0)
        {
            return rows;
        }

        var header = lines[0];

        foreach (var line in lines.Skip(1))
        {
            var record = new JsonObject();

            for (var column = 0; column < header.Count && column < line.Count; column++)
            {
                var value = line[column];

                if (value.Length == 0 || header[column].Length == 0)
                {
                    continue;
                }

                // İç içe değerler (satırlar, çantalar) hücrede JSON olarak durur.
                record[header[column]] = value[0] is '{' or '[' && TryParse(value) is { } nested ? nested : JsonValue.Create(value);
            }

            rows.Add(Convert<TRequest>(JsonSerializer.SerializeToElement(record), Lenient, messages));
        }

        return rows;
    }

    private static (TRequest?, string?) Convert<TRequest>(JsonElement element, JsonSerializerOptions options, IHaldusMessages messages)
        where TRequest : class
    {
        try
        {
            return (element.Deserialize<TRequest>(options), null);
        }
        catch (JsonException exception)
        {
            // Hangi alanın okunamadığı söylenir; istisnanın geri kalanı istemciye gösterilmez.
            return (null, messages.Format(HaldusMessageKeys.ImportRowUnreadable, exception.Path ?? "$"));
        }
    }

    private static JsonNode? TryParse(string value)
    {
        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Bir reddin, hata raporuna yazılacak metni: başlık, ayrıntı ve alan hataları.</summary>
    private static string? Describe(ProblemHttpResult? problem)
    {
        if (problem is null)
        {
            return null;
        }

        var details = problem.ProblemDetails;
        var fields = details is HttpValidationProblemDetails validation
            ? validation.Errors.Select(error => $"{error.Key}: {string.Join(' ', error.Value)}")
            : details.Extensions.TryGetValue("errors", out var extension) && extension is IDictionary<string, string[]> listed
                ? listed.Select(error => $"{error.Key}: {string.Join(' ', error.Value)}")
                : [];

        return string.Join("; ", new[] { details.Detail ?? details.Title }.Concat(fields).Where(part => !string.IsNullOrEmpty(part)));
    }

    private static string Cell(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.String => Cell(value.GetString()!, text: true),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => Cell(value.GetRawText(), text: false),
    };

    private static string Cell(string value, bool text)
    {
        // Tablolama programları =, +, -, @ ile başlayan hücreyi formül sayar; dışa aktarılan bir metin orada
        // çalıştırılmamalıdır. Öne konan kesme işareti hücreyi metin yapar.
        if (text && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 || (value.Length > 0 && (value[0] == ' ' || value[^1] == ' '))
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    /// <summary>RFC 4180: tırnak içindeki virgül ve satır sonu değerin parçasıdır, çift tırnak tek tırnaktır.</summary>
    private static List<List<string>> ParseCsv(string content)
    {
        var lines = new List<List<string>>();
        var line = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var any = false;

        // BOM başlığın ilk sütun adına karışmasın.
        var text = content.Length > 0 && content[0] == '\uFEFF' ? content.AsSpan(1) : content.AsSpan();

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (quoted)
            {
                if (character != '"')
                {
                    cell.Append(character);
                }
                else if (index + 1 < text.Length && text[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (character)
            {
                case '"' when cell.Length == 0:
                    quoted = true;
                    any = true;
                    break;
                case ',':
                    line.Add(cell.ToString());
                    cell.Clear();
                    any = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    End();
                    break;
                default:
                    cell.Append(character);
                    any = true;
                    break;
            }
        }

        End();
        return lines;

        void End()
        {
            if (any || line.Count > 0)
            {
                line.Add(cell.ToString());
                lines.Add(line);
            }

            line = [];
            cell.Clear();
            any = false;
        }
    }

    private sealed class LenientBooleanConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String when reader.GetString()?.Trim().ToLowerInvariant() is "true" or "1" => true,
            JsonTokenType.String when reader.GetString()?.Trim().ToLowerInvariant() is "false" or "0" => false,
            _ => throw new JsonException(),
        };

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }
}