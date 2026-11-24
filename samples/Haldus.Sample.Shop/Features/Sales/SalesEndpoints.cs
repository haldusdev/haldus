namespace Haldus.Sample.Shop.Features.Sales;

public static class SalesEndpoints
{
    public static void MapSales(this IEndpointRouteBuilder app)
    {
        app.MapCrud<Customer>()
            .MultiTenant()
            .Auditable(trackUser: true)
            .SoftDeletable()
            .ConcurrencyChecked();
    }
}
