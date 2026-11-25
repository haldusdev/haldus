using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Haldus.Generators.Tests;

/// <summary>
/// <c>docs/guide.md</c> Haldus'u hiç görmemiş birinin (bir kodlama ajanının da) okuduğu tek başvurudur; eksik kalan bir
/// bildirim onun için yoktur. Bu testler kılavuzun herkese açık API ile birlikte ilerlediğini denetler: yeni bir
/// <c>Map*</c> veya zincir metodu, yeni bir tanı kılavuza yazılmadan eklenemez. Anlatımın doğruluğunu sınamazlar.
/// </summary>
public sealed class GuideTests
{
    private static readonly string Guide = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "guide.md"));

    private static readonly Assembly Runtime = typeof(ResourceBuilder<>).Assembly;

    [Fact]
    public void Guide_NamesEveryMapAndChainMethod()
    {
        Type[] builders = [typeof(ResourceBuilder<>), typeof(DetailBuilder<,>), typeof(AssociationBuilder<,>)];

        // Builder'ların kendi üyeleri: zinciri sürdüren ya da bir uç eşleyen metotlar (arayüz uygulamaları değil).
        var members = builders
            .SelectMany(builder => builder.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => !method.IsSpecialName && (IsBuilder(method.ReturnType, builders) || method.ReturnType == typeof(RouteHandlerBuilder)));

        // Uzantılar: trait'ler builder'ı, Map* çağrıları uç eşleyiciyi genişletir.
        var extensions = Extensions()
            .Where(method => IsBuilder(method.GetParameters()[0].ParameterType, builders)
                || (method.GetParameters()[0].ParameterType == typeof(IEndpointRouteBuilder) && method.Name.StartsWith("Map", StringComparison.Ordinal)));

        var names = members.Concat(extensions).Select(method => method.Name).Distinct().Order().ToList();

        Assert.True(names.Count > 80, $"Only {names.Count} declarations were found; the discovery is broken.");
        AssertAllPresent(names.Where(name => !Regex.IsMatch(Guide, $@"\b{name}[<(]")));
    }

    [Fact]
    public void Guide_NamesEveryRegistrationMethod()
    {
        var names = Extensions()
            .Where(method => method.GetParameters()[0].ParameterType == typeof(IServiceCollection)
                || method.GetParameters()[0].ParameterType == typeof(IApplicationBuilder))
            .Select(method => method.Name)
            .Distinct()
            .Order()
            .ToList();

        Assert.Contains("AddHaldus", names);
        Assert.Contains("UseHaldus", names);
        AssertAllPresent(names.Where(name => !Regex.IsMatch(Guide, $@"\b{name}\b")));
    }

    [Fact]
    public void Guide_ExplainsEveryDiagnostic()
    {
        var descriptors = typeof(HaldusGenerator).Assembly
            .GetType("Haldus.Generators.Diagnostics.HaldusDiagnostics", throwOnError: true)!
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(DiagnosticDescriptor))
            .Select(field => (DiagnosticDescriptor)field.GetValue(null)!)
            .ToList();

        Assert.NotEmpty(descriptors);

        foreach (var descriptor in descriptors)
        {
            // Başlık, derleme çıktısındaki bağlantının vardığı yerdir.
            Assert.Contains($"### {descriptor.Id}\n", Guide.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.EndsWith($"docs/guide.md#{descriptor.Id.ToLowerInvariant()}", descriptor.HelpLinkUri, StringComparison.Ordinal);
        }
    }

    private static void AssertAllPresent(IEnumerable<string> missing)
    {
        var names = missing.ToList();
        Assert.True(names.Count == 0, $"Not described in docs/guide.md: {string.Join(", ", names)}");
    }

    private static IEnumerable<MethodInfo> Extensions() => Runtime.GetExportedTypes()
        .Where(type => type is { IsAbstract: true, IsSealed: true })
        .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        .Where(method => method.IsDefined(typeof(ExtensionAttribute), inherit: false));

    private static bool IsBuilder(Type type, Type[] builders) =>
        type.IsGenericType && builders.Contains(type.GetGenericTypeDefinition());

    private static string RepositoryRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
}
