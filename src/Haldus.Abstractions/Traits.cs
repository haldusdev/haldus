namespace Haldus;

// Trait arayüzleri: generator, zincirde bildirilen her trait için entity'ye ilgili arayüzü ve alanları
// ekler. Çalışma zamanı davranışları (interceptor, sorgu filtresi) entity'yi bu arayüzlerle tanır.

/// <summary>Kayıt bir kiracıya aittir; sorgular kiracıya göre süzülür.</summary>
public interface IMultiTenant
{
    Guid TenantId { get; set; }
}

/// <summary>
/// Kayıt, onu oluşturan kullanıcıya aittir; sorgular geçerli kullanıcıya göre süzülür. Kiracı sınırının
/// içinde ikinci bir sınırdır: aynı kiracıdaki başka bir kullanıcı da kaydı göremez.
/// </summary>
public interface IOwned
{
    string OwnerId { get; set; }
}

/// <summary>Oluşturulma ve son değiştirilme zamanı tutulur.</summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }

    DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>Oluşturan ve son değiştiren kullanıcı da tutulur.</summary>
public interface IUserAuditable : IAuditable
{
    string? CreatedBy { get; set; }

    string? UpdatedBy { get; set; }
}

/// <summary>Kayıt silindiğinde fiziksel olarak kaldırılmaz, silinme zamanı işaretlenir.</summary>
public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Güncelleme ve silme, kaydın okunduğundan beri değişmediği doğrulanarak yapılır.</summary>
public interface IConcurrencyChecked
{
    uint Version { get; set; }
}

/// <summary>Kayıt pasifleştirilebilir: görünür ve referanslı kalır ama listelerde varsayılan olarak gizlenir.</summary>
public interface IArchivable
{
    DateTimeOffset? ArchivedAt { get; set; }
}

/// <summary>
/// Değeri diğer alanlardan hesaplanan alanları olan entity. Haldus, oluşturma ve güncelleme uçlarında
/// istek uygulandıktan ve detay satırları yüklendikten sonra, kaydetmeden hemen önce <see cref="Compute"/>
/// metodunu çağırır. Hesaplanan alanı herkese açık setter'sız yazarsan yalnızca yanıtta görünür.
/// </summary>
public interface IComputed
{
    void Compute();
}

/// <summary>Geçerli isteğin kiracısını verir.</summary>
public interface ITenantProvider
{
    Guid? TenantId { get; }
}

/// <summary>Geçerli isteğin kullanıcısını verir.</summary>
public interface ICurrentUser
{
    string? UserId { get; }
}

/// <summary>Referans verilen kayıt silindiğinde, ona referans veren kayıtlara ne olacağı.</summary>
public enum OnDelete
{
    /// <summary>Referans veren kayıt varken silme reddedilir.</summary>
    Restrict,

    /// <summary>Referans veren kayıtlar da silinir.</summary>
    Cascade,

    /// <summary>Referans boşaltılır; yalnızca isteğe bağlı referanslarda kullanılabilir.</summary>
    SetNull,
}

/// <summary><c>.Without()</c> ve <c>.Only()</c> ile seçilebilen standart işlemler.</summary>
public enum CrudOperation
{
    List,
    Get,
    Create,
    Update,
    Patch,
    Delete,
}
