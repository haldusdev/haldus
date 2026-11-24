using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Localization;

namespace Haldus.Sample.Shop.Features.Sales;

public enum OrderStatus
{
    Draft,
    Placed,
    Shipped,
    Cancelled,
}

// Geçişlerin iş mantığı üç yoldan biriyle verilir; aşağıda üçü de var.

/// <summary>
/// <c>POST /orders/{id}/place</c>. İş mantığı bağımlılık istediği için (çeviriler) bir handler sınıfındadır;
/// Haldus onu bulur ve kaydeder.
/// </summary>
public sealed record PlaceOrder
{
    [MaxLength(500)]
    public string? Note { get; init; }
}

public sealed class PlaceOrderHandler(IStringLocalizer<ShopMessages> messages) : ITransitionHandler<Order, PlaceOrder>
{
    public Task HandleAsync(Order entity, PlaceOrder request, CancellationToken cancellationToken)
    {
        if (entity.OrderItems.Count == 0)
        {
            // Geçiş reddedilir: hiçbir şey kaydedilmez ve açıklama istemciye 422 ile döner.
            throw new TransitionRejectedException(messages["OrderHasNoItems"]);
        }

        entity.Place(request.Note);
        return Task.CompletedTask;
    }
}

/// <summary><c>POST /orders/{id}/cancel</c>. Bağımlılık gerekmediği için istek kendi etkisini uygular.</summary>
public sealed record CancelOrder : ITransition<Order>
{
    [Required, MaxLength(500)]
    public string Reason { get; init; } = "";

    public void Apply(Order entity) => entity.Cancel(Reason);
}

/// <summary><c>POST /orders/{id}/reopen</c>. İş mantığı Map çağrısına verilen lambda'dadır.</summary>
public sealed record ReopenOrder;
