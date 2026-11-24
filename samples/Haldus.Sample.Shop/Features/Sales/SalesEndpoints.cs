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

        // Adres müşterinin adresi altında yaşar: /customers/{customerId}/addresses
        // Müşteri kiracıya bağlı olduğu için adreslere de yalnızca o kiracı ulaşabilir.
        app.MapCrud<Address>()
            .Under<Customer>();

        // Aşağıdaki ikisi adresin kendisi gibi korunur: adres görünmüyorsa ona not eklenemez ve
        // bir teslimat ona bağlanamaz.
        app.MapCrud<AddressNote>()
            .Under<Address>();

        app.MapCrud<Delivery>()
            .References<Address>();

        var orders = app.MapCrud<Order>()
            .MultiTenant()
            .Auditable()
            .ConcurrencyChecked();

        // Satırlar siparişin istek ve yanıtlarına gömülüdür; ayrı uçları yoktur.
        orders.MapDetail<OrderItem>();

        // Notlar siparişe aittir ama kendi uçlarından yönetilir: /orders/{orderId}/order-notes
        orders.MapDetail<OrderNote>().AsSubresource();

        // Müşteri kiracıya bağlı olduğu için bağlar da öyledir: başka bir kiracı ne bağı görebilir
        // ne de etiketin altında bu müşteriyi listeleyebilir.
        app.MapAssociation<Customer, Catalog.Tag>();
    }
}
