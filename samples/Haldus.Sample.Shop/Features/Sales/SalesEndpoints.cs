namespace Haldus.Sample.Shop.Features.Sales;

public static class SalesEndpoints
{
    public static void MapSales(this IEndpointRouteBuilder app)
    {
        app.MapCrud<Customer>()
            .MultiTenant()
            .Auditable(trackUser: true)
            .SoftDeletable()
            .Archivable()
            .ConcurrencyChecked()
            .Searchable(
                customer => customer.Id,
                customer => customer.Name,
                customer => customer.Email,
                customer => customer.LoyaltyPoints,
                customer => customer.CreatedAt,
                customer => customer.UpdatedAt);
    }
}
