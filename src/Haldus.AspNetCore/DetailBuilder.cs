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

    /// <summary>
    /// Satırlar üst kaydın yanıtında gömülü kalır, ama güncelleme isteğinde topluca gönderilmek yerine tek tek
    /// komutlarla yazılır: <c>POST /orders/{id}/order-items</c> (ekle), <c>PUT …/{rowId}</c> (değiştir),
    /// <c>DELETE …/{rowId}</c> (kaldır). Her komut üst kaydın bir yazmasıdır: onun durum ve sürüm kurallarına
    /// uyar, hesaplanan alanlarını yeniler ve üst kaydı döndürür.
    /// </summary>
    public DetailBuilder<TEntity, TDetail> AsCommands() => this;
}
