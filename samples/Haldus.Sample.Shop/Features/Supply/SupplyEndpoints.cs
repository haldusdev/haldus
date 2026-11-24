using Haldus.Sample.Shop.Features.Catalog;

namespace Haldus.Sample.Shop.Features.Supply;

public static class SupplyEndpoints
{
    public static void MapSupply(this IEndpointRouteBuilder app)
    {
        var suppliers = app.MapCrud<Supplier>()
            .ConcurrencyChecked();

        // Sertifikalar tedarikçinin yanıtında gömülüdür, ama güncelleme isteğinde topluca gönderilmez; tek tek
        // komutlarla yazılır: POST /suppliers/{id}/supplier-certificates, PUT ve DELETE …/{rowId}
        // Her komut tedarikçinin bir yazmasıdır: If-Match ister, sayacı yeniler ve tedarikçiyi döndürür.
        suppliers.MapDetail<SupplierCertificate>().AsCommands();

        // Ebeveyn başına tek kayıt: GET ve PUT /suppliers/{supplierId}/supplier-terms
        app.MapSingleton<SupplierTerms>()
            .Under<Supplier>();

        // shallow: true kaynağı kendi adresinde bırakır: /supplier-contacts/{id}
        // Tedarikçi yine zorunludur ve silinince kişileri de gider; ama adresten değil oluşturma isteğinden gelir.
        // Liste tedarikçiler arasında döner ve ?supplierId= ile süzülür; sıra tedarikçi içindedir.
        app.MapCrud<SupplierContact>()
            .Under<Supplier>(shallow: true)
            .Orderable();

        // SupplierProduct sınıfına yazılan alanlar bağın yüküdür:
        // PUT /suppliers/{supplierId}/products/{productId}  { "cost": 12.5, "leadTimeDays": 7 }
        // Bağlar yükleriyle GET /suppliers/{supplierId}/products/links altında listelenir.
        // Bağın durumu da vardır: önerilen bir tedarik onaylanır veya sonlandırılır. Durum istekle yazılamaz;
        // yalnızca geçişlerle değişir: POST /suppliers/{supplierId}/products/{productId}/approve
        var supplies = app.MapAssociation<Supplier, Product>()
            .Workflow<SupplyStatus>(initial: SupplyStatus.Proposed);

        supplies.MapTransition("approve", from: SupplyStatus.Proposed, to: SupplyStatus.Approved);
        supplies.MapTransition("discontinue", from: [SupplyStatus.Proposed, SupplyStatus.Approved], to: SupplyStatus.Discontinued);

        // Tedarikçinin fiyatı belirli bir dönemde geçerlidir; para birimi başına bir dönem dizisi tutulur. Dönemler
        // çakışamaz; açık uçlu fiyatın içinde başlayan yeni fiyat, eskisini kendi başladığı gün kapatır.
        // GET /suppliers/{supplierId}/supplier-rates?asOf=2026-06-01 o gün geçerli olanları döndürür.
        app.MapCrud<SupplierRate>()
            .Under<Supplier>()
            .EffectiveDated(rate => rate.Currency);

        // Satın alma talebi iki farklı kişinin sırayla onayına bağlıdır: POST /purchase-requests/{id}/approve ve
        // /reject, kararların listesi GET /purchase-requests/{id}/approvals. Talebi son yazan kişi karar veremez;
        // içerik değişince toplanan onaylar geçersiz olur. "order" geçişi talep onaylanmadan çalışmaz.
        var requests = app.MapCrud<PurchaseRequest>()
            .Workflow<PurchaseRequestStatus>(initial: PurchaseRequestStatus.Open)
            .Approvable(levels: 2, "order");

        // Onayın izin verdiği geçişin kendi yazdıkları onayı düşürmez.
        requests.MapTransition<OrderPurchase>(from: PurchaseRequestStatus.Open, to: PurchaseRequestStatus.Ordered, name: "order");

        // Onay istemeyen bir aksiyon içeriği değiştirirse toplanan onaylar düşer; aksiyonu çalıştıran da artık karar veremez.
        requests.MapAction<AdjustPurchase>();

        // Kalemler talebin içeriğidir; onları değiştiren bir aksiyon da onayı düşürür.
        requests.MapDetail<PurchaseRequestLine>();
        requests.MapAction("clear-lines", request => request.PurchaseRequestLines.Clear());

        // Tek onaylı, durum makinesi olmayan en yalın biçim.
        app.MapCrud<ExpenseClaim>()
            .Approvable();

        // Belge: taslak → gönder → iptal → düzelt. Taslak düzenlenir ve silinir, gönderilen sözleşme kilitlidir.
        // POST /supply-contracts/{id}/submit ve /cancel kendiliğinden üretilir; POST /{id}/amend iptal edilmiş
        // sözleşmenin kopyası olan yeni bir taslak oluşturur. Numara sözleşme gönderilirken atanır.
        app.MapCrud<SupplyContract>()
            .Document()
            .Numbered("CTR-{seq:4}", assignOn: DocumentStatus.Submitted);
    }
}
