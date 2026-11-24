namespace Haldus;

/// <summary>
/// <c>MapDetail</c> çağrısının döndürdüğü builder. Zincir metotları çalışma zamanında bir şey yapmaz;
/// generator onları derleme zamanında okur.
/// </summary>
public sealed class DetailBuilder<TEntity, TDetail>
    where TEntity : class
    where TDetail : class
{
    /// <summary>
    /// Satırları üst kaydın istek ve yanıtlarına gömmek yerine kendi uçlarına ayırır
    /// (<c>/orders/{orderId}/order-notes</c>). Çok sayıda satırı olan veya satırları tek tek değişen
    /// kaynaklar için.
    /// </summary>
    public DetailBuilder<TEntity, TDetail> AsSubresource() => this;
}
