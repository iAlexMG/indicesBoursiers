using System.Runtime.Loader;

namespace Phase0Poc;

/// <summary>
/// Résolution dynamique du bin Quantower (dossier v* le plus récent) et chargement
/// des dépendances du BusinessLayer à l'exécution (Private=false : rien n'est copié).
/// </summary>
internal static class QuantowerLocator
{
    internal const string Root = @"C:\Quantower\TradingPlatform";

    internal static string ResolveBin()
    {
        var bin = Directory.GetDirectories(Root, "v*")
            .Select(d => new { Dir = d, Ver = ParseVersion(Path.GetFileName(d)) })
            .Where(x => x.Ver is not null)
            .OrderByDescending(x => x.Ver)
            .Select(x => Path.Combine(x.Dir, "bin"))
            .FirstOrDefault(Directory.Exists);
        return bin ?? throw new DirectoryNotFoundException($"Aucun dossier v*\\bin sous {Root}");
    }

    private static Version? ParseVersion(string name)
        => Version.TryParse(name.TrimStart('v', 'V'), out var v) ? v : null;

    /// <summary>À appeler AVANT tout usage d'un type du BusinessLayer.</summary>
    internal static string HookAssemblyResolution()
    {
        string bin = ResolveBin();
        // Dossiers de probing de Quantower (mesuré : Pkcs 4.0.3.1 vit dans bin\System).
        string[] probeDirs =
        [
            bin,
            Path.Combine(bin, "System"),
            Path.Combine(bin, "runtimes", "win", "lib", "net8.0"),
        ];
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            foreach (var dir in probeDirs)
            {
                string candidate = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(candidate))
                    return ctx.LoadFromAssemblyPath(candidate);
            }
            return null;
        };
        return bin;
    }
}
