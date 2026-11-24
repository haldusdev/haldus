namespace Haldus.Sample.Shop.Features.Stock;

public static class StockEndpoints
{
    public static void MapStock(this IEndpointRouteBuilder app)
    {
        // Stok hareketi bir kez yazılır, sonra değişmez; düzeltme yeni bir hareketle yapılır.
        app.MapAppendOnly<StockMovement>()
            .Auditable();

        // Stok seviyesini istemci yazamaz; hareketlerden sistem hesaplar.
        app.MapReadOnly<StockLevel>();
    }
}
