using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Haldus.Generators.Parsing;

/// <summary>
/// <c>orders.MapTransition(...)</c> çağrılarını okur. Geçişin sahibi, tıpkı <c>MapDetail</c>'de olduğu gibi,
/// çağrının yapıldığı builder'ın tip argümanından anlaşılır; bu yüzden bir değişken üzerinden çağrılabilir.
/// Durumların ve adın geçerliliği, sahip kaynak bilindikten sonra bağlama adımında doğrulanır.
/// </summary>
internal static class TransitionParser
{
    private const string BuilderType = "Haldus.ResourceBuilder<TEntity>";

    public static bool IsCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "MapTransition" or "MapAction" },
        };

    public static TransitionDeclaration? Parse(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var access = (MemberAccessExpressionSyntax)invocation.Expression;
        var semanticModel = context.SemanticModel;

        if (semanticModel.GetTypeInfo(access.Expression, cancellationToken).Type
                is not INamedTypeSymbol { TypeArguments: { Length: 1 } owners } receiver
            || receiver.OriginalDefinition.ToDisplayString() != BuilderType)
        {
            return null;
        }

        var owner = owners[0];
        var isAction = access.Name.Identifier.ValueText == "MapAction";
        INamedTypeSymbol? request = null;
        ITypeSymbol? response = null;

        if (access.Name is GenericNameSyntax { TypeArgumentList.Arguments: { Count: >= 1 } typeArguments })
        {
            request = semanticModel.GetTypeInfo(typeArguments[0], cancellationToken).Type as INamedTypeSymbol;

            if (request is null or { TypeKind: TypeKind.Error })
            {
                // İstek tipi çözülemiyorsa derleyici zaten hata verir.
                return null;
            }

            // İkinci tip argümanı yalnızca aksiyonda vardır: aksiyonun kendi sonucu.
            if (typeArguments.Count == 2)
            {
                response = semanticModel.GetTypeInfo(typeArguments[1], cancellationToken).Type;

                if (response is null or { TypeKind: TypeKind.Error })
                {
                    return null;
                }
            }
        }

        string? name = null;
        var nameIsConstant = true;
        var from = new List<StateReference>();
        StateReference? to = null;
        var statesReadable = true;
        var arguments = invocation.ArgumentList.Arguments;

        for (var index = 0; index < arguments.Count; index++)
        {
            var expression = arguments[index].Expression;

            var isText = semanticModel.GetTypeInfo(expression, cancellationToken).ConvertedType?.SpecialType == SpecialType.System_String;

            switch (ParameterOf(arguments[index], index, typed: request is not null, isText, isAction))
            {
                case "name" when !expression.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression):
                    if (semanticModel.GetConstantValue(expression, cancellationToken) is { HasValue: true, Value: string text })
                    {
                        name = text;
                    }
                    else
                    {
                        nameIsConstant = false;
                    }

                    break;

                case "from":
                    var elements = expression is CollectionExpressionSyntax collection
                        ? collection.Elements.Select(element => (element as ExpressionElementSyntax)?.Expression)
                        : [expression];

                    foreach (var element in elements)
                    {
                        if (element is not null && LifecycleReader.StateOf(semanticModel, element, cancellationToken) is { } state)
                        {
                            from.Add(state);
                        }
                        else
                        {
                            statesReadable = false;
                        }
                    }

                    break;

                case "to":
                    to = LifecycleReader.StateOf(semanticModel, expression, cancellationToken);
                    statesReadable &= to is not null;
                    break;
            }
        }

        return new TransitionDeclaration(
            owner.Name,
            owner is IErrorTypeSymbol ? null : owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            name,
            nameIsConstant,
            request?.Name,
            request?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ImplementsTransition: request?.AllInterfaces.Any(candidate =>
                candidate.OriginalDefinition.ToDisplayString() == (isAction ? "Haldus.IEntityAction<TEntity>" : "Haldus.ITransition<TEntity>")
                && candidate.TypeArguments[0].Name == owner.Name) == true,
            from.ToEquatableArray(),
            to,
            isAction || (statesReadable && from.Count > 0 && to is not null),
            LocationInfo.From(access.Name.GetLocation()))
        {
            IsAction = isAction,
            ResponseType = response?.ToDisplayString(MapCallParser.TypeFormat),
        };
    }

    /// <summary>
    /// Argümanın hangi parametreye verildiğini bulur. Adlandırılmış argümanda ad yeterlidir; sıralı argümanda
    /// sıra, iki çağrı biçiminin imzasından bilinir. İstek tipli biçimde üçüncü sıradaki argüman ya iş
    /// mantığıdır ya da addır; hangisi olduğunu tipi söyler.
    /// </summary>
    private static string ParameterOf(ArgumentSyntax argument, int index, bool typed, bool isText, bool isAction)
    {
        if (argument.NameColon is { } named)
        {
            return named.Name.Identifier.ValueText;
        }

        if (isAction)
        {
            // Aksiyonun durumu yoktur: gövdesiz biçim (ad, mantık), istek tipli biçim ([mantık], [ad]).
            return typed
                ? index == 0 && !isText ? "apply" : "name"
                : index == 0 ? "name" : "apply";
        }

        if (!typed)
        {
            return index switch { 0 => "name", 1 => "from", 2 => "to", _ => "apply" };
        }

        return index switch
        {
            0 => "from",
            1 => "to",
            _ => isText ? "name" : "apply",
        };
    }
}
