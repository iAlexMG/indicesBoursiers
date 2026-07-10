using System.Reflection;
using System.Runtime.Loader;

namespace Phase0Poc;

/// <summary>
/// Phase 0 — POC BusinessLayer hors du process Quantower.
/// Étape 1 (`dump`) : inventaire réflexif de l'API publique — on MESURE l'API avant
/// d'écrire le moindre code typé (connexion, historique, aggressor, volumétrie).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "dump";
        string bin = QuantowerLocator.HookAssemblyResolution();
        Console.WriteLine($"Bin Quantower : {bin}");

        return mode switch
        {
            "dump" => DumpApi(bin, args.Length > 1 ? args[1] : "api-dump.txt"),
            "connect" => Steps.Connect(bin),
            "rithmic" => Steps.Rithmic(bin),
            "type" => DumpType(bin, args.Length > 1 ? args[1] : "TradingPlatform.BusinessLayer.Strategy"),
            _ => Fail($"Mode inconnu : {mode} (attendu : dump | connect | rithmic | type)"),
        };
    }

    private static int DumpApi(string bin, string outPath)
    {
        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(
            Path.Combine(bin, "TradingPlatform.BusinessLayer.dll"));

        Type[] types;
        try { types = asm.GetExportedTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            Console.WriteLine($"⚠ ReflectionTypeLoadException : {ex.LoaderExceptions.Length} loader errors, {types.Length} types récupérés");
        }

        using var w = new StreamWriter(outPath);
        foreach (var t in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            string kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class";
            w.WriteLine($"### {kind} {t.FullName} : {t.BaseType?.Name}");
            if (t.IsEnum)
            {
                w.WriteLine($"  values: {string.Join(", ", Enum.GetNames(t))}");
                continue;
            }
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var m in t.GetMembers(flags).OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_") ||
                    m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")) continue;
                try { w.WriteLine($"  [{m.MemberType}] {m}"); }
                catch { w.WriteLine($"  [{m.MemberType}] {m.Name} (signature illisible)"); }
            }
        }
        Console.WriteLine($"OK — {types.Length} types publics -> {Path.GetFullPath(outPath)}");
        return 0;
    }

    /// <summary>Réflexion d'UN type avec ses membres virtuels/protégés (les On* surchargeables).</summary>
    private static int DumpType(string bin, string typeName)
    {
        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(
            Path.Combine(bin, "TradingPlatform.BusinessLayer.dll"));
        var t = asm.GetType(typeName);
        if (t is null) { Console.Error.WriteLine($"Type introuvable : {typeName}"); return 1; }

        Console.WriteLine($"### {typeName} : {t.BaseType?.FullName}");
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        // On remonte la hiérarchie pour attraper les virtuels déclarés en base.
        for (var cur = t; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var m in cur.GetMethods(flags | BindingFlags.DeclaredOnly)
                         .Where(m => (m.IsFamily || m.IsPublic) && (m.IsVirtual || m.IsAbstract) && !m.IsSpecialName)
                         .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                string vis = m.IsFamily ? "protected" : "public";
                string mod = m.IsAbstract ? "abstract" : "virtual";
                var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                Console.WriteLine($"  [{cur.Name}] {vis} {mod} {m.ReturnType.Name} {m.Name}({ps})");
            }
        }
        return 0;
    }

    private static int Fail(string msg)
    {
        Console.Error.WriteLine(msg);
        return 1;
    }
}
