namespace Haldus.Sample.Shop.Features.Content;

public static class ContentEndpoints
{
    public static void MapContent(this IEndpointRouteBuilder app)
    {
        // Makalenin her yazması saklanır: GET /articles/{id}/revisions sürümleri listeler,
        // GET /articles/{id}/revisions/{revisionId} o andaki halini döndürür,
        // POST /articles/{id}/revisions/{revisionId}/rollback alanları ve bölümleri o sürüme geri yükler.
        // Değişiklikler GET /articles/changes?since={imleç} ile izlenir; silinen makaleler de bildirilir.
        // Makale taslak olarak yazılır; POST /articles/{id}/publish o andaki halini yayına alır ve yayındaki hal
        // GET /articles/published ve GET /articles/published/{id} ile okunur. Taslak sonradan değişse de yayındaki
        // hal bir sonraki yayına kadar aynı kalır.
        var articles = app.MapCrud<Article>()
            .Auditable()
            .Revisioned()
            .ChangeFeed()
            .Publishable()
            .NaturalKey(article => article.Slug);

        articles.MapDetail<ArticleSection>();

        // Bölümleri bir aksiyon değiştirse de makale değişmiş sayılır: sürümü saklanır, akışta görünür.
        articles.MapAction("clear-sections", article => article.ArticleSections.Clear());
    }
}