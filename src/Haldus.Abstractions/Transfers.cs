namespace Haldus;

/// <summary>
/// Bir kaynağın kayıtlarını dosyaya döken iş (<c>MapJob&lt;T&gt;().Exports&lt;TResource&gt;()</c>). Alanları Haldus
/// üretir; <see cref="Format"/> işin girdisidir, geri kalanı çıktısıdır.
/// </summary>
public interface IHaldusExportJob
{
    /// <summary>Dosyanın biçimi: <c>csv</c> (varsayılan) veya <c>json</c>.</summary>
    string? Format { get; set; }

    /// <summary>Dışa aktarılan kayıt sayısı.</summary>
    int Rows { get; set; }

    /// <summary>Üretilen dosyanın bayt cinsinden boyutu.</summary>
    long Size { get; set; }

    string? FileName { get; set; }

    /// <summary>Dosyanın depodaki anahtarı; dışarıya gösterilmez.</summary>
    string? StorageKey { get; set; }
}

/// <summary>
/// Bir kaynağa toplu kayıt ekleyen iş (<c>MapJob&lt;T&gt;().Imports&lt;TResource&gt;()</c>). <see cref="Format"/> ve
/// <see cref="Content"/> işin girdisidir; içerik yanıtta gösterilmez ve iş bitince boşaltılır.
/// </summary>
public interface IHaldusImportJob
{
    /// <summary>İçeriğin biçimi: <c>csv</c> (varsayılan) veya <c>json</c>.</summary>
    string? Format { get; set; }

    /// <summary>İçe aktarılacak kayıtlar: başlık satırı olan CSV ya da bir JSON dizisi.</summary>
    string Content { get; set; }

    /// <summary>Oluşturulan kayıt sayısı. Bir satır bile reddedilirse hiçbiri oluşturulmaz ve sıfırdır.</summary>
    int Imported { get; set; }

    /// <summary>Reddedilen satır sayısı.</summary>
    int Failed { get; set; }

    /// <summary>Reddedilen satırların nedenleri (ilk elli satır).</summary>
    string? Errors { get; set; }
}