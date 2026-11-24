namespace Haldus;

/// <summary>
/// Ters kaydın (<c>.Reversible()</c>) içeriğini düzeltir. Haldus ters kaydı asıl kaydın kopyası olarak kurar ve
/// sonra bunu çağırır; burada yalnızca fark yazılır (ör. tutarın işareti çevrilir).
/// </summary>
public interface IReversal<in TEntity>
{
    /// <param name="original">Ters çevrilen kayıt.</param>
    void Reverse(TEntity original);
}

/// <summary>
/// Yerine geçen kaydın (<c>.Supersedes()</c>) içeriğini düzeltir. Haldus yeni kaydı eskisinin kopyası olarak kurar
/// ve sonra bunu çağırır; istekte gönderilen alanlar en son yazılır.
/// </summary>
public interface ISupersession<in TEntity>
{
    /// <param name="previous">Yerine geçilen kayıt.</param>
    void Supersede(TEntity previous);
}

/// <summary>
/// Düzeltme kaydının (<c>.Document()</c> altında <c>POST /{id}/amend</c>) içeriğini düzeltir. Haldus yeni taslağı
/// iptal edilmiş belgenin kopyası olarak kurar ve sonra bunu çağırır; istekte gönderilen alanlar en son yazılır.
/// </summary>
public interface IAmendment<in TEntity>
{
    /// <param name="cancelled">Düzeltilen, iptal edilmiş belge.</param>
    void Amend(TEntity cancelled);
}

/// <summary>
/// Başka bir kayıttan üretilen kaydın (<c>.CreatedFrom&lt;TSource&gt;()</c>) içeriğini doldurur. Haldus aynı adlı
/// alanları kopyalar ve sonra bunu çağırır; adı farklı alanlar ve satırlar burada yazılır.
/// </summary>
public interface ICreatedFrom<in TSource>
{
    void CreateFrom(TSource source);
}

/// <summary>
/// Bir kaynağın (<typeparamref name="TTarget"/>) başka kayıtlardan (<typeparamref name="TSource"/>) nasıl
/// türediğini söyler. Haldus bu sınıfı bulur, kaydeder ve her kaynak kayıt oluşturulduğunda, o kayıtla aynı
/// transaction'da çağırır.
/// </summary>
public interface IDerivation<in TSource, TTarget>
    where TTarget : class
{
    /// <param name="source">Oluşturulmakta olan kaynak kayıt; henüz kaydedilmemiştir.</param>
    /// <param name="targets">Türetilen kayıtlar. Buradan okunan kayıtlardaki değişiklikler kaynakla birlikte kaydedilir.</param>
    Task ApplyAsync(TSource source, IDerivedSet<TTarget> targets, CancellationToken cancellationToken);
}

/// <summary>Bir izdüşümün yazdığı, başka kayıtlardan türeyen kayıtlar.</summary>
public interface IDerivedSet<TTarget>
    where TTarget : class
{
    /// <summary>İzlenen kayıtlar: okunan bir kayıt değiştirilirse değişiklik kaydedilir.</summary>
    IQueryable<TTarget> Records { get; }

    void Add(TTarget record);

    void Remove(TTarget record);
}
