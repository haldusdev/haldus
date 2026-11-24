using Microsoft.AspNetCore.Http;

namespace Haldus;

/// <summary>Üretilen uçların adres hesaplamak için kullandığı yardımcılar.</summary>
public static class HaldusRoutes
{
    /// <summary>
    /// Bir kaydın alt adresine (<c>/orders/{id}/clone</c>) yapılan isteğin oluşturduğu yeni kaydın adresi:
    /// <c>/orders/{yeniId}</c>.
    /// </summary>
    public static string Sibling(HttpContext httpContext, object id)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(id);

        var path = httpContext.Request.Path.Value?.TrimEnd('/') ?? string.Empty;
        var action = path.LastIndexOf('/');
        var record = action > 0 ? path.LastIndexOf('/', action - 1) : -1;

        return $"{httpContext.Request.PathBase}{(record < 0 ? string.Empty : path[..record])}/{id}";
    }

    /// <summary>
    /// Bir koleksiyon adresine (<c>/orders/{id}/edits</c>) yapılan isteğin oluşturduğu kaydın adresi:
    /// <c>/orders/{id}/edits/{yeniId}</c>.
    /// </summary>
    public static string Child(HttpContext httpContext, object id)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(id);

        return $"{httpContext.Request.PathBase}{httpContext.Request.Path.Value?.TrimEnd('/')}/{id}";
    }
}
