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
        IReadOnlyList<QueryModel> queries,
        IReadOnlyList<AssociationModel> associations)
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
                if (resource.Numbering is { Gapless: false })
                {
                    model.Append($"            modelBuilder.HasSequence<long>({ResourceEmitter.Literal(ResourceEmitter.NumberSequence(resource))});\n");
                }

                AppendModel(model, resource, ResourceEmitter.QualifiedName(resource, entityNamespace));
            }

            // Haldus'un kendi tabloları yalnızca onları kullanan bir kaynak varsa modele girer.
            if (resources.Any(entry => entry.Resource.Numbering is { Gapless: true }))
            {
                model.Append("            global::Haldus.HaldusNumbering.Configure(modelBuilder);\n");
            }

            if (resources.Any(entry => entry.Resource.Has(Traits.Idempotent)))
            {
                model.Append("            global::Haldus.HaldusIdempotency.Configure(modelBuilder);\n");
            }

            foreach (var association in associations)
            {
                model.Append(AssociationEmitter.EmitModel(association));
            }

            model.Append("        });\n");
            blocks.Add(model.ToString());
        }

        foreach (var (resource, entityNamespace) in resources.Where(entry => entry.Resource.Archetype != Archetype.Detail))
        {
            var entity = ResourceEmitter.QualifiedName(resource, entityNamespace);
            blocks.Add($"        global::Haldus.HaldusEndpointRegistry.Register<{entity}>({entity}Endpoints.Map);\n");

            if (resource.Archetype == Archetype.Job)
            {
                // Yürütücü işi, kuyruktan gelen tip adıyla bulur.
                blocks.Add($"        global::Haldus.HaldusJobRegistry.Register<{entity}>();\n");
            }

            foreach (var transition in resource.Transitions)
            {
                var name = transition.RegistryName is null ? "null" : ResourceEmitter.Literal(transition.RegistryName);

                blocks.Add(transition.RequestType is null
                    ? $"        global::Haldus.HaldusTransitionRegistry.Register<{entity}>({name}, {entity}Endpoints.Map{transition.PascalName}Transition);\n"
                    : $"        global::Haldus.HaldusTransitionRegistry.Register<{entity}, {transition.RequestType}>({name}, {entity}Endpoints.Map{transition.PascalName}Transition);\n");
            }
        }

        foreach (var association in associations)
        {
            blocks.Add(
                $"        global::Haldus.HaldusEndpointRegistry.RegisterAssociation<{association.Left.QualifiedName}, "
                + $"{association.Right.QualifiedName}>({association.QualifiedLinkName}Endpoints.Map);\n");
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
    public static string? EmitDbContext(
        DbContextModel dbContext,
        IReadOnlyList<(ResourceModel Resource, string Namespace)> resources,
        IReadOnlyList<AssociationModel> associations)
    {
        var existing = new HashSet<string>(dbContext.MemberNames);
        var builder = new StringBuilder();

        foreach (var association in associations)
        {
            var setName = Naming.Pluralize(association.LinkName);
            if (existing.Add(setName))
            {
                builder.Append(
                    $"    public global::Microsoft.EntityFrameworkCore.DbSet<{association.QualifiedLinkName}> {setName} "
                    + $"=> Set<{association.QualifiedLinkName}>();\n");
            }
        }

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

        if (resource.Workflow is not null)
        {
            // Durum adıyla saklanır: enum'a yeni üye eklemek veya üyeleri yeniden sıralamak eski kayıtları bozmaz.
            statements.Add("entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(64);");

            if (!resource.Has(Traits.ConcurrencyChecked))
            {
                // Durum kontrolü ile yazma arasında başka bir istek durumu değiştirebilir. Durum eşzamanlılık
                // belirteci olunca her güncelleme ve silme "durum hâlâ okuduğum gibi mi" koşuluyla yapılır;
                // sürüm kontrollü kaynakta aynı işi satır sürümü görür.
                statements.Add("entity.Property(item => item.Status).IsConcurrencyToken();");
            }
        }

        if (resource.Archetype == Archetype.Job)
        {
            // Durum eşzamanlılık belirtecidir: iki yürütücü aynı işi sahiplenemez ve iptal ile başlatma yarışmaz.
            statements.Add("entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(64).IsConcurrencyToken();");
            statements.Add("entity.Property(item => item.Error).HasMaxLength(2000);");
            statements.Add("entity.Property(item => item.Culture).HasMaxLength(20);");
            statements.Add("entity.HasIndex(item => item.Status);");
        }

        if (resource.Numbering is not null)
        {
            statements.Add("entity.Property(item => item.Number).HasMaxLength(64);");
            statements.Add(tenant
                ? "entity.HasIndex(item => new { item.TenantId, item.Number }).IsUnique();"
                : "entity.HasIndex(item => item.Number).IsUnique();");
        }

        if (resource.Expiry is not null)
        {
            // Süresi dolan kayıt silinmez ama hiçbir sorguda görünmez; şimdiki zaman her sorguda yeniden okunur.
            statements.Add(
                "entity.HasQueryFilter(global::Haldus.HaldusFilters.Expiry, item => item.ExpiresAt == null "
                + "|| item.ExpiresAt > global::Haldus.HaldusScope.UtcNow(context));");
        }

        foreach (var embedded in resource.Properties.Where(property => property.IsEmbedded))
        {
            // Değer nesnesinin kendi tablosu olmaz; alanları sahibinin kolonlarında saklanır.
            statements.Add($"entity.ComplexProperty(item => item.{embedded.Name});");
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

        // Gömülü detayın sahibiyle ilişkisi, koleksiyonu taşıyan sahip tarafında kurulur.
        var embeddedDetail = resource.Archetype == Archetype.Detail;

        foreach (var reference in resource.References.Where(reference => !(embeddedDetail && reference.Kind == ReferenceKind.Parent)))
        {
            statements.Add(
                $"entity.HasOne<{reference.TargetType}>().WithMany().HasForeignKey(item => item.{reference.PropertyName})"
                + $".OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.{reference.OnDelete});");
        }

        if (embeddedDetail)
        {
            // Satırın kimliğini Haldus atar; EF anahtarı dolu yeni bir satırı var olan bir satır sanmasın.
            statements.Add($"entity.Property(item => item.{resource.KeyName}).ValueGeneratedNever();");
        }

        foreach (var detail in resource.Details)
        {
            statements.Add(
                $"entity.HasMany(item => item.{detail.CollectionName}).WithOne().HasForeignKey(detail => detail.{detail.Parent!.PropertyName})"
                + ".OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade);");
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
