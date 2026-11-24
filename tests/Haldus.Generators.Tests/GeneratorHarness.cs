using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Haldus.Generators.Tests;

/// <summary>Verilen kaynağı Haldus generator'ı ile derler ve sonucu döndürür.</summary>
internal static class GeneratorHarness
{
    private const string GlobalUsings = """
        global using System;
        global using System.ComponentModel.DataAnnotations;
        global using Haldus;
        global using Microsoft.AspNetCore.Builder;
        global using Microsoft.AspNetCore.Routing;
        global using Microsoft.EntityFrameworkCore;
        global using Microsoft.Extensions.DependencyInjection;
        """;

    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
    ];

    public static GeneratorRun Run(string source, string assemblyName = "Shop")
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [
                CSharpSyntaxTree.ParseText(GlobalUsings, parseOptions, path: "GlobalUsings.cs"),
                CSharpSyntaxTree.ParseText(source, parseOptions, path: "Source.cs"),
            ],
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        CSharpGeneratorDriver
            .Create([new HaldusGenerator().AsSourceGenerator()], parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics, TestContext.Current.CancellationToken);

        var sources = output.SyntaxTrees
            .Where(tree => tree.FilePath.EndsWith(".g.cs", StringComparison.Ordinal))
            .ToDictionary(tree => Path.GetFileName(tree.FilePath), tree => tree.ToString());

        var compileErrors = output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        return new GeneratorRun(sources, generatorDiagnostics, compileErrors);
    }
}

internal sealed record GeneratorRun(
    IReadOnlyDictionary<string, string> Sources,
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<Diagnostic> CompileErrors)
{
    /// <summary>Üretilen kodun hatasız derlendiğini ve generator'ın hata bildirmediğini doğrular.</summary>
    public GeneratorRun AssertCompiles()
    {
        Assert.Empty(GeneratorDiagnostics);
        Assert.True(CompileErrors.IsEmpty, string.Join(Environment.NewLine, CompileErrors));
        return this;
    }
}
