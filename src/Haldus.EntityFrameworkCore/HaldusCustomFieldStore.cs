using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Haldus;

/// <summary>Bir kaynağın, bir kiracının tanımladığı özel alanı.</summary>
public sealed class HaldusCustomFieldDefinition
{
    public long Id { get; set; }

    /// <summary>Kaynağın tipinin tam adı.</summary>
    public string Resource { get; set; } = "";

    /// <summary>Tanımın sahibi kiracı; kiracıya bağlı olmayan kaynakta <see cref="Guid.Empty"/>.</summary>
    public Guid TenantId { get; set; }

    public string Key { get; set; } = "";

    public string Label { get; set; } = "";

    public CustomFieldType Type { get; set; }

    public bool Required { get; set; }

    public string[] Options { get; set; } = [];
}

/// <summary>Özel alan tanımlarının tablosu ve değer çantasının kolonu.</summary>
public static class HaldusCustomFieldStore
{
    /// <summary>Tanım tablosunu modele ekler; yalnızca özel alanları olan bir kaynak varsa çağrılır.</summary>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HaldusCustomFieldDefinition>(entity =>
        {
            entity.ToTable("HaldusCustomFieldDefinitions");
            entity.HasKey(definition => definition.Id);
            entity.Property(definition => definition.Resource).HasMaxLength(512);
            entity.Property(definition => definition.Key).HasMaxLength(40);
            entity.Property(definition => definition.Label).HasMaxLength(200);
            entity.Property(definition => definition.Type).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(definition => new { definition.Resource, definition.TenantId, definition.Key }).IsUnique();
        });
    }

    /// <summary>Değer çantasını jsonb kolonunda saklar.</summary>
    public static PropertyBuilder<Dictionary<string, JsonElement>> ConfigureValues(PropertyBuilder<Dictionary<string, JsonElement>> property)
    {
        ArgumentNullException.ThrowIfNull(property);

        return property
            .HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, (JsonSerializerOptions?)null) ?? new Dictionary<string, JsonElement>(),
                new ValueComparer<Dictionary<string, JsonElement>>(
                    (left, right) => JsonSerializer.Serialize(left, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(right, (JsonSerializerOptions?)null),
                    value => value.Count,
                    value => value.ToDictionary(entry => entry.Key, entry => entry.Value.Clone(), StringComparer.Ordinal)))
            .HasColumnType("jsonb");
    }

    /// <summary>Kaynağın, geçerli kiracıya ait tanımları.</summary>
    public static IQueryable<HaldusCustomFieldDefinition> Definitions<TEntity>(DbContext context)
        where TEntity : class, IHasCustomFields
    {
        ArgumentNullException.ThrowIfNull(context);

        var resource = typeof(TEntity).FullName ?? typeof(TEntity).Name;
        var tenantId = TenantOf<TEntity>(context);

        return context.Set<HaldusCustomFieldDefinition>()
            .Where(definition => definition.Resource == resource && definition.TenantId == tenantId);
    }

    /// <summary>Tanımların sahibi: kiracıya bağlı kaynakta geçerli kiracı, değilse herkes için tek bir küme.</summary>
    public static Guid TenantOf<TEntity>(DbContext context) =>
        typeof(IMultiTenant).IsAssignableFrom(typeof(TEntity)) ? HaldusScope.TenantId(context) ?? Guid.Empty : Guid.Empty;
}