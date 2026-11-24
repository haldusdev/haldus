using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Haldus.Generators.Parsing;

/// <summary>Entity'ye bağlı olmayan işlemleri okur: <c>MapQuery</c> çağrıları ve handler sınıfları.</summary>
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
                Name: "IActionHandler" or "IQueryHandler",
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
