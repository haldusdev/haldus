namespace Haldus;

/// <summary>
/// Her yazmada anlık görüntüsü saklanan kayıt (<c>.Revisioned()</c>). Üyesi yoktur; anlık görüntüyü Haldus
/// kaydederken alır.
/// </summary>
public interface IRevisioned;

/// <summary>Bir kaydın saklanmış bir sürümü.</summary>
/// <param name="Id">Sürümün kimliği; kaydın sürümleri arasında artan sıradadır.</param>
/// <param name="CreatedBy">Sürümü oluşturan yazmayı yapan kullanıcı.</param>
public sealed record RevisionInfo(long Id, DateTimeOffset CreatedAt, string? CreatedBy);

/// <summary>
/// Belirli bir dönemde geçerli olan kayıt (<c>.EffectiveDated()</c>): ücret, fiyat, görev gibi, zamanla yerini bir
/// yenisine bırakan değerler. Dönem yarı açıktır: başlangıç günü dahil, bitiş günü hariç.
/// </summary>
public interface IEffectiveDated
{
    /// <summary>Kaydın geçerli olmaya başladığı gün.</summary>
    DateOnly EffectiveFrom { get; set; }

    /// <summary>Kaydın geçerliliğinin bittiği gün (hariç); boşsa kayıt hâlâ geçerlidir.</summary>
    DateOnly? EffectiveTo { get; set; }
}

/// <summary>
/// Değişiklikleri bir akıştan izlenebilen kayıt (<c>.ChangeFeed()</c>). Üyesi yoktur; değişikliği Haldus
/// kaydederken günlüğe yazar.
/// </summary>
public interface IChangeTracked;

/// <summary>Değişiklik akışındaki tek bir kayıt.</summary>
/// <param name="Id">Kaydın anahtarı, metin olarak.</param>
/// <param name="Deleted">Kayıt artık yok (silindi ya da isteği yapana görünmez oldu); <paramref name="Record"/> boştur.</param>
/// <param name="Record">Kaydın şimdiki hali.</param>
/// <param name="ChangedAt">Kaydın bu sayfadaki son değişikliğinin zamanı.</param>
public sealed record Change<T>(string Id, bool Deleted, T? Record, DateTimeOffset ChangedAt)
    where T : class;

/// <summary>Değişiklik akışının bir sayfası.</summary>
/// <param name="Next">Bir sonraki istekte <c>since</c> olarak gönderilecek imleç.</param>
/// <param name="HasMore">Bu imleçten sonra okunmayı bekleyen değişiklik var mı.</param>
public sealed record ChangeFeedPage<T>(IReadOnlyList<Change<T>> Items, long Next, bool HasMore)
    where T : class;
