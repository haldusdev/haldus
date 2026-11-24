using System.Collections.Immutable;
using System.Text;
using Haldus.Generators.Diagnostics;
using Haldus.Generators.Emit;
using Haldus.Generators.Infrastructure;
using Haldus.Generators.Linking;
using Haldus.Generators.Model;
using Haldus.Generators.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

        var queries = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => OperationParser.IsQueryCandidate(node),
                static (syntaxContext, cancellationToken) => OperationParser.ParseQuery(syntaxContext, cancellationToken))
            .Where(static query => query is not null)
            .Select(static (query, _) => query!);

        var handlers = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => OperationParser.IsHandlerCandidate(node),
                static (syntaxContext, cancellationToken) => OperationParser.ParseHandler(syntaxContext, cancellationToken))
            .Where(static handler => handler is not null)
            .Select(static (handler, _) => handler!);

        var associations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => OperationParser.IsAssociationCandidate(node),
                static (syntaxContext, cancellationToken) => OperationParser.ParseAssociation(syntaxContext, cancellationToken))
            .Where(static association => association is not null)
            .Select(static (association, _) => association!);

        var transitions = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => TransitionParser.IsCandidate(node),
                static (syntaxContext, cancellationToken) => TransitionParser.Parse(syntaxContext, cancellationToken))
            .Where(static transition => transition is not null)
            .Select(static (transition, _) => transition!);

        context.RegisterSourceOutput(
            resources.Collect()
                .Combine(dbContexts.Collect())
                .Combine(queries.Collect())
                .Combine(handlers.Collect())
                .Combine(associations.Collect())
                .Combine(transitions.Collect()),
            static (productionContext, data) => Emit(
                productionContext,
                data.Left.Left.Left.Left.Left,
                data.Left.Left.Left.Left.Right,
                data.Left.Left.Left.Right,
                data.Left.Left.Right,
                data.Left.Right,
                data.Right));

        // Zincir derleme zamanında okunduğu için bir Map çağrısına kesintisiz bağlı olmayan bildirimler
        // sessizce yok sayılırdı; bunun yerine hata verilir.
        var detachedDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name: var name },
                }
                && ChainReader.DeclarationMethods.Contains(name.Identifier.ValueText),
                static (syntaxContext, cancellationToken) => FindDetachedDeclaration(syntaxContext, cancellationToken))
            .Where(static diagnostic => diagnostic is not null)
            .Select(static (diagnostic, _) => diagnostic!);

        context.RegisterSourceOutput(detachedDeclarations, static (productionContext, diagnostic) => Report(productionContext, diagnostic));
    }

    private static DiagnosticInfo? FindDetachedDeclaration(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (ChainReader.IsRootedAtMapCall(invocation)
            || context.SemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method
            || !IsDeclarationHost(method.ContainingType))
        {
            return null;
        }

        var name = ((MemberAccessExpressionSyntax)invocation.Expression).Name;

        return new DiagnosticInfo(
            "HALDUS004",
            LocationInfo.From(name.GetLocation()),
            new EquatableArray<string>([name.Identifier.ValueText]));
    }

    /// <summary>Zincir bildirimleri ya uzantı sınıfında ya da doğrudan <c>ResourceBuilder</c> üzerinde tanımlıdır.</summary>
    private static bool IsDeclarationHost(INamedTypeSymbol? type) =>
        type?.ToDisplayString() == "Haldus.ResourceBuilderTraitExtensions"
        || type?.OriginalDefinition.ToDisplayString() == "Haldus.ResourceBuilder<TEntity>"
        || type?.OriginalDefinition.ToDisplayString() == "Haldus.DetailBuilder<TEntity, TDetail>";

    private static void Emit(
        SourceProductionContext context,
        ImmutableArray<ResourceModel> resources,
        ImmutableArray<DbContextModel> dbContexts,
        ImmutableArray<QueryModel> queries,
        ImmutableArray<HandlerModel> handlers,
        ImmutableArray<AssociationDeclaration> associationDeclarations,
        ImmutableArray<TransitionDeclaration> transitionDeclarations)
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

            var entityNamespace = resource.Namespace;
            if (!seen.Add($"{entityNamespace}.{resource.EntityName}"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    HaldusDiagnostics.EntityMappedMoreThanOnce, resource.Location.ToLocation(), resource.EntityName));
                continue;
            }

            valid.Add((resource, entityNamespace));
        }

        // Referansların hedefleri ve aranabilir alanlar, tüm kaynaklar bilindikten sonra çözülür.
        valid =
        [
            .. ResourceLinker
                .Link([.. valid.Select(entry => entry.Resource)], diagnostic => Report(context, diagnostic))
                .Select(resource => (resource, resource.Namespace)),
        ];

        var associations = ResourceLinker.LinkAssociations(
            associationDeclarations, [.. valid.Select(entry => entry.Resource)], diagnostic => Report(context, diagnostic));


        valid =
        [
            .. ResourceLinker
                .AttachTransitions([.. valid.Select(entry => entry.Resource)], transitionDeclarations, diagnostic => Report(context, diagnostic))
                .Select(resource => (resource, resource.Namespace)),
        ];

        // Aynı istek tipi birden çok rotaya eşlenebilir; endpoint kodu bir kez üretilir.
        var validQueries = new List<QueryModel>();
        var seenQueries = new HashSet<string>();

        foreach (var query in queries)
        {
            if (!query.HasParameterlessConstructor)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    HaldusDiagnostics.QueryRequestNeedsParameterlessConstructor,
                    query.Location.ToLocation(),
                    query.RequestName));
            }
            else if (seenQueries.Add(query.RequestType))
            {
                validQueries.Add(query);
            }
        }

        // Partial bir handler sınıfı her bildirimi için bir kez gelir.
        var distinctHandlers = handlers.Distinct().OrderBy(handler => handler.Implementation, StringComparer.Ordinal).ToList();

        if (valid.Count == 0 && validQueries.Count == 0 && distinctHandlers.Count == 0)
        {
            return;
        }

        foreach (var query in validQueries)
        {
            var hintPrefix = query.RequestNamespace.Length == 0 ? query.RequestName : $"{query.RequestNamespace}.{query.RequestName}";
            AddSource(context, $"{hintPrefix}.Query.g.cs", QueryEmitter.Emit(query));
        }

        foreach (var (resource, entityNamespace) in valid)
        {
            var hintPrefix = entityNamespace.Length == 0 ? resource.EntityName : $"{entityNamespace}.{resource.EntityName}";

            if (ResourceEmitter.EmitEntity(resource, entityNamespace) is { } entity)
            {
                AddSource(context, $"{hintPrefix}.Entity.g.cs", entity);
            }

            AddSource(context, $"{hintPrefix}.Dtos.g.cs", ResourceEmitter.EmitDtos(resource, entityNamespace));

            // Gömülü detayın kendi ucu yoktur; sahibinin uçlarında taşınır.
            if (resource.Archetype != Archetype.Detail)
            {
                AddSource(context, $"{hintPrefix}.Endpoints.g.cs", EndpointEmitter.Emit(resource, entityNamespace));
            }
        }

        foreach (var association in associations)
        {
            var hintPrefix = association.Namespace.Length == 0
                ? association.LinkName
                : $"{association.Namespace}.{association.LinkName}";

            AddSource(context, $"{hintPrefix}.Entity.g.cs", AssociationEmitter.EmitEntity(association));
            AddSource(context, $"{hintPrefix}.Endpoints.g.cs", AssociationEmitter.EmitEndpoints(association));
        }

        AddSource(context, "HaldusModule.g.cs", ModuleEmitter.EmitModule(valid, distinctHandlers, validQueries, associations));

        foreach (var dbContext in dbContexts.Distinct())
        {
            if (ModuleEmitter.EmitDbContext(dbContext, valid, associations) is { } source)
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
