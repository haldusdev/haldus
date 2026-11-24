namespace Haldus;

/// <summary>Liste endpoint'lerinin döndürdüğü sayfalanmış sonuç.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);
