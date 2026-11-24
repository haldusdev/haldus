using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Haldus.Sample.Shop.Features.Account;

/// <summary>Oturum açmış kullanıcının sepeti; yalnızca sahibi görür (<c>/me/baskets</c>).</summary>
public partial class Basket
{
    [MaxLength(200)]
    public string? Note { get; set; }
}

public partial class BasketItem
{
    [Required]
    [MaxLength(200)]
    public string ProductName { get; set; } = "";

    [Range(1, 1_000)]
    public int Quantity { get; set; }
}

/// <summary>
/// Oturumu olmayan ziyaretçinin sepeti. Sahibi bir kullanıcı değildir; ona yalnızca oluşturulurken bir kez
/// gösterilen token'ı taşıyan ulaşır.
/// </summary>
public partial class GuestBasket
{
    [MaxLength(200)]
    public string? Note { get; set; }
}

public partial class GuestBasketItem
{
    [Required]
    [MaxLength(200)]
    public string ProductName { get; set; } = "";

    [Range(1, 1_000)]
    public int Quantity { get; set; }
}

/// <summary>
/// <c>POST /guest-baskets/by-token/add-basket-item</c>. Ziyaretçinin token ile yapabildiği tek yazma budur; aynı ürün
/// ikinci kez eklenirse satır çoğalmaz, miktarı artar.
/// </summary>
public sealed record AddBasketItem : IEntityAction<GuestBasket>
{
    [Required]
    [MaxLength(200)]
    public string ProductName { get; init; } = "";

    [Range(1, 1_000)]
    public int Quantity { get; init; } = 1;

    public void Apply(GuestBasket entity)
    {
        if (entity.GuestBasketItems.FirstOrDefault(item => item.ProductName == ProductName) is { } existing)
        {
            existing.Quantity += Quantity;
            return;
        }

        entity.GuestBasketItems.Add(new GuestBasketItem { ProductName = ProductName, Quantity = Quantity });
    }
}

/// <summary><c>POST /me/baskets/{id}/merge</c>: oturum açan ziyaretçinin misafir sepetini kendi sepetine katar.</summary>
public sealed record MergeBasket
{
    [Required]
    [MaxLength(200)]
    public string GuestToken { get; init; } = "";
}

/// <summary>
/// Birleştirme kuralı alana özgüdür, bu yüzden bir entity aksiyonudur: Haldus kullanıcının sepetini satırlarıyla
/// yükler ve kaydeder; misafir sepetini bulmak, satırları katmak ve misafir sepetini silmek buradadır. İkisi aynı
/// bağlamda olduğu için birlikte kaydedilir: ya ikisi de olur ya hiçbiri.
/// </summary>
public sealed class MergeBasketHandler(AppDbContext db, IStringLocalizer<ShopMessages> messages)
    : IEntityActionHandler<Basket, MergeBasket>
{
    public async Task HandleAsync(Basket entity, MergeBasket request, CancellationToken cancellationToken)
    {
        // Token saklanmaz, yalnızca özeti saklanır. Süresi dolmuş sepet sorgu filtresinden geçemez.
        var tokenHash = HaldusSecrets.Hash(request.GuestToken);
        var guest = await db.Set<GuestBasket>()
            .Include(basket => basket.GuestBasketItems)
            .FirstOrDefaultAsync(basket => basket.TokenHash == tokenHash, cancellationToken);

        if (guest is null)
        {
            // Aksiyon reddedilir: hiçbir şey kaydedilmez ve açıklama istemciye 422 ile döner.
            throw new TransitionRejectedException(messages["GuestBasketNotFound"]);
        }

        foreach (var item in guest.GuestBasketItems.OrderBy(item => item.Position))
        {
            if (entity.BasketItems.FirstOrDefault(existing => existing.ProductName == item.ProductName) is { } existing)
            {
                existing.Quantity = Math.Min(existing.Quantity + item.Quantity, 1_000);
            }
            else
            {
                entity.BasketItems.Add(new BasketItem { ProductName = item.ProductName, Quantity = item.Quantity });
            }
        }

        // Misafir sepeti bir kez kullanılır; token'ı da onunla birlikte geçersiz olur.
        db.Remove(guest);
    }
}
