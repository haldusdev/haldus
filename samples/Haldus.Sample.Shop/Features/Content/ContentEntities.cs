using System.ComponentModel.DataAnnotations;

namespace Haldus.Sample.Shop.Features.Content;

/// <summary>Makale. Her yazması saklanır ve değişiklikleri bir akıştan izlenir.</summary>
public partial class Article : IComputed
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    /// <summary>Makalenin adresteki adı; taslaklar arasında benzersizdir.</summary>
    [Required]
    [MaxLength(200)]
    public string Slug { get; set; } = "";

    [MaxLength(10_000)]
    public string? Body { get; set; }

    public int SectionCount { get; private set; }

    public void Compute() => SectionCount = ArticleSections.Count;
}

/// <summary>Dosya eki. Adı, boyutu, türü ve durumu Haldus'a aittir; burada yalnızca kendi alanı yazılır.</summary>
public partial class Attachment
{
    [MaxLength(500)]
    public string? Caption { get; set; }
}

/// <summary>Bir kayda yazılan yorum.</summary>
public partial class Comment
{
    [Required]
    [MaxLength(2000)]
    public string Text { get; set; } = "";
}

/// <summary>Makalenin bölümü; makalenin anlık görüntüsüne girer.</summary>
public partial class ArticleSection
{
    [Required]
    [MaxLength(200)]
    public string Heading { get; set; } = "";

    [MaxLength(10_000)]
    public string? Text { get; set; }
}