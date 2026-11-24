namespace Haldus;

// Uzun süren işler (app.MapJob<TJob>()). İş, girdisini ve çıktısını taşıyan bir kayıttır: istek onu
// oluşturur ve hemen döner, arka plandaki yürütücü çalıştırır, istemci durumunu aynı kayıttan izler.

/// <summary>Bir işin bulunduğu aşama.</summary>
public enum JobStatus
{
    /// <summary>Oluşturuldu, sırasını bekliyor.</summary>
    Queued,

    Running,

    Succeeded,

    Failed,

    /// <summary>Başlamadan veya çalışırken iptal edildi.</summary>
    Cancelled,
}

/// <summary>
/// Haldus'un her iş entity'sine eklediği alanlar. Yürütücü işi bu arayüz üzerinden yönetir; alanlar istek
/// gövdesinden yazılamaz.
/// </summary>
public interface IHaldusJob
{
    Guid Id { get; set; }

    JobStatus Status { get; set; }

    /// <summary>İlerleme yüzdesi (0–100); işin mantığı <see cref="JobContext.ReportProgressAsync"/> ile bildirir.</summary>
    int Progress { get; set; }

    /// <summary>İş başarısız olduysa, mantığının bildirdiği neden.</summary>
    string? Error { get; set; }

    DateTimeOffset CreatedAt { get; set; }

    DateTimeOffset? StartedAt { get; set; }

    DateTimeOffset? CompletedAt { get; set; }

    /// <summary>İşin kaç kez çalıştırılmaya başlandığı; yarıda kalan iş yeniden denenirken artar.</summary>
    int Attempts { get; set; }

    /// <summary>Çalışan iş için iptal istendi mi; yürütücü bunu görünce işin iptal belirtecini tetikler.</summary>
    bool CancelRequested { get; set; }

    /// <summary>
    /// İşi oluşturan isteğin dili. İş bu dille çalışır; böylece mantığının ürettiği metinler (ör. başarısızlık
    /// nedeni) sunucunun değil, işi isteyen kişinin dilinde olur.
    /// </summary>
    string? Culture { get; set; }
}

/// <summary>
/// Bir işin mantığı. Haldus bu arayüzü uygulayan sınıfları derleme zamanında bulur ve bağımlılık
/// enjeksiyonuna kendisi kaydeder. İş, isteği yapan kiracı ve kullanıcının kimliğiyle çalışır. Çıktı,
/// işin kendi alanlarına yazılır; iş başarıyla bitince Haldus kaydeder.
/// </summary>
public interface IJobHandler<in TJob>
    where TJob : class
{
    Task HandleAsync(TJob job, JobContext context, CancellationToken cancellationToken);
}

/// <summary>Çalışan bir işin, yürütücüyle konuştuğu yer.</summary>
public abstract class JobContext
{
    /// <summary>İşin çalıştığı kapsamın servisleri.</summary>
    public abstract IServiceProvider Services { get; }

    /// <summary>İlerlemeyi hemen kaydeder; istemci işi sorguladığında görür.</summary>
    /// <param name="percent">0 ile 100 arasında yüzde.</param>
    public abstract Task ReportProgressAsync(int percent, CancellationToken cancellationToken = default);
}

/// <summary>
/// İşin mantığı işi başarısız saydığında fırlatılır. Mesaj işin <c>Error</c> alanına yazılır ve istemciye
/// gösterilir; bu yüzden metni son kullanıcıya uygun biçimde üretmek fırlatanın işidir. Başka türden bir
/// hata da işi başarısız yapar, ama ayrıntısı istemciye gösterilmez, yalnızca günlüğe yazılır.
/// </summary>
public sealed class JobFailedException : Exception
{
    public JobFailedException()
    {
    }

    public JobFailedException(string message)
        : base(message)
    {
    }

    public JobFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
