namespace Haldus;

/// <summary>
/// Bir kaynağın kayıtlarının kime ait olduğu; üretilen her uç bunu metadata olarak taşır. Uygulama yetki kurallarını
/// buna göre yazabilir (ör. "kimseye ait olmayan, ortak kaynakları yalnızca personel yazar"):
/// <code>
/// var scope = httpContext.GetEndpoint()?.Metadata.GetMetadata&lt;HaldusResourceScope&gt;();
/// </code>
/// Kapsam derleme zamanında, kaynağın zincirinden ve ebeveynlerinden çözülür.
/// </summary>
/// <param name="tenantScoped">Kayıtlar bir kiracıya ait: kaynak <c>MultiTenant</c>'tır veya öyle bir ebeveynin altındadır.</param>
/// <param name="ownerScoped">Kayıtlar tek bir kullanıcıya ait: kaynak sahiplidir veya sahipli bir ebeveynin altındadır.</param>
public sealed class HaldusResourceScope(bool tenantScoped, bool ownerScoped)
{
    public bool TenantScoped { get; } = tenantScoped;

    public bool OwnerScoped { get; } = ownerScoped;

    /// <summary>Kayıtlar ne bir kiracıya ne bir kullanıcıya ait: onları gören herkes için ortaktır.</summary>
    public bool IsShared => !TenantScoped && !OwnerScoped;
}

/// <summary>
/// Gövde taşıdığı için <c>POST</c> ile çağrılan ama hiçbir şey yazmayan uçların işareti: arama (<c>/search</c>), toplu
/// okuma (<c>/batch-get</c>) ve önizleme (<c>/preview</c>). Yetki kuralları okumayı yazmadan HTTP metoduna bakarak
/// ayıramaz; adresin sonuna bakmak da güvenilir değildir (bir aksiyonun veya anahtarın adı aynı olabilir). Bu yüzden
/// uç kendisi söyler.
/// </summary>
public sealed class HaldusReadOnly
{
    public static HaldusReadOnly Instance { get; } = new();
}