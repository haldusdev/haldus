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

        // Bakiye: yalnızca komutlarla değişir ve her değişimi deftere geçer. Anahtarı istemci verir (burada SKU).
        // POST /inventories/{key}/adjust  { "delta": -3, "bucket": "available", "reason": "sipariş 1042" }
        // POST /inventories/{key}/set     { "quantity": 40, "expected": 38 }   (sayım; bakiye 38 değilse 409)
        // POST /inventories/{key}/move    { "from": "available", "to": "reserved", "quantity": 2 }
        // GET  /inventories/{key}/entries (defter)
        // Hiçbir kova sıfırın altına düşemez; yetersiz bakiye 409 döner ve hiçbir şey yazılmaz.
        app.MapBalance<Inventory>()
            .MultiTenant()
            .Buckets("available", "reserved", "damaged")
            .Idempotent()
            .Sortable(inventory => inventory.Available);

        // Tek miktarlı bakiye; eksiye düşebilir (avans izin gibi).
        app.MapBalance<LeaveAllowance>()
            .AllowNegative();
    }
}
