using System.Text;
using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;

namespace Haldus.Generators.Emit;

/// <summary>Modülün (assembly'nin) kayıt kodunu ve bağlamın DbSet özelliklerini üretir.</summary>
internal static class ModuleEmitter
{
    public static string EmitModule(IReadOnlyList<(ResourceModel Resource, string Namespace)> resources)
    {
        var builder = new StringBuilder();
        builder.Append(ResourceEmitter.Header).Append('\n');
        builder.Append("using Microsoft.EntityFrameworkCore;\n\n");
        builder.Append("namespace Haldus.Generated;\n\n");
        builder.Append("internal static class HaldusModule\n");
        builder.Append("{\n");
        builder.Append("    [global::System.Runtime.CompilerServices.ModuleInitializer]\n");
        builder.Append("    internal static void Initialize()\n");
        builder.Append("    {\n");
        builder.Append("        global::Haldus.HaldusModelRegistry.Register(typeof(HaldusModule).Assembly, static (modelBuilder, context) =>\n");
        builder.Append("        {\n");

        foreach (var (resource, entityNamespace) in resources)
        {
            AppendModel(builder, resource, ResourceEmitter.QualifiedName(resource, entityNamespace));
        }

        builder.Append("        });\n");

        foreach (var (resource, entityNamespace) in resources)
        {
            var entity = ResourceEmitter.QualifiedName(resource, entityNamespace);
            builder.Append('\n');
            builder.Append($"        global::Haldus.HaldusEndpointRegistry.Register<{entity}>({entity}Endpoints.Map);\n");
        }

        builder.Append("    }\n");
        builder.Append("}\n");

        return builder.ToString();
    }

    /// <summary>Eklenecek DbSet yoksa <c>null</c> döner.</summary>
    public static string? EmitDbContext(DbContextModel dbContext, IReadOnlyList<(ResourceModel Resource, string Namespace)> resources)
    {
        var existing = new HashSet<string>(dbContext.MemberNames);
        var builder = new StringBuilder();

        foreach (var (resource, entityNamespace) in resources)
        {
            // Tek kayıtlı kaynak koleksiyon değildir; adı çoğullanmaz.
            var setName = resource.Archetype == Archetype.Singleton
                ? resource.EntityName
                : Naming.Pluralize(resource.EntityName);
            if (!existing.Add(setName))
            {
                continue;
            }

            var entity = ResourceEmitter.QualifiedName(resource, entityNamespace);
            builder.Append($"    public global::Microsoft.EntityFrameworkCore.DbSet<{entity}> {setName} => Set<{entity}>();\n");
        }

        if (builder.Length == 0)
        {
            return null;
        }

        return $$"""
            {{ResourceEmitter.Header}}
            {{ResourceEmitter.NamespaceLine(dbContext.Namespace)}}
            partial class {{dbContext.Name}}
            {
            {{builder}}}

            """;
    }

    private static void AppendModel(StringBuilder builder, ResourceModel resource, string entity)
    {
        var statements = new List<string>();
        var tenant = resource.Has(Traits.MultiTenant);

        if (resource.Archetype == Archetype.Singleton)
        {
            // Kapsam başına tek kayıt: anahtar ya kiracının kendisidir ya da sabit bir gölge alandır.
            if (tenant)
            {
                statements.Add("entity.HasKey(item => item.TenantId);");
            }
            else
            {
                statements.Add("entity.Property<int>(global::Haldus.HaldusModel.SingletonKey).ValueGeneratedNever();");
                statements.Add("entity.HasKey(global::Haldus.HaldusModel.SingletonKey);");
            }
        }
        else if (resource.Archetype.HasClientKey())
        {
            // Anahtarı istemci verir; kiracıya bağlı kaynakta aynı anahtar her kiracıda ayrı kullanılabilir.
            statements.Add(tenant
                ? $"entity.HasKey(item => new {{ item.TenantId, item.{resource.KeyName} }});"
                : $"entity.HasKey(item => item.{resource.KeyName});");
            statements.Add($"entity.Property(item => item.{resource.KeyName}).ValueGeneratedNever();");
        }

        if (tenant)
        {
            // Filtre, modelin kurulduğu bağlama başvurur; EF Core bu başvuruyu her sorguda geçerli bağlamla değiştirir.
            statements.Add(
                "entity.HasQueryFilter(global::Haldus.HaldusFilters.Tenant, item => item.TenantId == global::Haldus.HaldusScope.TenantId(context));");

            // Kiracı zaten anahtarın ilk parçasıysa ayrı indeks gereksizdir.
            if (resource.Archetype != Archetype.Singleton && !resource.Archetype.HasClientKey())
            {
                statements.Add("entity.HasIndex(item => item.TenantId);");
            }
        }

        if (resource.Has(Traits.SoftDeletable))
        {
            statements.Add("entity.HasQueryFilter(global::Haldus.HaldusFilters.SoftDelete, item => item.DeletedAt == null);");
        }

        if (resource.Has(Traits.ConcurrencyChecked))
        {
            statements.Add("entity.Property(item => item.Version).IsRowVersion();");
        }

        if (statements.Count == 0)
        {
            builder.Append($"            modelBuilder.Entity<{entity}>();\n");
            return;
        }

        builder.Append($"            modelBuilder.Entity<{entity}>(entity =>\n");
        builder.Append("            {\n");

        foreach (var statement in statements)
        {
            builder.Append($"                {statement}\n");
        }

        builder.Append("            });\n");
    }
}
