using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Catalog;

public partial class Category
{
    // Id'yi kendin yazarsan Haldus onu kullanır; int ve long kimlikleri veritabanı üretir.
    public int Id { get; set; }

    [StringLength(100, MinimumLength = 2)]
    public string Name { get; set; } = "";
}
