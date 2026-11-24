namespace Haldus.Sample.Shop.Features.Stock;

public static class StockEndpoints
{
    public static void MapStock(this IEndpointRouteBuilder app)
    {
        // Stok hareketi bir kez yazılır, sonra değişmez; düzeltme, onu nötrleyen ters hareketle yapılır:
        // POST /stock-movements/{id}/reverse
        app.MapAppendOnly<StockMovement>()
            .Auditable()
            .Reversible();

        // Stok seviyesini istemci yazamaz; her hareket kaydedilirken StockLevelProjection onu günceller.
        app.MapReadOnly<StockLevel>()
            .DerivedFrom<StockMovement>();
    }
}
