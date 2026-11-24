using Microsoft.AspNetCore.Builder;

namespace Haldus;

public static class HaldusApplicationBuilderExtensions
{
    /// <summary>İstek dilini Accept-Language başlığından çözer; Haldus mesajları bu dile göre döner.</summary>
    public static IApplicationBuilder UseHaldus(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseRequestLocalization();
    }
}
