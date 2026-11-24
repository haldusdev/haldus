using System.Text;

namespace Haldus.Generators.Infrastructure;

internal static class Naming
{
    public static string Pluralize(string name)
    {
        if (name.Length > 1 && name.EndsWith("y", StringComparison.Ordinal) && !IsVowel(name[name.Length - 2]))
        {
            return name.Substring(0, name.Length - 1) + "ies";
        }

        if (name.EndsWith("s", StringComparison.Ordinal)
            || name.EndsWith("x", StringComparison.Ordinal)
            || name.EndsWith("z", StringComparison.Ordinal)
            || name.EndsWith("ch", StringComparison.Ordinal)
            || name.EndsWith("sh", StringComparison.Ordinal))
        {
            return name + "es";
        }

        return name + "s";
    }

    /// <summary><c>OrderItems</c> → <c>order-items</c>.</summary>
    public static string ToKebabCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);

        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsUpper(character) && index > 0 && !char.IsUpper(name[index - 1]))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static bool IsVowel(char character) => "aeiouAEIOU".IndexOf(character) >= 0;
}
