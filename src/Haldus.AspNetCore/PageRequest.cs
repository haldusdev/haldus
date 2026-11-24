using Microsoft.AspNetCore.Mvc;

namespace Haldus;

/// <summary>Liste endpoint'lerinin sayfalama parametreleri.</summary>
public readonly record struct PageRequest(
    [property: FromQuery(Name = "page")] int? Page,
    [property: FromQuery(Name = "pageSize")] int? PageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    // Skip hesabı int sınırını aşmasın diye sayfa numarası da sınırlanır.
    public const int MaxPage = int.MaxValue / MaxPageSize;

    public int PageOrDefault => Page is > 0 ? Math.Min(Page.Value, MaxPage) : 1;

    public int PageSizeOrDefault => PageSize is > 0 ? Math.Min(PageSize.Value, MaxPageSize) : DefaultPageSize;

    public int Skip => (PageOrDefault - 1) * PageSizeOrDefault;
}
