using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Haldus;

/// <summary><c>ConcurrencyChecked</c> trait'inin sürüm bilgisini ETag ve If-Match başlıklarıyla taşır.</summary>
public static class HaldusETag
{
    public static void Write(HttpContext httpContext, uint version)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.Headers.ETag = $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";
    }

    public static IfMatch ReadIfMatch(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var header = httpContext.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return new IfMatch(IfMatchKind.Missing, Version: null);
        }

        if (header.Trim() == "*")
        {
            return new IfMatch(IfMatchKind.Any, Version: null);
        }

        return EntityTagHeaderValue.TryParse(header, out var tag)
            && uint.TryParse(tag.Tag.AsSpan().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                ? new IfMatch(IfMatchKind.Version, version)
                : new IfMatch(IfMatchKind.Invalid, Version: null);
    }
}

public enum IfMatchKind
{
    /// <summary>Başlık gönderilmedi.</summary>
    Missing,

    /// <summary>Başlık <c>*</c>: kayıt varsa sürümüne bakılmadan işlem yapılır.</summary>
    Any,

    /// <summary>Başlık bir sürüm taşıyor.</summary>
    Version,

    /// <summary>Başlık okunamadı; hiçbir sürümle eşleşemez.</summary>
    Invalid,
}

public readonly record struct IfMatch(IfMatchKind Kind, uint? Version);
