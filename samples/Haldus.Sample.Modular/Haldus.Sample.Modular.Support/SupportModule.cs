using System.ComponentModel.DataAnnotations;
using Haldus.Sample.Modular.Sales;
using Microsoft.AspNetCore.Routing;

namespace Haldus.Sample.Modular.Support;

public partial class Ticket
{
    [MaxLength(200)]
    public string Subject { get; set; } = "";
}

/// <summary>
/// Destek modülünün kendi bağlamı yoktur; entity'leri ana projenin <c>AddHaldus</c> ile bildirdiği ortak
/// bağlama girer. Modülün kayıt yapması gerekmez: ana proje onu kullandığı için Haldus kendisi bulur.
/// </summary>
public static class SupportModule
{
    public static void MapSupport(this IEndpointRouteBuilder app)
    {
        // Sipariş satış modülünün kendi bağlamındadır; bu referans da yalnızca kimlikle tutulur.
        app.MapCrud<Ticket>()
            .References<Order>(required: false);
    }
}