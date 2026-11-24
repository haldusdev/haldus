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

            // Yayın ileri bir zamana kurulabilir: POST /articles/{id}/schedule  { "publishAt": "…" }
            // Zamanı gelen taslağı bakım servisi yayınlar; DELETE /articles/{id}/schedule zamanlamayı kaldırır.
            .ScheduledPublishing()
            .NaturalKey(article => article.Slug)

            // Başlık ve gövde başka dillere çevrilebilir: PUT /articles/{id}/translations/tr  { "title": "…" }
            // GET /articles/{id}?locale=tr çevrilmiş alanları o dilde döndürür; yayındaki hal de çevirileri taşır.
            .Translatable(article => article.Title, article => article.Body)

            // Makaleye dosya eklenebilir, yorum yazılabilir ve makale etiketlenebilir; üçü de başka kaynaklarla paylaşılır.
            .Attachable<Attachment>()
            .Commentable<Comment>()
            .Taggable<Label>();


        articles.MapDetail<ArticleSection>();

        // Bölümleri bir aksiyon değiştirse de makale değişmiş sayılır: sürümü saklanır, akışta görünür.
        articles.MapAction("clear-sections", article => article.ArticleSections.Clear());

        // Dosya: kayıt POST ile oluşturulur, içeriği PUT /{id}/content ile yüklenir, GET /{id}/content ile indirilir.
        // En çok 1 MB; yalnızca resim, PDF ve düz metin kabul edilir. Birkaç kaynağa takılabildiği için takıldığı
        // kaynağın adresi altında sunulur: /articles/{articleId}/attachments, /suppliers/{supplierId}/attachments
        app.MapFile<Attachment>(maxBytes: 1024 * 1024, "image/*", "application/pdf", "text/plain");

        // Yorum yalnızca eklenir; takıldığı kayıt görünürse görünür ve onunla birlikte silinir. Yorumlar birbirine
        // yanıt olabilir: yanıtlanan yorum aynı kayda takılı olmalıdır.
        app.MapAppendOnly<Comment>()
            .Auditable()
            .Hierarchical();

        // Etiket: MapAssociation<Article, Label>() ile aynıdır (PUT /articles/{id}/labels etiketleri topluca değiştirir).
        app.MapLookup<Label>();
    }
}