using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Account;

/// <summary>Kullanıcının kendi alışveriş listesi; yalnızca sahibi görür.</summary>
public partial class ShoppingList
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";
}

/// <summary>Listenin kalemi. Sahibi yoktur; listenin altında yaşadığı için liste kimeyse o görür.</summary>
public partial class ShoppingListItem
{
    [Required]
    [MaxLength(200)]
    public string Text { get; set; } = "";

    public bool Done { get; set; }
}

/// <summary>Kullanıcının bir ürün için kendine tuttuğu not. Ürün herkese açıktır, not yalnızca yazanına.</summary>
public partial class ProductNote
{
    [Required]
    [MaxLength(1000)]
    public string Text { get; set; } = "";
}
