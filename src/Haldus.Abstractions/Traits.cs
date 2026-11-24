namespace Haldus;

// Trait arayüzleri: generator, zincirde bildirilen her trait için entity'ye ilgili arayüzü ve alanları
// ekler. Çalışma zamanı davranışları (interceptor, sorgu filtresi) entity'yi bu arayüzlerle tanır.

/// <summary>Kayıt bir kiracıya aittir; sorgular kiracıya göre süzülür.</summary>
public interface IMultiTenant
{
    Guid TenantId { get; set; }
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
