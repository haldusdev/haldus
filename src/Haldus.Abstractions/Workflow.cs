using System.Collections;
using System.Runtime.CompilerServices;

namespace Haldus;

// Durum makinesi (.Workflow<TStatus>()) sözleşmeleri. Bir geçişin durum kontrolünü, kaydetmeyi ve yanıtı
// Haldus üretir; iş mantığı aşağıdaki üç yoldan biriyle verilir: istek tipinin kendisi, bir handler sınıfı
// veya Map çağrısına verilen lambda.

/// <summary>
/// Belgenin (<c>.Document()</c>) sabit durumları: taslak düzenlenir ve silinir, gönderilen belge kilitlidir, iptal
/// edilen belge yalnızca düzeltilebilir (yeni bir taslak doğar).
/// </summary>
public enum DocumentStatus
{
    Draft,

    Submitted,

    Cancelled,
}

/// <summary>
/// Entity üzerindeki etkisini kendisi uygulayan geçiş isteği. Bağımlılık gerektirmeyen geçişler için:
/// <c>record CancelOrder(string Reason) : ITransition&lt;Order&gt;</c>.
/// </summary>
public interface ITransition<in TEntity>
    where TEntity : class
{
    void Apply(TEntity entity);
}

/// <summary>
/// Bir geçişin iş mantığı. Haldus bu arayüzü uygulayan sınıfları derleme zamanında bulur ve bağımlılık
/// enjeksiyonuna kendisi kaydeder. Entity yüklenmiş ve durumu doğrulanmış olarak gelir; handler onu
/// değiştirir, kaydetmez.
/// </summary>
public interface ITransitionHandler<in TEntity, in TRequest>
    where TEntity : class
{
    Task HandleAsync(TEntity entity, TRequest request, CancellationToken cancellationToken);
}

// Entity aksiyonları (orders.MapAction(...)): bir kayıt üzerinde çalışan ama durumunu değiştirmeyen işlemler
// (parola sıfırlama, yeniden gönderme, giriş/çıkış). Kaydı bulmayı, sürüm kontrolünü ve kaydetmeyi Haldus
// üretir; iş mantığı geçişlerdeki gibi istek tipinden, bir handler sınıfından veya lambda'dan gelir.

/// <summary>Entity üzerindeki etkisini kendisi uygulayan aksiyon isteği.</summary>
public interface IEntityAction<in TEntity>
    where TEntity : class
{
    void Apply(TEntity entity);
}

/// <summary>
/// Bir entity aksiyonunun iş mantığı; yanıt olarak kaydın güncel hali döner. Haldus bu arayüzü uygulayan
/// sınıfları bulur ve bağımlılık enjeksiyonuna kendisi kaydeder. Handler entity'yi değiştirir, kaydetmez.
/// </summary>
public interface IEntityActionHandler<in TEntity, in TRequest>
    where TEntity : class
{
    Task HandleAsync(TEntity entity, TRequest request, CancellationToken cancellationToken);
}

/// <summary>Kendi sonucunu döndüren entity aksiyonunun iş mantığı.</summary>
public interface IEntityActionHandler<in TEntity, in TRequest, TResponse>
    where TEntity : class
{
    Task<TResponse> HandleAsync(TEntity entity, TRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Bir geçişin veya entity aksiyonunun iş mantığı işlemi reddettiğinde fırlatılır (ör. satırı olmayan sipariş
/// verilemez). Hiçbir şey kaydedilmez ve mesaj istemciye 422 yanıtının açıklaması olarak döner; bu yüzden
/// metni son kullanıcıya gösterilecek biçimde ve isteğin dilinde üretmek fırlatanın işidir.
/// </summary>
public sealed class TransitionRejectedException : Exception
{
    public TransitionRejectedException()
    {
    }

    public TransitionRejectedException(string message)
        : base(message)
    {
    }

    public TransitionRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Bir geçişin çıkabileceği durumlar. Tek bir durumdan veya bir koleksiyon ifadesinden örtük olarak
/// oluşur: <c>from: OrderStatus.Draft</c> ya da <c>from: [OrderStatus.Draft, OrderStatus.Placed]</c>.
/// Değerler çalışma zamanında kullanılmaz; generator onları derleme zamanında okur.
/// </summary>
[CollectionBuilder(typeof(StateSet), nameof(Create))]
public readonly struct StateSet : IEnumerable<Enum>
{
    private readonly Enum[]? _states;

    private StateSet(Enum[] states) => _states = states;

    public static StateSet Create(ReadOnlySpan<Enum> states) => new(states.ToArray());

    public static implicit operator StateSet(Enum state) => new([state]);

    public IEnumerator<Enum> GetEnumerator() => ((IEnumerable<Enum>)(_states ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
