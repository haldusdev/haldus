namespace Haldus;

/// <summary>Bir dosya kaydının (<c>MapFile</c>) içeriğinin durumu.</summary>
public enum FileStatus
{
    /// <summary>Kayıt oluşturuldu, içeriği henüz yüklenmedi.</summary>
    Pending,

    /// <summary>İçerik yüklendi ve indirilebilir.</summary>
    Uploaded,
}

/// <summary>
/// Asıl içeriği bayt akışı olan kayıt (<c>MapFile</c>). <c>FileName</c> dışındaki alanları Haldus yönetir: içerik
/// yüklenirken yazılırlar, istekle verilemezler.
/// </summary>
public interface IHaldusFile
{
    /// <summary>Dosyanın, indirilirken önerilen adı.</summary>
    string FileName { get; set; }

    /// <summary>İçeriğin, yüklenirken bildirilen türü.</summary>
    string? ContentType { get; set; }

    /// <summary>İçeriğin bayt cinsinden boyutu.</summary>
    long Size { get; set; }

    /// <summary>İçeriğin SHA-256 özeti (onaltılık).</summary>
    string? Checksum { get; set; }

    FileStatus Status { get; set; }

    DateTimeOffset? UploadedAt { get; set; }

    /// <summary>İçeriğin depodaki anahtarı; dışarıya gösterilmez.</summary>
    string? StorageKey { get; set; }
}