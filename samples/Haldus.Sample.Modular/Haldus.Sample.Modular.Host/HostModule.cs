using System.ComponentModel.DataAnnotations;
using Haldus.Sample.Modular.Support;
using Microsoft.EntityFrameworkCore;

namespace Haldus.Sample.Modular.Host;

/// <summary>Testlerin uygulamayı başlatırken gösterdiği tip.</summary>
public sealed class ModularApp;

/// <summary>Ortak bağlam: bu projenin ve kendi bağlamı olmayan modüllerin entity'leri.</summary>
public partial class HostDbContext(DbContextOptions<HostDbContext> options) : DbContext(options);

public partial class Feedback
{
    [Range(1, 5)]
    public int Rating { get; set; }
}

public partial class TicketAttachment
{
    [MaxLength(200)]
    public string FileName { get; set; } = "";
}

public static class HostModule
{
    public static void MapHost(this IEndpointRouteBuilder app)
    {
        // Talep başka bir modülün entity'sidir, ama bu projeyle aynı (ortak) bağlamdadır: referans gerçek bir
        // yabancı anahtar kısıtı olur ve geri bildirimi olan talep silinemez.
        app.MapCrud<Feedback>()
            .References<Ticket>();

        // Aynı bağlamda oldukları için başka modülün kaydı ebeveyn de olabilir: /tickets/{ticketId}/ticket-attachments
        app.MapCrud<TicketAttachment>()
            .Under<Ticket>();
    }
}