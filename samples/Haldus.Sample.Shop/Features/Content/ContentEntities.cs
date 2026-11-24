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

/// <summary>Makalenin bölümü; makalenin anlık görüntüsüne girer.</summary>
public partial class ArticleSection
{
    [Required]
    [MaxLength(200)]
    public string Heading { get; set; } = "";

    [MaxLength(10_000)]
    public string? Text { get; set; }
}