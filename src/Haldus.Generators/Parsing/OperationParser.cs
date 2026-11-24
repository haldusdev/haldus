using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Haldus.Generators.Parsing;

/// <summary>Entity'ye bağlı olmayan işlemleri ve handler sınıflarını (komut, sorgu, geçiş) okur.</summary>
internal static class OperationParser
{
    private const string OperationExtensionsType = "Haldus.HaldusOperationExtensions";

    public static bool IsQueryCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { Identifier.ValueText: "MapQuery", TypeArgumentList.Arguments.Count: 2 },
            },
        };

    public static QueryModel? ParseQuery(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method
            || method.ContainingType?.ToDisplayString() != OperationExtensionsType
            || method.TypeArguments[0] is not INamedTypeSymbol { TypeKind: TypeKind.Class, ContainingType: null, IsGenericType: false } request)
        {
            return null;
        }

        // İstek, tıpkı bir filtre gibi, yazılabilir skaler özelliklerinden doldurulur.
        var parameters = MapCallParser.PublicInstanceProperties(request)
            .Where(property => property.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false })
            .Where(property => MapCallParser.TryClassify(
                property.Type is IArrayTypeSymbol array ? array.ElementType : property.Type, out _, out _))
            .Select(property => new FilterProperty(
                property.Name,
                MapCallParser.AsNullable(property.Type.ToDisplayString(MapCallParser.TypeFormat)),
                IsArray: property.Type is IArrayTypeSymbol));

        var name = ((MemberAccessExpressionSyntax)invocation.Expression).Name;

        return new QueryModel(
            request.Name,
            request.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
                ? containingNamespace.ToDisplayString()
                : string.Empty,
            request.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            method.TypeArguments[1].ToDisplayString(MapCallParser.TypeFormat),
            HasParameterlessConstructor: request.InstanceConstructors.Any(constructor =>
                constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility == Accessibility.Public),
            parameters.ToEquatableArray(),
            LocationInfo.From(name.GetLocation()));
    }

    public static bool IsAssociationCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { Identifier.ValueText: "MapAssociation", TypeArgumentList.Arguments.Count: 2 },
            },
        };

    public static AssociationDeclaration? ParseAssociation(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var name = (GenericNameSyntax)((MemberAccessExpressionSyntax)invocation.Expression).Name;

        // Uçlardan biri henüz üretilmemiş bir entity ise derleyici çağrıyı bağlayamaz; o durumda ad yeterlidir.
        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, cancellationToken);
        var method = symbolInfo.Symbol as IMethodSymbol ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

        if (method is not null && method.ContainingType?.ToDisplayString() != "Haldus.HaldusEndpointRouteBuilderExtensions")
        {
            return null;
        }

        var (leftName, leftType) = Describe(name.TypeArgumentList.Arguments[0], context.SemanticModel, cancellationToken);
        var (rightName, rightType) = Describe(name.TypeArgumentList.Arguments[1], context.SemanticModel, cancellationToken);

        // Kullanıcı bağ sınıfını (sol ucun yanında, {Sol}{Sağ} adıyla) kendisi de yazdıysa alanları bağın yüküdür.
        var payload = new List<PropertyModel>();
        var problems = new List<string>();
        var linkDeclared = false;
        var statusDeclared = false;
        WorkflowModel? workflow = null;

        foreach (var node in ChainReader.Nodes(invocation))
        {
            if (node.Name.Identifier.ValueText == "Workflow")
            {
                workflow = LifecycleReader.ReadWorkflow(
                    context.SemanticModel,
                    node.Name,
                    node.Invocation.ArgumentList.Arguments,
                    leftName + rightName,
                    (_, message) => problems.Add(message),
                    cancellationToken);
                break;
            }
        }

        if (context.SemanticModel.GetTypeInfo(name.TypeArgumentList.Arguments[0], cancellationToken).Type
                is INamedTypeSymbol { TypeKind: not TypeKind.Error } leftSymbol
            && rightType is not null)
        {
            var linkNamespace = leftSymbol.ContainingNamespace is { IsGlobalNamespace: false } home ? home.ToDisplayString() + "." : string.Empty;

            if (context.SemanticModel.Compilation.GetTypeByMetadataName(linkNamespace + leftName + rightName) is { TypeKind: TypeKind.Class } link)
            {
                linkDeclared = true;

                foreach (var property in MapCallParser.PublicInstanceProperties(link))
                {
                    // Durum alanı yük değildir: istekle yazılamaz, yalnızca geçişlerle değişir.
                    if (workflow is not null && property.Name == "Status")
                    {
                        statusDeclared = true;

                        if (property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != workflow.StatusType)
                        {
                            problems.Add($"The Status property of '{leftName}{rightName}' must have the status type declared with Workflow<TStatus>()");
                        }
                    }
                    else if (MapCallParser.TryClassify(property.Type, out var kind, out var valueType))
                    {
                        payload.Add(MapCallParser.ToModel(property, kind, valueType));
                    }
                }
            }
        }

        return new AssociationDeclaration(leftName, leftType, rightName, rightType, LocationInfo.From(name.GetLocation()))
        {
            Payload = payload.ToEquatableArray(),
            LinkDeclared = linkDeclared,
            Workflow = workflow,
            StatusDeclared = statusDeclared,
            Problems = problems.ToEquatableArray(),
            ForCurrentUser = ChainReader.Nodes(invocation).Any(node => node.Name.Identifier.ValueText == "ForCurrentUser"),
            CallNamespace = context.SemanticModel.GetEnclosingSymbol(invocation.SpanStart, cancellationToken)
                ?.ContainingNamespace is { IsGlobalNamespace: false } callSiteNamespace
                    ? callSiteNamespace.ToDisplayString()
                    : string.Empty,
        };
    }

    private static (string Name, string? Type) Describe(TypeSyntax syntax, SemanticModel semanticModel, CancellationToken cancellationToken) =>
        semanticModel.GetTypeInfo(syntax, cancellationToken).Type is INamedTypeSymbol { TypeKind: not TypeKind.Error } type
            ? (type.Name, type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            : (syntax.ToString(), null);

    public static bool IsHandlerCandidate(SyntaxNode node) => node is ClassDeclarationSyntax { BaseList: not null };

    public static HandlerModel? ParseHandler(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)context.Node, cancellationToken)
            is not { IsAbstract: false, IsStatic: false, IsGenericType: false } handler)
        {
            return null;
        }

        var services = handler.AllInterfaces
            .Where(candidate => candidate is
            {
                Name: "IActionHandler" or "IQueryHandler" or "ITransitionHandler" or "IEntityActionHandler" or "IJobHandler" or "IDerivation",
                ContainingNamespace: { Name: "Haldus", ContainingNamespace.IsGlobalNamespace: true },
            })
            .Select(service => service.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .OrderBy(service => service, StringComparer.Ordinal)
            .ToArray();

        return services.Length == 0
            ? null
            : new HandlerModel(handler.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), services.ToEquatableArray());
    }
}
