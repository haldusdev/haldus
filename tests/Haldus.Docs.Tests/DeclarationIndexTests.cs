using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus.Docs.Tests;

/// <summary>
/// Herkese açık her bildirimin (<c>Map*</c>, zincir metotları, <c>AddHaldus*</c> / <c>UseHaldus</c>) bildirim
/// dizininde (<c>docs/reference/declarations.md</c>) geçtiğini ve dizindeki her bağlantının var olan bir belgeye
/// gittiğini doğrular. Yeni bir bildirim belgelenmeden eklenemez.
/// </summary>
public sealed class DeclarationIndexTests
{
    private static readonly string Index = File.ReadAllText(Path.Combine(DocBlocks.Root, "docs", "reference", "declarations.md"));

    private static readonly Assembly[] Runtimes =
    [
        typeof(ResourceBuilder<>).Assembly,
        typeof(HaldusPostgreSqlJobsExtensions).Assembly,
        typeof(HaldusInMemoryJobsExtensions).Assembly,
        typeof(HaldusHangfireJobsExtensions).Assembly,
        typeof(HaldusS3StorageExtensions).Assembly,
        typeof(HaldusSqsServiceCollectionExtensions).Assembly,
        typeof(HaldusRabbitMqServiceCollectionExtensions).Assembly,
        typeof(HaldusKafkaServiceCollectionExtensions).Assembly,
    ];

    [Fact]
    public void Index_NamesEveryDeclaration()
    {
        Type[] builders = [typeof(ResourceBuilder<>), typeof(DetailBuilder<,>), typeof(AssociationBuilder<,>)];

        var members = builders
            .SelectMany(builder => builder.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => !method.IsSpecialName && (IsBuilder(method.ReturnType, builders) || method.ReturnType == typeof(RouteHandlerBuilder)));

        var extensions = Runtimes
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type is { IsAbstract: true, IsSealed: true })
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.IsDefined(typeof(ExtensionAttribute), inherit: false))
            .Where(method =>
            {
                var target = method.GetParameters()[0].ParameterType;
                return IsBuilder(target, builders)
                    || target == typeof(IServiceCollection)
                    || target == typeof(IApplicationBuilder)
                    || (target == typeof(IEndpointRouteBuilder) && method.Name.StartsWith("Map", StringComparison.Ordinal));
            });

        var names = members.Concat(extensions).Select(method => method.Name).Distinct().Order().ToList();
        var missing = names.Where(name => !Regex.IsMatch(Index, $@"`[^`]*\b{name}\b[^`]*`")).ToList();

        Assert.True(names.Count > 90, $"Only {names.Count} declarations were found; the discovery is broken.");
        Assert.True(missing.Count == 0, "Not in docs/reference/declarations.md: " + string.Join(", ", missing));
    }

    [Fact]
    public void Links_PointToExistingDocumentsAndHeadings()
    {
        var broken = new List<string>();

        foreach (var file in DocBlocks.Files())
        {
            // Kod bloklarındaki metin bağlantı değildir.
            var text = Regex.Replace(File.ReadAllText(file), @"(?ms)^\s*```.*?^\s*```", string.Empty);

            foreach (Match link in Regex.Matches(text, @"\]\((?<target>[^)\s#]*)(#(?<anchor>[^)\s]*))?\)"))
            {
                var target = link.Groups["target"].Value;

                if (target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.Ordinal))
                {
                    continue;
                }

                var resolved = target.Length == 0
                    ? file
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, Uri.UnescapeDataString(target)));

                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    broken.Add($"{Path.GetRelativePath(DocBlocks.Root, file)} -> {target}");
                    continue;
                }

                var anchor = Uri.UnescapeDataString(link.Groups["anchor"].Value);

                if (anchor.Length > 0 && resolved.EndsWith(".md", StringComparison.Ordinal) && !Headings(resolved).Contains(anchor))
                {
                    broken.Add($"{Path.GetRelativePath(DocBlocks.Root, file)} -> {target}#{anchor}");
                }
            }
        }

        Assert.True(broken.Count == 0, "Broken links: " + string.Join(", ", broken));
    }

    /// <summary>Bir belgenin başlıklarının GitHub'ın ürettiği bağlantı adları (aynı ad ikinci kez geçerse <c>-1</c> eklenir).</summary>
    private static HashSet<string> Headings(string file)
    {
        var text = Regex.Replace(File.ReadAllText(file), @"(?ms)^\s*```.*?^\s*```", string.Empty);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var slugs = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match heading in Regex.Matches(text, @"(?m)^#{1,6}\s+(?<title>.+?)\s*$"))
        {
            var slug = Slug(heading.Groups["title"].Value);
            var count = seen.GetValueOrDefault(slug);
            seen[slug] = count + 1;
            slugs.Add(count == 0 ? slug : $"{slug}-{count}");
        }

        return slugs;
    }

    // GitHub: küçük harfe çevir (JavaScript gibi: 'İ' iki karaktere, "i̇" olur), harf, rakam, boşluk, '-' ve '_' dışındakileri
    // at, boşlukları '-' yap. Bağlantı verilen başlıklar bu yüzden 'İ' ile başlamamalıdır.
    private static string Slug(string title)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var character in title.Trim())
        {
            if (character == 'İ')
            {
                builder.Append("i̇");
            }
            else if (char.IsLetterOrDigit(character) || character is '-' or '_' || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (character == ' ')
            {
                builder.Append('-');
            }
        }

        return builder.ToString();
    }

    private static bool IsBuilder(Type type, Type[] builders) =>
        type.IsGenericType && builders.Contains(type.GetGenericTypeDefinition());
}
