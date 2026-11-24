namespace Haldus.Generators.Model;

/// <summary>Her trait'in entity'ye eklediği arayüz ve alanlar.</summary>
internal static class TraitCatalog
{
    private const string DateTimeOffset = "global::System.DateTimeOffset";

    public static readonly TraitDefinition[] All =
    [
        new(Traits.MultiTenant, "global::Haldus.IMultiTenant",
        [
            new("TenantId", "global::System.Guid", InResponse: false, Search: null),
        ]),
        new(Traits.Owned, "global::Haldus.IOwned",
        [
            // Sahip isteği yapanın kendisidir; yanıtta yinelenmez.
            new("OwnerId", "string", InResponse: false, Search: null),
        ]),
        new(Traits.Auditable, "global::Haldus.IAuditable",
        [
            new("CreatedAt", DateTimeOffset, InResponse: true, new("CreatedAt", SearchKind.Range, DateTimeOffset, IsNullable: false)),
            new("UpdatedAt", DateTimeOffset + "?", InResponse: true, new("UpdatedAt", SearchKind.Range, DateTimeOffset, IsNullable: true)),
        ]),
        new(Traits.AuditUser, "global::Haldus.IUserAuditable",
        [
            new("CreatedBy", "string?", InResponse: true, new("CreatedBy", SearchKind.String, "string", IsNullable: true)),
            new("UpdatedBy", "string?", InResponse: true, new("UpdatedBy", SearchKind.String, "string", IsNullable: true)),
        ]),
        new(Traits.SoftDeletable, "global::Haldus.ISoftDeletable",
        [
            new("DeletedAt", DateTimeOffset + "?", InResponse: true, new("DeletedAt", SearchKind.Range, DateTimeOffset, IsNullable: true)),
        ]),

        // Sürüm yanıt gövdesinde değil ETag başlığında taşınır.
        new(Traits.ConcurrencyChecked, "global::Haldus.IConcurrencyChecked",
        [
            new("Version", "uint", InResponse: false, Search: null),
        ]),
        new(Traits.Archivable, "global::Haldus.IArchivable",
        [
            new("ArchivedAt", DateTimeOffset + "?", InResponse: true, new("ArchivedAt", SearchKind.Range, DateTimeOffset, IsNullable: true)),
        ]),

        // Konumu yalnızca taşıma ucu değiştirir; istek gövdesinden yazılamaz.
        new(Traits.Orderable, "global::Haldus.IOrderable",
        [
            new("Position", "int", InResponse: true, new("Position", SearchKind.Range, "int", IsNullable: false)),
        ]),

        // Onay alanlarını yalnızca onay uçları ve içeriği değiştiren yazmalar değiştirir.
        new(Traits.Approvable, "global::Haldus.IApprovable",
        [
            new("ApprovalStatus", ApprovalStatus, InResponse: true, new("ApprovalStatus", SearchKind.Equality, ApprovalStatus, IsNullable: false),
                ResponseAttribute: $"global::System.Text.Json.Serialization.JsonConverter(typeof(global::System.Text.Json.Serialization.JsonStringEnumConverter<{ApprovalStatus}>))"),
            new("ApprovalLevel", "int", InResponse: true, Search: null),
            new("RequestedBy", "string?", InResponse: true, new("RequestedBy", SearchKind.String, "string", IsNullable: true)),
            new("RequestedAt", DateTimeOffset, InResponse: true, new("RequestedAt", SearchKind.Range, DateTimeOffset, IsNullable: false)),
            new("DecidedBy", "string?", InResponse: true, new("DecidedBy", SearchKind.String, "string", IsNullable: true)),
            new("DecidedAt", DateTimeOffset + "?", InResponse: true, new("DecidedAt", SearchKind.Range, DateTimeOffset, IsNullable: true)),
            new("DecisionNote", "string?", InResponse: true, Search: null),
        ]),

        // Çanta yanıtta görünür; isteklerdeki yeri ve yazılma kuralları ayrıca üretilir.
        new(Traits.Metadata, "global::Haldus.IHasMetadata",
        [
            new("Metadata", MetadataType, InResponse: true, Search: null),
        ]),
    ];

    public const string MetadataType = "global::System.Collections.Generic.Dictionary<string, string>";

    public const string ApprovalStatus = "global::Haldus.ApprovalStatus";

    /// <summary>Üretilen trait alanının başlangıç değeri; null olamayan referans tipleri boş başlar.</summary>
    public static string Initializer(string type) => type switch
    {
        "string" => " = \"\";",
        MetadataType => " = new();",
        _ => string.Empty,
    };

    public static IEnumerable<TraitDefinition> For(Traits traits) =>
        All.Where(definition => (traits & definition.Trait) == definition.Trait);

    public static IEnumerable<TraitMember> ResponseMembers(Traits traits) =>
        For(traits).SelectMany(definition => definition.Members).Where(member => member.InResponse);
}

internal sealed record TraitDefinition(Traits Trait, string Interface, TraitMember[] Members);

/// <param name="Search">Alan aranabiliyorsa arama modeli; kiracı ve sürüm gibi iç alanlarda <c>null</c>.</param>
/// <param name="ResponseAttribute">Yanıt DTO'sundaki özelliğe eklenecek attribute (ör. enum'u metin olarak yazdıran dönüştürücü).</param>
internal sealed record TraitMember(string Name, string Type, bool InResponse, SearchField? Search, string? ResponseAttribute = null);
