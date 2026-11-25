using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Haldus.Docs.Tests;

/// <summary>Belgelerdeki bir C# kod bloğu ve onu niteleyen işaret.</summary>
/// <param name="File">Deponun köküne göre dosya yolu.</param>
/// <param name="Line">Bloğun açıldığı satır (1'den başlar).</param>
/// <param name="Examples">Bloğun ait olduğu örneklerin adları; aynı dosyada aynı adı taşıyan bloklar birlikte derlenir. Bir blok birden çok örneğe girebilir (eğitimdeki ortak bir entity gibi).</param>
/// <param name="ExpectedError">Örnek bilerek hatalıysa beklenen tanının kodu (<c>HALDUS004</c>).</param>
/// <param name="IsFragment">Blok tek başına derlenmeyen bir parçadır ve bilerek derlenmez.</param>
public sealed record DocBlock(string File, int Line, string Code, IReadOnlyList<string> Examples, string? ExpectedError, bool IsFragment)
{
    public IEnumerable<string> Ids => Examples.Select(example => $"{File}#{example}");
}

/// <summary>
/// Depodaki Markdown belgelerini okur. Bir C# bloğunun hemen önündeki satır bir HTML yorumuysa blok onunla
/// nitelenir: <c>&lt;!-- example: ad --&gt;</c> (birden çok ad boşlukla ayrılır), <c>&lt;!-- example: ad, error: HALDUS004 --&gt;</c> veya
/// <c>&lt;!-- fragment --&gt;</c>. Yorumlar GitHub'da görünmez.
/// </summary>
public static partial class DocBlocks
{
    private static readonly Lazy<IReadOnlyList<DocBlock>> Blocks = new(Read);

    public static string Root { get; } = RepositoryRoot();

    public static IReadOnlyList<DocBlock> All() => Blocks.Value;

    /// <summary>Örnekleri denetlenen belgeler: kökteki README ve CONTRIBUTING, <c>docs</c> ile örneklerin README'leri.</summary>
    public static IEnumerable<string> Files() =>
        new[] { "README.md", "CONTRIBUTING.md" }
            .Select(file => Path.Combine(Root, file))
            .Where(File.Exists)
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "docs"), "*.md", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "samples"), "README.md", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            .Where(path => Only is null || Only.Any(prefix => Relative(path).StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// <c>HALDUS_DOCS_ONLY</c> (virgülle ayrılmış yol önekleri, ör. <c>docs/guides/jobs.md</c>) verilirse yalnızca o
    /// belgeler denetlenir; bir sayfa yazılırken onun örnekleri tek başına sınanır.
    /// </summary>
    public static IReadOnlyList<string>? Only { get; } =
        Environment.GetEnvironmentVariable("HALDUS_DOCS_ONLY") is { Length: > 0 } only
            ? only.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : null;

    private static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    private static IReadOnlyList<DocBlock> Read()
    {
        var blocks = new List<DocBlock>();

        foreach (var path in Files())
        {
            var relative = Relative(path);
            var lines = File.ReadAllLines(path);
            string? marker = null;

            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var fence = Fence().Match(lines[i]);

                if (!fence.Success)
                {
                    if (trimmed.Length > 0)
                    {
                        marker = trimmed.StartsWith("<!--", StringComparison.Ordinal) ? trimmed : null;
                    }

                    continue;
                }

                var indent = fence.Groups["indent"].Value.Length;
                var ticks = fence.Groups["ticks"].Value;
                var language = fence.Groups["lang"].Value.Trim().ToLowerInvariant();
                var start = i;
                var code = new StringBuilder();

                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(ticks, StringComparison.Ordinal); i++)
                {
                    code.AppendLine(lines[i].Length >= indent && string.IsNullOrWhiteSpace(lines[i][..indent]) ? lines[i][indent..] : lines[i].TrimStart());
                }

                if (language is "csharp" or "cs" or "c#")
                {
                    var example = marker is null ? null : Example().Match(marker);
                    blocks.Add(new DocBlock(
                        relative,
                        start + 1,
                        code.ToString(),
                        example is { Success: true } ? example.Groups["names"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [],
                        example is { Success: true } && example.Groups["error"].Success ? example.Groups["error"].Value : null,
                        marker is not null && Fragment().IsMatch(marker)));
                }

                marker = null;
            }
        }

        return blocks;
    }

    private static string RepositoryRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));

    [GeneratedRegex(@"^(?<indent>\s*)(?<ticks>`{3,})(?<lang>[^`]*)$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^<!--\s*example:\s*(?<names>[\w\-]+(\s+[\w\-]+)*)\s*(,\s*error:\s*(?<error>HALDUS\d{3})\s*)?-->$")]
    private static partial Regex Example();

    [GeneratedRegex(@"^<!--\s*fragment\s*-->$")]
    private static partial Regex Fragment();
}
