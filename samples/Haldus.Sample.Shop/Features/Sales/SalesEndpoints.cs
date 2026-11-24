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
            .Under<Customer>()
            .Batch();

        // Aşağıdaki ikisi adresin kendisi gibi korunur: adres görünmüyorsa ona not eklenemez ve
        // bir teslimat ona bağlanamaz.
        app.MapCrud<AddressNote>()
            .Under<Address>();

        app.MapCrud<Delivery>()
            .References<Address>();

        // Sipariş bir durum makinesidir: yalnızca taslakken düzenlenir, numarasını verilirken alır.
        var orders = app.MapCrud<Order>()
            .MultiTenant()
            .Auditable()
            .ConcurrencyChecked()
            .Workflow<OrderStatus>(initial: OrderStatus.Draft)
            .EditableIn(OrderStatus.Draft)
            .DeletableIn(OrderStatus.Draft, OrderStatus.Cancelled)
            .Numbered("ORD-{yyyy}-{seq:6}", assignOn: OrderStatus.Placed)
            .Idempotent();

        // Satırlar siparişin istek ve yanıtlarına gömülüdür; ayrı uçları yoktur.
        orders.MapDetail<OrderItem>();

        // Notlar siparişe aittir ama kendi uçlarından yönetilir: /orders/{orderId}/order-notes
        // Siparişin parçası oldukları için onlar da yalnızca sipariş taslakken yazılabilir.
        orders.MapDetail<OrderNote>().AsSubresource();

        // Geçişler: POST /orders/{id}/place, /ship, /cancel, /reopen
        orders.MapTransition<PlaceOrder>(from: OrderStatus.Draft, to: OrderStatus.Placed);
        orders.MapTransition("ship", from: OrderStatus.Placed, to: OrderStatus.Shipped);
        orders.MapTransition<CancelOrder>(from: [OrderStatus.Draft, OrderStatus.Placed], to: OrderStatus.Cancelled);
        orders.MapTransition<ReopenOrder>(
            from: OrderStatus.Cancelled,
            to: OrderStatus.Draft,
            (order, _) => order.Reopen());

        // Rezervasyon geçici bir tutmadır: onaylanır, bırakılır veya 15 dakika sonra kendiliğinden biter.
        var reservations = app.MapCrud<Reservation>()
            .MultiTenant()
            .Workflow<ReservationStatus>(initial: ReservationStatus.Held)
            .Expiring(TimeSpan.FromMinutes(15));

        reservations.MapTransition("confirm", from: ReservationStatus.Held, to: ReservationStatus.Confirmed);
        reservations.MapTransition("release", from: ReservationStatus.Held, to: ReservationStatus.Released);

        app.MapCrud<Cart>()
            .Expiring(TimeSpan.FromDays(7), sliding: true);

        app.MapCrud<Coupon>()
            .Expiring();

        // Uzun süren iş: POST 202 döner, iş arka planda çalışır. Mantığı ReportExportHandler sınıfındadır.
        app.MapJob<ReportExport>()
            .MultiTenant()
            .Auditable(trackUser: true)
            .Idempotent();

        // Numara bir sequence'ten gelir: iadeler birbirini beklemez, ama numaralarda boşluk olabilir.
        app.MapAppendOnly<Refund>()
            .MultiTenant()
            .References<Order>()
            .Numbered("RF-{seq:8}", gapless: false)
            .Idempotent(required: true);

        // Müşteri kiracıya bağlı olduğu için bağlar da öyledir: başka bir kiracı ne bağı görebilir
        // ne de etiketin altında bu müşteriyi listeleyebilir.
        app.MapAssociation<Customer, Catalog.Tag>();
    }
}
