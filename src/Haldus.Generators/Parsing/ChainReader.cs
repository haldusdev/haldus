using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Haldus.Generators.Parsing;

/// <summary>Bir <c>Map*</c> çağrısını izleyen zinciri okur ve trait ile işlem seçimlerine çevirir.</summary>
internal static class ChainReader
{
    /// <summary>Çalışma zamanında bir şey yapmayan, yalnızca generator'ın okuduğu zincir metotları.</summary>
    public static readonly HashSet<string> DeclarationMethods =
    [
        "MultiTenant",
        "Auditable",
        "SoftDeletable",
        "ConcurrencyChecked",
        "Archivable",
        "Without",
        "Only",
        "Filterable",
        "Searchable",
        "References",
        "Under",
        "Hierarchical",
        "Embeds",
        "AsSubresource",
        "Workflow",
        "EditableIn",
        "DeletableIn",
        "Numbered",
        "Expiring",
        "Idempotent",
    ];

    /// <summary>Zincirdeki çağrıları, Map çağrısından dışa doğru sırayla verir.</summary>
    public static IEnumerable<(SimpleNameSyntax Name, InvocationExpressionSyntax Invocation)> Nodes(InvocationExpressionSyntax mapCall)
    {
        SyntaxNode current = mapCall;

        while (current.Parent is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Expression == current
            && memberAccess.Parent is InvocationExpressionSyntax outer)
        {
            yield return (memberAccess.Name, outer);
            current = outer;
        }
    }

    public static EquatableArray<ChainCall> Read(InvocationExpressionSyntax mapCall) =>
        Nodes(mapCall)
            .Select(node => new ChainCall(
                node.Name.Identifier.ValueText,
                (node.Name is GenericNameSyntax generic
                    ? generic.TypeArgumentList.Arguments.Select(argument => argument.ToString())
                    : []).ToEquatableArray(),
                node.Invocation.ArgumentList.Arguments.Select(argument => argument.Expression.ToString()).ToEquatableArray()))
            .ToEquatableArray();

    /// <returns>
    /// Etkin trait'ler, üretilecek işlemler ve zincirde istenip archetype'ın sunmadığı işlemler
    /// (bunlar sessizce yok sayılmaz, tanı olarak bildirilir).
    /// </returns>
    public static (Traits Traits, Operations Operations, Operations Unsupported) Interpret(
        EquatableArray<ChainCall> chain,
        Operations baseOperations)
    {
        var traits = Traits.None;
        var operations = baseOperations;
        var unsupported = Operations.None;

        foreach (var call in chain)
        {
            switch (call.Name)
            {
                case "MultiTenant":
                    traits |= Traits.MultiTenant;
                    break;
                case "Auditable":
                    traits |= Traits.Auditable;
                    if (call.Arguments.Any(argument => argument == "true"))
                    {
                        traits |= Traits.AuditUser;
                    }

                    break;
                case "SoftDeletable":
                    traits |= Traits.SoftDeletable;
                    break;
                case "ConcurrencyChecked":
                    traits |= Traits.ConcurrencyChecked;
                    break;
                case "Archivable":
                    traits |= Traits.Archivable;
                    break;
                case "Idempotent":
                    traits |= Traits.Idempotent;
                    if (call.Arguments.Any(argument => argument == "true"))
                    {
                        traits |= Traits.IdempotencyRequired;
                    }

                    break;
                case "Without":
                {
                    var requested = ParseOperations(call);
                    unsupported |= requested & ~baseOperations;
                    operations &= ~requested;
                    break;
                }

                case "Only":
                {
                    var requested = ParseOperations(call);
                    unsupported |= requested & ~baseOperations;
                    operations = requested & baseOperations;
                    break;
                }
            }
        }

        return (traits, operations, unsupported);
    }

    /// <summary>
    /// Zincir metodunun bir <c>Map*</c> çağrısına kesintisiz bağlı olup olmadığını söyler. Bir değişken
    /// üzerinden yapılan çağrıyı generator göremez.
    /// </summary>
    public static bool IsRootedAtMapCall(InvocationExpressionSyntax declaration)
    {
        var current = declaration;

        while (current.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax inner })
        {
            if (inner.Expression is MemberAccessExpressionSyntax { Name: var name }
                && MapCallParser.ArchetypeOf(name.Identifier.ValueText) is not null)
            {
                return true;
            }

            current = inner;
        }

        return false;
    }

    private static Operations ParseOperations(ChainCall call)
    {
        var operations = Operations.None;

        foreach (var argument in call.Arguments)
        {
            // Argüman CrudOperation.Delete biçimindedir; son parça işlem adıdır.
            var name = argument.Substring(argument.LastIndexOf('.') + 1);
            if (name != nameof(Operations.All) && Enum.TryParse<Operations>(name, out var operation))
            {
                operations |= operation;
            }
        }

        return operations;
    }
}
