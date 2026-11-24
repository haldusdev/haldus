using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Haldus.Generators.Parsing;

/// <summary>
/// <c>AddHaldus&lt;AppDbContext&gt;()</c> çağrısını bulur. Bağlam aynı projede ve partial ise Haldus
/// ona DbSet özellikleri ekleyebilir.
/// </summary>
internal static class DbContextParser
{
    private const string ServiceExtensionsType = "Haldus.HaldusServiceCollectionExtensions";

    public static bool IsCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { Identifier.ValueText: "AddHaldus", TypeArgumentList.Arguments.Count: 1 },
            },
        };

    public static DbContextModel? Parse(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method
            || method.ContainingType?.ToDisplayString() != ServiceExtensionsType
            || method.TypeArguments[0] is not INamedTypeSymbol { ContainingType: null } dbContext
            || dbContext.DeclaringSyntaxReferences.Length == 0)
        {
            return null;
        }

        foreach (var reference in dbContext.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(cancellationToken) is not ClassDeclarationSyntax declaration
                || !declaration.Modifiers.Any(modifier => modifier.ValueText == "partial"))
            {
                return null;
            }
        }

        var containingNamespace = dbContext.ContainingNamespace is { IsGlobalNamespace: false } declared
            ? declared.ToDisplayString()
            : string.Empty;

        return new DbContextModel(
            dbContext.Name,
            containingNamespace,
            dbContext.GetMembers().Select(member => member.Name).Distinct().ToEquatableArray());
    }
}
