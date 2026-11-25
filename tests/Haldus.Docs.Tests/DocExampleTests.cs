using System.Collections.Immutable;
using System.Text;
using Haldus.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Haldus.Docs.Tests;

/// <summary>
/// Belgelerdeki C# örneklerinin Haldus generator'ı ile birlikte hatasız derlendiğini doğrular. Okuyanın kopyalayıp
/// yapıştırdığı kod çalışmalıdır; bu test, API değişince eskiyen bir örneği derleme hatası olarak yakalar.
/// Kurallar <c>CONTRIBUTING.md</c> içindeki "Belgeler" bölümündedir.
/// </summary>
public sealed class DocExampleTests
{
    // Web SDK'nın örtük using'leri ve örneklerin hepsinde yazılmış sayılan üç ad alanı (belgelerde de söylenir).
    private const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Net.Http.Json;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using Microsoft.AspNetCore.Builder;
        global using Microsoft.AspNetCore.Hosting;
        global using Microsoft.AspNetCore.Http;
        global using Microsoft.AspNetCore.Routing;
        global using Microsoft.Extensions.Configuration;
        global using Microsoft.Extensions.DependencyInjection;
        global using Microsoft.Extensions.Hosting;
        global using Microsoft.Extensions.Logging;
        global using System.ComponentModel.DataAnnotations;
        global using Haldus;
        global using Microsoft.EntityFrameworkCore;
        """;

    private const string DefaultContext =
        "public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);";

    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
    ];

    public static TheoryData<string> Examples()
    {
        var data = new TheoryData<string>();

        foreach (var example in DocBlocks.All().SelectMany(block => block.Ids).Distinct())
        {
            data.Add(example);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_Compiles(string id)
    {
        var blocks = DocBlocks.All().Where(block => block.Ids.Contains(id)).ToList();
        var expected = blocks.Select(block => block.ExpectedError).FirstOrDefault(error => error is not null);

        var (diagnostics, generated) = Compile(blocks.Select(block => block.Code));

        if (expected is null)
        {
            Assert.True(diagnostics.IsEmpty, $"{id}:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");
            Assert.True(generated > 0, $"{id}: the generator produced nothing; the example declares no resource.");
        }
        else
        {
            Assert.True(
                diagnostics.Any(diagnostic => diagnostic.Id == expected),
                $"{id}: expected {expected}, got:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");
        }
    }

    [Fact]
    public void EveryCSharpBlock_IsCompiledOrMarkedAsFragment()
    {
        var unmarked = DocBlocks.All()
            .Where(block => block.Examples.Count == 0 && !block.IsFragment)
            .Select(block => $"{block.File}:{block.Line}")
            .ToList();

        Assert.True(unmarked.Count == 0, "C# blocks without <!-- example: name --> or <!-- fragment -->: " + string.Join(", ", unmarked));
    }

    [Fact]
    public void Examples_AreFound()
    {
        // Ayrıştırıcı bozulursa testler sessizce boş geçmesin.
        Assert.True(DocBlocks.All().Count(block => block.Examples.Count > 0) > (DocBlocks.Only is null ? 50 : 0));
    }

    private static (ImmutableArray<Diagnostic> Diagnostics, int Generated) Compile(IEnumerable<string> blocks)
    {
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(GlobalUsings, options, path: "GlobalUsings.cs") };

        // Üst düzey komutlar tek bir dosyada olabilir: komut taşıyan blokların using'leri, komutları ve tipleri tek bir
        // Program.cs'te birleştirilir. Yalnızca tip taşıyan bloklar kendi dosyalarıdır.
        var usings = new StringBuilder();
        var statements = new StringBuilder();
        var members = new StringBuilder();
        var index = 0;

        foreach (var code in blocks)
        {
            var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code, options).GetRoot();

            if (!root.Members.OfType<GlobalStatementSyntax>().Any())
            {
                trees.Add(CSharpSyntaxTree.ParseText(code, options, path: $"Block{index++}.cs"));
                continue;
            }

            foreach (var directive in root.Usings)
            {
                usings.AppendLine(directive.ToString());
            }

            foreach (var member in root.Members)
            {
                (member is GlobalStatementSyntax ? statements : members).AppendLine(member.ToFullString());
            }
        }

        var hasProgram = statements.Length > 0;

        if (hasProgram)
        {
            var body = statements.ToString();
            var prelude = new StringBuilder();

            // Kısa örnekler uygulamanın kurulduğu satırları yazmaz; Program.cs'in geri kalanı yazılmış sayılır.
            if (!body.Contains("CreateBuilder", StringComparison.Ordinal))
            {
                prelude.AppendLine("var builder = WebApplication.CreateBuilder(args);");
            }

            if (!body.Contains("builder.Build()", StringComparison.Ordinal))
            {
                prelude.AppendLine("var app = builder.Build();");
            }

            trees.Add(CSharpSyntaxTree.ParseText($"{usings}{prelude}{body}{members}", options, path: "Program.cs"));
        }

        var declaresContext = trees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Any(type => type.Identifier.Text == "AppDbContext");

        if (!declaresContext && trees.Any(tree => tree.ToString().Contains("AppDbContext", StringComparison.Ordinal)))
        {
            trees.Add(CSharpSyntaxTree.ParseText(DefaultContext, options, path: "AppDbContext.cs"));
        }

        var compilation = CSharpCompilation.Create(
            "Docs",
            trees,
            References,
            new CSharpCompilationOptions(
                hasProgram ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        CSharpGeneratorDriver
            .Create([new HaldusGenerator().AsSourceGenerator()], parseOptions: options)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics, TestContext.Current.CancellationToken);

        var generated = output.SyntaxTrees.Count(tree => tree.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));

        var errors = generatorDiagnostics
            .Concat(output.GetDiagnostics(TestContext.Current.CancellationToken))
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        return (errors, generated);
    }
}
