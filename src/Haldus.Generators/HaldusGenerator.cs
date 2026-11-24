using System.Collections.Immutable;
using System.Text;
using Haldus.Generators.Diagnostics;
using Haldus.Generators.Emit;
using Haldus.Generators.Model;
using Haldus.Generators.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Haldus.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class HaldusGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var resources = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => MapCallParser.IsCandidate(node),
                static (syntaxContext, cancellationToken) => MapCallParser.Parse(syntaxContext, cancellationToken))
            .Where(static resource => resource is not null)
            .Select(static (resource, _) => resource!);

        var dbContexts = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => DbContextParser.IsCandidate(node),
                static (syntaxContext, cancellationToken) => DbContextParser.Parse(syntaxContext, cancellationToken))
            .Where(static dbContext => dbContext is not null)
            .Select(static (dbContext, _) => dbContext!);

        // Kullanıcının yazmadığı entity'ler projenin kök namespace'ine üretilir.
        var rootNamespace = context.AnalyzerConfigOptionsProvider
            .Combine(context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName))
            .Select(static (pair, _) =>
                pair.Left.GlobalOptions.TryGetValue("build_property.RootNamespace", out var value) && value.Length > 0
                    ? value
                    : pair.Right ?? string.Empty);

        var input = resources.Collect().Combine(dbContexts.Collect()).Combine(rootNamespace);

        context.RegisterSourceOutput(
            input,
            static (productionContext, data) => Emit(productionContext, data.Left.Left, data.Left.Right, data.Right));
    }

    private static void Emit(
        SourceProductionContext context,
        ImmutableArray<ResourceModel> resources,
        ImmutableArray<DbContextModel> dbContexts,
        string rootNamespace)
    {
        var valid = new List<(ResourceModel Resource, string Namespace)>();
        var seen = new HashSet<string>();

        foreach (var resource in resources)
        {
            foreach (var diagnostic in resource.Diagnostics)
            {
                Report(context, diagnostic);
            }

            if (resource.HasErrors)
            {
                continue;
            }

            var entityNamespace = resource.Namespace ?? rootNamespace;
            if (!seen.Add($"{entityNamespace}.{resource.EntityName}"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    HaldusDiagnostics.EntityMappedMoreThanOnce, resource.Location.ToLocation(), resource.EntityName));
                continue;
            }

            valid.Add((resource, entityNamespace));
        }

        if (valid.Count == 0)
        {
            return;
        }

        foreach (var (resource, entityNamespace) in valid)
        {
            var hintPrefix = entityNamespace.Length == 0 ? resource.EntityName : $"{entityNamespace}.{resource.EntityName}";

            if (ResourceEmitter.EmitEntity(resource, entityNamespace) is { } entity)
            {
                AddSource(context, $"{hintPrefix}.Entity.g.cs", entity);
            }

            AddSource(context, $"{hintPrefix}.Dtos.g.cs", ResourceEmitter.EmitDtos(resource, entityNamespace));
            AddSource(context, $"{hintPrefix}.Endpoints.g.cs", ResourceEmitter.EmitEndpoints(resource, entityNamespace));
        }

        AddSource(context, "HaldusModule.g.cs", ModuleEmitter.EmitModule(valid));

        foreach (var dbContext in dbContexts.Distinct())
        {
            if (ModuleEmitter.EmitDbContext(dbContext, valid) is { } source)
            {
                AddSource(context, $"{dbContext.Name}.DbSets.g.cs", source);
            }
        }
    }

    private static void Report(SourceProductionContext context, DiagnosticInfo diagnostic) =>
        context.ReportDiagnostic(Diagnostic.Create(
            HaldusDiagnostics.Get(diagnostic.Id),
            diagnostic.Location.ToLocation(),
            [.. diagnostic.Arguments]));

    private static void AddSource(SourceProductionContext context, string hintName, string source) =>
        context.AddSource(hintName, SourceText.From(source.Replace("\r\n", "\n"), Encoding.UTF8));
}
