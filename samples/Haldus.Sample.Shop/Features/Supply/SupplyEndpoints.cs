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
    }
}
