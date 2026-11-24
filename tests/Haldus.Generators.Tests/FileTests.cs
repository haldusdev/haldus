namespace Haldus.Generators.Tests;

/// <summary>Dosyalar (<c>MapFile</c>) ve takılabilirler (<c>AttachedTo</c>, <c>Attachable</c>, <c>Commentable</c>, <c>Taggable</c>).</summary>
public sealed class FileTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum NoteStatus { Open, Closed }

            public partial class Order
            {
                public string Customer { get; set; } = "";
            }

            public partial class Invoice
            {
                public string Number { get; set; } = "";
            }

            public partial class Attachment
            {
                public string? Caption { get; set; }
            }

            public partial class Note
            {
                public string Text { get; set; } = "";
            }

            public partial class Line
            {
                public string Text { get; set; } = "";
            }

            public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
        }
        """;

    private static GeneratorRun Run(string body) =>
        GeneratorHarness.Run($$"""
            {{Entities}}

            namespace Shop
            {
                public static class Startup
                {
                    public static void Configure(IServiceCollection services) => services.AddHaldus<AppDbContext>();

                    public static void Map(IEndpointRouteBuilder app)
                    {
                        {{body}}
                    }
                }
            }
            """);

    // ── Dosya ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MapFile_IsAResourceWhoseContentIsUploadedSeparately()
    {
        var run = Run("""app.MapFile<Attachment>(maxBytes: 2048, "image/*", "application/pdf").ConcurrencyChecked();""").AssertCompiles();

        var entity = run.Sources["Shop.Attachment.Entity.g.cs"];
        Assert.Contains("global::Haldus.IHaldusFile", entity);
        Assert.Contains("public string FileName { get; set; } = \"\";", entity);
        Assert.Contains("public global::Haldus.FileStatus Status { get; set; }", entity);
        Assert.Contains("public string? StorageKey { get; set; }", entity);

        // Ad istemcinin yazdığı alandır; boyut, tür, özet ve durum yalnızca yanıtta görünür, deponun anahtarı hiç görünmez.
        var dtos = run.Sources["Shop.Attachment.Dtos.g.cs"];
        var response = Between(dtos, "record AttachmentResponse", "record CreateAttachmentRequest");
        var requests = dtos.Substring(dtos.IndexOf("record CreateAttachmentRequest", StringComparison.Ordinal));
        Assert.Contains("JsonStringEnumConverter<global::Haldus.FileStatus>", response);
        Assert.Contains("public required long Size { get; init; }", response);
        Assert.DoesNotContain("StorageKey", dtos);
        Assert.Contains("FileName", requests);
        Assert.Contains("MaxLengthAttribute(255)", requests);
        Assert.DoesNotContain("Size", requests);
        Assert.DoesNotContain("Checksum", requests);

        var endpoints = run.Sources["Shop.Attachment.Endpoints.g.cs"];
        Assert.Contains("private const long MaxFileBytes = 2048L;", endpoints);
        Assert.Contains("private static readonly string[] FileContentTypes = [\"image/*\", \"application/pdf\"];", endpoints);
        Assert.Contains("""MapPut("/{id:guid}/content", UploadAsync)""", endpoints);
        Assert.Contains("""MapGet("/{id:guid}/content", DownloadAsync)""", endpoints);

        // Yükleme kaydın bir yazmasıdır: sürüm ister; sınırlar ve depo çalışma zamanındadır.
        var upload = Handler(endpoints, "UploadAsync");
        Assert.Contains("PreconditionRequired(messages)", upload);
        Assert.Contains("httpContext, context, entity, MaxFileBytes, FileContentTypes, messages, cancellationToken) is { } rejected", upload);

        // İndirme kaydı kaynağın kendi filtrelerinden geçirerek bulur.
        var download = Handler(endpoints, "DownloadAsync");
        Assert.DoesNotContain("IgnoreQueryFilters", download);
        Assert.Contains("HaldusFiles.SendAsync(httpContext, context, entity, messages, \"Attachment\", id, cancellationToken)", download);

        // Kayıt silindikten sonra içeriği de depodan kaldırılır.
        var delete = Handler(endpoints, "DeleteAsync");
        Assert.True(
            delete.IndexOf("SaveChangesAsync", StringComparison.Ordinal) < delete.IndexOf("HaldusFiles.ReleaseAsync(context, entity, cancellationToken)", StringComparison.Ordinal));

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("global::Haldus.HaldusFileStore.Configure(modelBuilder);", module);
        Assert.Contains("entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(16);", module);
        Assert.DoesNotContain("HaldusFileStore", Run("app.MapCrud<Order>();").AssertCompiles().Sources["HaldusModule.g.cs"]);
    }

    [Fact]
    public void MapFile_DefaultsToTenMegabytesAndAnyType()
    {
        var endpoints = Run("app.MapFile<Attachment>();").AssertCompiles().Sources["Shop.Attachment.Endpoints.g.cs"];

        Assert.Contains("private const long MaxFileBytes = 10485760L;", endpoints);
        Assert.Contains("private static readonly string[] FileContentTypes = [];", endpoints);
    }

    [Theory]
    [InlineData("app.MapFile<Attachment>().Workflow<NoteStatus>();", "HALDUS007", "MapFile cannot be combined with Workflow")]
    [InlineData("app.MapFile<Attachment>().Cloneable();", "HALDUS007", "MapFile cannot be combined with Cloneable")]
    [InlineData("app.MapFile<Attachment>().SoftDeletable();", "HALDUS007", "MapFile cannot be combined with SoftDeletable")]
    [InlineData("app.MapFile<Attachment>(maxBytes: 0);", "HALDUS010", "between 1 byte and 1 GB")]
    [InlineData("app.MapFile<Attachment>(1024, \"png\");", "HALDUS010", "The arguments of MapFile must be constants")]
    [InlineData(
        """
        var files = app.MapFile<Attachment>();
        files.MapAction("content", file => file.Caption = "");
        """,
        "HALDUS007",
        "collides with the 'content' endpoint")]
    public void MapFile_RejectsDeclarationsThatCannotWork(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
    }

    // ── Takılabilirler ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AttachedTo_GeneratesOneOptionalParentPerHostAndOneSetOfEndpointsUnderEach()
    {
        var run = Run("""
            app.MapCrud<Order>();
            app.MapCrud<Invoice>();
            app.MapAppendOnly<Note>().AttachedTo<Order>().AttachedTo<Invoice>();
            """).AssertCompiles();

        var entity = run.Sources["Shop.Note.Entity.g.cs"];
        Assert.Contains("public global::System.Guid? OrderId { get; set; }", entity);
        Assert.Contains("public global::System.Guid? InvoiceId { get; set; }", entity);

        // Hedef istekle seçilmez (adresten gelir); yanıt ikisini de gösterir.
        var dtos = run.Sources["Shop.Note.Dtos.g.cs"];
        Assert.Contains("OrderId", Between(dtos, "record NoteResponse", "record CreateNoteRequest"));
        Assert.DoesNotContain("OrderId", dtos.Substring(dtos.IndexOf("record CreateNoteRequest", StringComparison.Ordinal)));

        // Aynı uçlar her hedefin adresi altında bir kez üretilir; işlem adları hedefin adıyla ayrışır.
        var onOrder = run.Sources["Shop.Note.OnOrder.Endpoints.g.cs"];
        var onInvoice = run.Sources["Shop.Note.OnInvoice.Endpoints.g.cs"];
        Assert.Contains("public static partial class NoteOnOrderEndpoints", onOrder);
        Assert.Contains("""MapGroup("/orders/{orderId:guid}/notes")""", onOrder);
        Assert.Contains("""WithName("OrderCreateNote")""", onOrder);
        Assert.Contains("OrderId = orderId", Handler(onOrder, "CreateAsync"));
        Assert.Contains("item.OrderId == orderId", Handler(onOrder, "ListAsync"));
        Assert.Contains("""MapGroup("/invoices/{invoiceId:guid}/notes")""", onInvoice);
        Assert.Contains("""WithName("InvoiceCreateNote")""", onInvoice);

        // Tek giriş noktası hepsini ortak bir grubun altında eşler.
        var aggregate = run.Sources["Shop.Note.Endpoints.g.cs"];
        Assert.Contains("NoteOnOrderEndpoints.Map(group);", aggregate);
        Assert.Contains("NoteOnInvoiceEndpoints.Map(group);", aggregate);

        // Takılı kayıt, takıldığı kayıt görünürse görünür; veritabanı tam olarak bir hedef ister.
        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("(item.OrderId != null && context.Set<global::Shop.Order>().Any(parent => parent.Id == item.OrderId))", module);
        Assert.Contains("|| (item.InvoiceId != null && context.Set<global::Shop.Invoice>().Any(parent => parent.Id == item.InvoiceId))", module);
        Assert.Contains("HasCheckConstraint(\"CK_Notes_AttachedTo\", \"num_nonnulls(\\\"OrderId\\\", \\\"InvoiceId\\\") = 1\")", module);
        Assert.Contains("HasForeignKey(item => item.OrderId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade)", module);
        Assert.Contains("HasForeignKey(item => item.InvoiceId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade)", module);
        Assert.Contains("HaldusEndpointRegistry.Register<global::Shop.Note>(global::Shop.NoteEndpoints.Map);", module);
    }

    [Fact]
    public void AttachableAndCommentable_AreTheSameDeclarationWrittenOnTheHost()
    {
        var written = Run("""
            app.MapCrud<Order>().Attachable<Attachment>().Commentable<Note>();
            app.MapCrud<Invoice>().Attachable<Attachment>();
            app.MapFile<Attachment>();
            app.MapAppendOnly<Note>().AttachedTo<Order>();
            """).AssertCompiles();

        // İki yönden de yazılan hedef bir kez sayılır.
        Assert.Contains("Shop.Attachment.OnOrder.Endpoints.g.cs", written.Sources.Keys);
        Assert.Contains("Shop.Attachment.OnInvoice.Endpoints.g.cs", written.Sources.Keys);
        Assert.Contains("Shop.Note.OnOrder.Endpoints.g.cs", written.Sources.Keys);
        Assert.Single(written.Sources.Keys, key => key.StartsWith("Shop.Note.On", StringComparison.Ordinal));

        // Dosyanın içerik uçları da her hedefin altındadır.
        Assert.Contains(
            """MapGroup("/invoices/{invoiceId:guid}/attachments")""",
            written.Sources["Shop.Attachment.OnInvoice.Endpoints.g.cs"]);
        Assert.Contains("""MapPut("/{id:guid}/content", UploadAsync)""", written.Sources["Shop.Attachment.OnInvoice.Endpoints.g.cs"]);
    }

    [Fact]
    public void AttachedRecords_CanReplyToEachOtherWithinTheSameHost()
    {
        var run = Run("""
            app.MapCrud<Order>();
            app.MapCrud<Invoice>();
            app.MapAppendOnly<Note>().AttachedTo<Order>().AttachedTo<Invoice>().Hierarchical();
            """).AssertCompiles();

        // Yanıtlanan kayıt aynı hedefe takılı olmalıdır.
        var create = Handler(run.Sources["Shop.Note.OnOrder.Endpoints.g.cs"], "CreateAsync");
        Assert.Contains("target.Id == parentIdReference && target.OrderId == orderId", create);
    }

    [Fact]
    public void Taggable_IsAnAssociationMappedWithTheResource()
    {
        var run = Run("""
            app.MapLookup<Line>();
            app.MapCrud<Order>().Taggable<Line>();
            """).AssertCompiles();

        var endpoints = run.Sources["Shop.OrderLine.Endpoints.g.cs"];
        Assert.Contains("""MapPut("/orders/{orderId:guid}/lines/{lineCode}", LinkAsync)""", endpoints);
        Assert.Contains("""MapPut("/orders/{orderId:guid}/lines", ReplaceLinesAsync)""", endpoints);

        // Ayrı bir MapAssociation çağrısı yoktur; bağın uçları kaynağın uçlarıyla birlikte eşlenir.
        Assert.Contains("OrderLineEndpoints.Map(endpoints);", run.Sources["Shop.Order.Endpoints.g.cs"]);
    }

    [Theory]
    [InlineData("app.MapCrud<Order>(); app.MapCrud<Invoice>(); app.MapCrud<Note>().AttachedTo<Order>().Under<Invoice>();", "HALDUS007", "it is already placed under a parent with Under<T>()")]
    [InlineData("app.MapCrud<Order>(); app.MapCrud<Note>().AttachedTo<Order>().Orderable();", "HALDUS007", "it is Orderable")]
    [InlineData("app.MapCrud<Order>(); app.MapCrud<Note>().AttachedTo<Order>().Workflow<NoteStatus>();", "HALDUS007", "it has a state machine")]
    [InlineData("app.MapCrud<Order>(); app.MapCrud<Note>().AttachedTo<Order>().ChangeFeed();", "HALDUS007", "it has a ChangeFeed")]
    [InlineData("app.MapCrud<Order>(); app.MapCrud<Note>().AttachedTo<Order>().NaturalKey(note => note.Text);", "HALDUS007", "it has a NaturalKey")]
    [InlineData("app.MapCrud<Order>(); app.MapStore<Note>().AttachedTo<Order>();", "HALDUS007", "it must be mapped with MapCrud, MapAppendOnly or MapFile")]
    [InlineData("app.MapCrud<Order>().Attachable<Note>(); app.MapCrud<Note>();", "HALDUS007", "Attachable<Note>() needs a file resource")]
    [InlineData("app.MapCrud<Order>().Commentable<Note>();", "HALDUS009", "Note")]
    [InlineData("app.MapCrud<Order>(); app.MapCrud<Note>().AttachedTo<Order>(); app.MapCrud<Line>().Under<Note>();", "HALDUS007", "has no single address")]
    [InlineData("app.MapCrud<Order>(); app.MapCrud<Note>().AttachedTo<Order>().MapDetail<Line>();", "HALDUS007", "so it cannot have details")]
    [InlineData("app.MapCrud<Order>(); app.MapCrud<Invoice>(); app.MapCrud<Note>().AttachedTo<Order>(); app.MapAssociation<Invoice, Note>();", "HALDUS007", "it cannot be an end of an association")]
    [InlineData(
        """
        app.MapCrud<Order>();
        var notes = app.MapCrud<Note>().AttachedTo<Order>();
        notes.MapAction("pin", note => note.Text = "");
        """,
        "HALDUS007",
        "MapAction cannot be declared on 'Note'")]
    public void AttachedTo_RejectsDeclarationsThatNeedASingleAddress(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void AttachedTo_KeepsTenantOwnedRecordsWhenAGlobalHostIsDeleted()
    {
        // Not kiracıya ait, sipariş genel: siparişi silen kişi başka kiracıların notlarını silememelidir.
        var own = Run("""
            app.MapCrud<Order>();
            app.MapCrud<Note>().MultiTenant().AttachedTo<Order>();
            """).AssertCompiles();

        Assert.Contains(
            "HasForeignKey(item => item.OrderId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict)",
            own.Sources["HaldusModule.g.cs"]);

        // Hedeflerden biri kiracıya aitse notların bir kısmı da öyledir; genel hedef yine onları götüremez.
        var mixed = Run("""
            app.MapCrud<Order>();
            app.MapCrud<Invoice>().MultiTenant();
            app.MapCrud<Note>().AttachedTo<Order>().AttachedTo<Invoice>();
            """).AssertCompiles().Sources["HaldusModule.g.cs"];

        Assert.Contains("HasForeignKey(item => item.OrderId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict)", mixed);
        Assert.Contains("HasForeignKey(item => item.InvoiceId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade)", mixed);
    }

    [Fact]
    public void FilesSnapshot()
    {
        var run = Run("""
            app.MapCrud<Order>().Attachable<Attachment>().Commentable<Note>();
            app.MapCrud<Invoice>().Attachable<Attachment>();
            app.MapFile<Attachment>(maxBytes: 1048576, "image/*");
            app.MapAppendOnly<Note>();
            """).AssertCompiles();

        Snapshot.Verify(run);
    }

    /// <summary>Bir handler metodunun gövdesi. Adın çağrıldığı yerler değil tanımlandığı yer aranır.</summary>
    private static string Handler(string endpoints, string name)
    {
        var from = endpoints.IndexOf($" {name}(\n", StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{name}' is not declared");

        var to = endpoints.IndexOf("\n    }\n", from, StringComparison.Ordinal);
        return endpoints.Substring(from, to - from);
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);

        Assert.True(from >= 0 && to > from, $"'{start}' … '{end}' not found");
        return text.Substring(from, to - from);
    }
}
