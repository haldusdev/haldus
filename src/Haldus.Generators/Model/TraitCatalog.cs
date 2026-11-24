namespace Haldus.Generators.Model;

/// <summary>Her trait'in entity'ye eklediği arayüz ve alanlar.</summary>
internal static class TraitCatalog
{
    public static readonly TraitDefinition[] All =
    [
        new(Traits.MultiTenant, "global::Haldus.IMultiTenant",
        [
            new("TenantId", "global::System.Guid", InResponse: false),
        ]),
        new(Traits.Auditable, "global::Haldus.IAuditable",
        [
            new("CreatedAt", "global::System.DateTimeOffset", InResponse: true),
            new("UpdatedAt", "global::System.DateTimeOffset?", InResponse: true),
        ]),
        new(Traits.AuditUser, "global::Haldus.IUserAuditable",
        [
            new("CreatedBy", "string?", InResponse: true),
            new("UpdatedBy", "string?", InResponse: true),
        ]),
        new(Traits.SoftDeletable, "global::Haldus.ISoftDeletable",
        [
            new("DeletedAt", "global::System.DateTimeOffset?", InResponse: true),
        ]),

        // Sürüm yanıt gövdesinde değil ETag başlığında taşınır.
        new(Traits.ConcurrencyChecked, "global::Haldus.IConcurrencyChecked",
        [
            new("Version", "uint", InResponse: false),
        ]),
        new(Traits.Archivable, "global::Haldus.IArchivable",
        [
            new("ArchivedAt", "global::System.DateTimeOffset?", InResponse: true),
        ]),
    ];

    public static IEnumerable<TraitDefinition> For(Traits traits) =>
        All.Where(definition => (traits & definition.Trait) == definition.Trait);

    public static IEnumerable<TraitMember> ResponseMembers(Traits traits) =>
        For(traits).SelectMany(definition => definition.Members).Where(member => member.InResponse);
}

internal sealed record TraitDefinition(Traits Trait, string Interface, TraitMember[] Members);

internal sealed record TraitMember(string Name, string Type, bool InResponse);
