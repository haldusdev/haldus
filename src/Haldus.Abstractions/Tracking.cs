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