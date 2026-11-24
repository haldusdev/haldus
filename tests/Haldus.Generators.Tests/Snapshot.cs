using System.Runtime.CompilerServices;

namespace Haldus.Generators.Tests;

/// <summary>
/// Üretilen kaynakları <c>Snapshots</c> klasöründeki onaylı dosyalarla karşılaştırır. Fark varsa yanına
/// <c>.received.cs</c> yazar ve testi düşürür; <c>HALDUS_ACCEPT_SNAPSHOTS=1</c> ile çalıştırıldığında
/// yeni çıktıyı onaylı sayar.
/// </summary>
internal static class Snapshot
{
    public static void Verify(
        GeneratorRun run,
        [CallerMemberName] string testName = "",
        [CallerFilePath] string testFile = "")
    {
        var directory = Path.Combine(
            Path.GetDirectoryName(testFile)!, "Snapshots", $"{Path.GetFileNameWithoutExtension(testFile)}.{testName}");
        var accept = Environment.GetEnvironmentVariable("HALDUS_ACCEPT_SNAPSHOTS") == "1";
        var mismatches = new List<string>();
        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(directory);

        foreach (var (name, source) in run.Sources.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var baseName = name[..^".g.cs".Length];
            var verifiedPath = Path.Combine(directory, baseName + ".verified.cs");
            var receivedPath = Path.Combine(directory, baseName + ".received.cs");
            var actual = source.ReplaceLineEndings("\n");
            expectedFiles.Add(verifiedPath);

            if (File.Exists(verifiedPath) && File.ReadAllText(verifiedPath).ReplaceLineEndings("\n") == actual)
            {
                File.Delete(receivedPath);
            }
            else if (accept)
            {
                File.WriteAllText(verifiedPath, actual);
                File.Delete(receivedPath);
            }
            else
            {
                File.WriteAllText(receivedPath, actual);
                mismatches.Add(name);
            }
        }

        // Artık üretilmeyen bir dosyanın onaylı kopyası da bir farktır.
        foreach (var stale in Directory.GetFiles(directory, "*.verified.cs").Where(path => !expectedFiles.Contains(path)))
        {
            if (accept)
            {
                File.Delete(stale);
            }
            else
            {
                mismatches.Add($"{Path.GetFileName(stale)} (no longer generated)");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            $"Generated output differs from the verified snapshot: {string.Join(", ", mismatches)}. " +
            $"Review the .received.cs files in '{directory}' and rerun with HALDUS_ACCEPT_SNAPSHOTS=1 to accept them.");
    }
}
