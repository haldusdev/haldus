using Microsoft.Extensions.Localization;

namespace Haldus.Sample.Shop.Features.Sales;

public static class SalesEndpoints
{
    public static void MapSales(this IEndpointRouteBuilder app)
    {
        var customers = app.MapCrud<Customer>()
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

        // Entity aksiyonu: bir kayıt üzerinde çalışır ama durumunu değiştirmez. İstek kendi etkisini uygular:
        // POST /customers/{id}/adjust-loyalty
        customers.MapAction<AdjustLoyalty>();

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
            .Idempotent()

            // Önizleme toplamı hesaplar ama kaydetmez: POST /orders/preview ve POST /orders/{id}/preview
            .Previewable();

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

        // Sepet kullanıldıkça yaşar; satırları sepetin isteğine gömülüdür.
        // Sepet satırlarıyla birlikte kopyalanabilir: POST /carts/{id}/clone
        var carts = app.MapCrud<Cart>()
            .Expiring(TimeSpan.FromDays(7), sliding: true)
            .Cloneable();

        carts.MapDetail<CartItem>();

        // Ödeme silinmez; yalnızca tahsil edilir veya iptal edilir. Hareketleri eklenir ve sonra değişmez.
        var payments = app.MapCrud<Payment>()
            .MultiTenant()
            .Without(CrudOperation.Delete)
            .References<Order>()
            .Workflow<PaymentStatus>(initial: PaymentStatus.RequiresCapture)
            .EditableIn(PaymentStatus.RequiresCapture)
            .Idempotent();

        payments.MapTransition("capture", from: PaymentStatus.RequiresCapture, to: PaymentStatus.Captured);
        payments.MapTransition("void", from: PaymentStatus.RequiresCapture, to: PaymentStatus.Voided);

        app.MapAppendOnly<PaymentTransaction>()
            .Under<Payment>();

        // Kupon koduyla da okunur (GET /coupons/by-code/{code}) ve kod benzersizdir. Kod ile indirim oranı
        // yalnızca oluştururken yazılır; dağıtılmış bir kuponun anlamı sonradan değişmez.
        var coupons = app.MapCrud<Coupon>()
            .Expiring()
            .NaturalKey(coupon => coupon.Code)
            .CreateOnly(coupon => coupon.Code, coupon => coupon.Percent)

            // Kopya yeni bir kod ister: POST /coupons/{id}/clone  { "code": "SPRING-2" }
            .Cloneable();

        // Gövdesiz aksiyon; mantığı lambda'dadır ve reddedilirse hiçbir şey kaydedilmez: POST /coupons/{id}/redeem
        coupons.MapAction("redeem", (coupon, services, _) =>
        {
            if (!coupon.TryRedeem())
            {
                throw new TransitionRejectedException(services.GetRequiredService<IStringLocalizer<ShopMessages>>()["CouponExhausted"]);
            }

            return Task.CompletedTask;
        });

        // Uzun süren iş: POST 202 döner, iş arka planda çalışır. Mantığı ReportExportHandler sınıfındadır.
        app.MapJob<ReportExport>()
            .MultiTenant()
            .Auditable(trackUser: true)
            .Idempotent();

        // Numara bir sequence'ten gelir: iadeler birbirini beklemez, ama numaralarda boşluk olabilir.
        // İade değiştirilemez, ama işlenmeden önce iptal edilebilir.
        var refunds = app.MapAppendOnly<Refund>()
            .MultiTenant()
            .References<Order>()
            .Workflow<RefundStatus>(initial: RefundStatus.Pending)
            .Numbered("RF-{seq:8}", gapless: false)
            .Idempotent(required: true)

            // İadeyi başlatan sistem kendi notlarını ekleyebilir (ör. destek kaydının numarası).
            .Metadata();

        refunds.MapTransition("cancel", from: RefundStatus.Pending, to: RefundStatus.Cancelled);

        // Müşteri kiracıya bağlı olduğu için bağlar da öyledir: başka bir kiracı ne bağı görebilir
        // ne de etiketin altında bu müşteriyi listeleyebilir.
        app.MapAssociation<Customer, Catalog.Tag>();
    }
}
