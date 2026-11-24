namespace Haldus;

/// <summary>
/// Sahibinin okuyup okumadığı izlenen kayıt (<c>.ReadTracking()</c>): bildirim, mesaj. Alanı Haldus üretir ve
/// yalnızca okundu uçları değiştirir.
/// </summary>
public interface IReadTracked
{
    /// <summary>Kaydın okunduğu an; okunmadıysa boştur.</summary>
    DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Okunmamış kayıtların sayısı.</summary>
public sealed record UnreadCount(long Count);

/// <summary>"Hepsini okundu işaretle" çağrısının sonucu.</summary>
/// <param name="Updated">Okundu olarak işaretlenen kayıt sayısı.</param>
public sealed record ReadAllResult(int Updated);
/// <summary>
/// Ölçüte uyan kayıtların topluca silinmesinin sonucu (<c>POST /purge</c>). <c>force</c> verilmediyse hiçbir şey
/// silinmez; yanıt yalnızca neyin silineceğini gösterir.
/// </summary>
/// <param name="Matched">Ölçüte uyan kayıtların sayısı.</param>
/// <param name="Purged">Bu istekte silinen kayıtların sayısı; <paramref name="Matched"/> değerinden azsa istek yinelenir.</param>
/// <param name="Sample">Ölçüte uyan kayıtlardan ilk yüzünün anahtarları.</param>
public sealed record PurgeResult<TKey>(long Matched, int Purged, IReadOnlyList<TKey> Sample);
