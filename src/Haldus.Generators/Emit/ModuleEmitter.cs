using System.Text;
using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;

namespace Haldus.Generators.Emit;

/// <summary>Modülün (assembly'nin) kayıt kodunu ve bağlamın DbSet özelliklerini üretir.</summary>
internal static class ModuleEmitter
{
    public static string EmitModule(
        IReadOnlyList<(ResourceModel Resource, string Namespace)> resources,
        IReadOnlyList<HandlerModel> handlers,
        IReadOnlyList<QueryModel> queries)
    {
        var builder = new StringBuilder();
        builder.Append(ResourceEmitter.Header).Append('\n');
        // Üretilen kod, tüketen projede örtük using'ler kapalıyken de derlenmelidir.
        builder.Append("using System.Linq;\n");
        builder.Append("using Microsoft.EntityFrameworkCore;\n\n");
        builder.Append("namespace Haldus.Generated;\n\n");
        builder.Append("internal static class HaldusModule\n");
        builder.Append("{\n");
        builder.Append("    [global::System.Runtime.CompilerServices.ModuleInitializer]\n");
        builder.Append("    internal static void Initialize()\n");
        builder.Append("    {\n");
        var blocks = new List<string>();

        if (resources.Count > 0)
        {
            var model = new StringBuilder();
            model.Append("        global::Haldus.HaldusModelRegistry.Register(typeof(HaldusModule).Assembly, static (modelBuilder, context) =>\n");
            model.Append("        {\n");

            foreach (var (resource, entityNamespace) in resources)
            {
                AppendModel(model, resource, ResourceEmitter.QualifiedName(resource, entityNamespace));
            }

            model.Append("        });\n");
            blocks.Add(model.ToString());
        }

        foreach (var (resource, entityNamespace) in resources)
        {
            var entity = ResourceEmitter.QualifiedName(resource, entityNamespace);
            blocks.Add($"        global::Haldus.HaldusEndpointRegistry.Register<{entity}>({entity}Endpoints.Map);\n");
        }

        if (handlers.Count > 0)
        {
            const string TryAddScoped =
                "global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddScoped";

            // TryAdd: kullanıcı aynı servisi kendisi kaydettiyse onunki geçerli kalır.
            var services = new StringBuilder();
            services.Append("        global::Haldus.HaldusServiceRegistry.Register(typeof(HaldusModule).Assembly, static services =>\n");
            services.Append("        {\n");

            foreach (var handler in handlers)
            {
                foreach (var service in handler.Services)
                {
                    services.Append($"            {TryAddScoped}<{service}, {handler.Implementation}>(services);\n");
                }
            }

            services.Append("        });\n");
            blocks.Add(services.ToString());
        }

        foreach (var query in queries)
        {
            var endpoint = query.RequestNamespace.Length == 0
                ? $"global::{query.RequestName}QueryEndpoint"
                : $"global::{query.RequestNamespace}.{query.RequestName}QueryEndpoint";

            blocks.Add(
                $"        global::Haldus.HaldusQueryRegistry.Register<{query.RequestType}, {query.ResponseType}>({endpoint}.Map);\n");
        }

        builder.Append(string.Join("\n", blocks));
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

        if (resource.Parent is { } parent)
        {
            // Ebeveyn altındaki kayıt, ancak ebeveyni görünürse görünür. Alt sorgu ebeveynin kendi
            // filtrelerinden (kiracı, silinmişlik, onun da ebeveyni) geçer. Koşul tek tek sorgulara değil
            // modele konur ki bu kaynağa referans veren veya onun altına yerleşen kaynaklar da korunsun.
            statements.Add(
                $"entity.HasQueryFilter(global::Haldus.HaldusFilters.Parent, item => context.Set<{parent.TargetType}>()"
                + $".Any(parent => parent.{parent.TargetKeyName} == item.{parent.PropertyName}));");
        }

        foreach (var reference in resource.References)
        {
            statements.Add(
                $"entity.HasOne<{reference.TargetType}>().WithMany().HasForeignKey(item => item.{reference.PropertyName})"
                + $".OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.{reference.OnDelete});");
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
