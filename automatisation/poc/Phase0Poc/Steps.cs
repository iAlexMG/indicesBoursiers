using TradingPlatform.BusinessLayer;

namespace Phase0Poc;

/// <summary>
/// Étapes typées du POC. Isolées de Program pour que le resolver d'assemblies soit posé
/// avant tout JIT touchant TradingPlatform.BusinessLayer.
/// </summary>
internal static class Steps
{
    /// <summary>
    /// Q1 — Core hors process : Initialize(), découverte des vendors (dont Rithmic) et des
    /// connexions sauvegardées dans les settings Quantower.
    /// </summary>
    internal static int Connect(string bin)
    {
        // Diagnostic : le premier essai s'est terminé exit 0 sans un mot dès Core.Instance.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        { Console.WriteLine("[ProcessExit] déclenché"); Console.Out.Flush(); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        { Console.WriteLine($"[Unhandled] {e.ExceptionObject}"); Console.Out.Flush(); };
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        { Console.WriteLine($"[FirstChance] {e.Exception.GetType().Name}: {e.Exception.Message}"); Console.Out.Flush(); };

        // Les vendors (bin\Vendors\*) et settings sont résolus relativement au process :
        // on se place dans le bin Quantower avant Initialize.
        Environment.CurrentDirectory = bin;

        Step("avant Core.Instance");
        var core = Core.Instance;
        Step("après Core.Instance");
        Console.WriteLine($"Core.CurrentVersion : {core.CurrentVersion}");

        Step("Core.Initialize()...");
        core.Initialize();
        Step("Initialize OK");

        var infos = core.Connections.ConnectionsInfo;
        Console.WriteLine($"ConnectionsInfo : {infos.Length}");
        foreach (var ci in infos)
            Console.WriteLine($"  - '{ci.Name}' | vendor='{ci.VendorName}' | group='{ci.Group}' | creation={ci.CreationType} | state={ci.ConnectionState}");

        var all = core.Connections.All;
        Console.WriteLine($"Connections.All : {all.Length}");
        foreach (var c in all)
            Console.WriteLine($"  - '{c.Name}' | vendor='{c.VendorName}' | state={c.State} | type={c.Type}");

        return 0;
    }

    /// <summary>
    /// Q1 (suite) — connexion Rithmic effective hors process. Le mot de passe stocké
    /// par Quantower ne se déchiffre PAS hors de son contexte (FailedToRestorePassword) :
    /// on le fournit en clair depuis une config locale gitignorée (chemin prévu au cahier des charges).
    /// </summary>
    internal static int Rithmic(string bin)
    {
        Environment.CurrentDirectory = bin;
        var core = Core.Instance;
        Step("Core.Initialize()...");
        core.Initialize();
        Step("Initialize OK");

        var info = core.Connections.ConnectionsInfo
            .FirstOrDefault(ci => ci.Name == "Rithmic" && ci.VendorName == "Rithmic");
        if (info is null) { Console.WriteLine("ConnectionInfo 'Rithmic' introuvable"); return 1; }

        var conn = core.Connections.CreateConnection(info);
        Step($"Connection créée : '{conn.Name}' state={conn.State}");

        // Chemin prévu par le cahier des charges : credentials en config locale gitignorée. On cherche
        // le fichier à côté de l'exe ET dans le dossier source du projet (poc/Phase0Poc).
        string? clearPw = null;
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "credentials.local.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "credentials.local.json")),
        ];
        string? credPath = candidates.FirstOrDefault(File.Exists);
        if (credPath is not null)
        {
            using var jd = System.Text.Json.JsonDocument.Parse(File.ReadAllText(credPath));
            if (jd.RootElement.TryGetProperty("rithmicPassword", out var p)) clearPw = p.GetString();
            Step($"credentials.local.json trouvé (password fourni : {(string.IsNullOrEmpty(clearPw) ? "NON" : "oui")})");
        }
        else Step("Pas de credentials.local.json → connexion impossible (mot de passe requis). Voir README du POC.");

        // On part des settings restaurés par Core (user/server déjà là) et on ne (re)pose
        // que le mot de passe en clair si la config locale en fournit un.
        var settings = conn.Settings;
        var live = settings.ExpandGroups().ToList();
        Console.WriteLine("Settings de la connexion : " +
            string.Join(", ", live.Select(s => $"{s.Name}({s.GetType().Name})")));

        if (clearPw is not null && live.FirstOrDefault(s => s.Name == "Password") is SettingItemPassword pwItem)
        {
            pwItem.SetValueWithReason(new PasswordHolder(clearPw, true, null), SettingItemValueChangingReason.Manually);
            PasswordHolder h = pwItem;
            Step($"Mot de passe injecté depuis config locale : len={h?.Password?.Length ?? 0}");
        }
        Step($"user={Mask(live.FirstOrDefault(s => s.Name == "User")?.GetValue<string>())}");

        conn.Settings = settings;
        conn.ConnectingProgressChanged += (_, e) => Step($"progress: {e.Progress}");

        Step("Connect()...");
        var result = conn.Connect();
        Step($"ConnectionResult : State={result.State}, Message='{result.Message}'");
        Step($"conn.State={conn.State}");

        if (conn.State == ConnectionState.Connected)
        {
            Console.WriteLine($"ServerTime (UTC?) : {conn.ServerTime:o} | kind={conn.ServerTime.Kind}");
            var md = conn.HistoryMetaData;
            Console.WriteLine($"HistoryMetadata : tickTypes=[{string.Join(",", md.AllowedHistoryTypesHistoryAggregationTick ?? [])}] " +
                $"| barTypes=[{string.Join(",", md.AllowedHistoryTypesHistoryAggregationTime ?? [])}] " +
                $"| basePeriods=[{string.Join(",", md.AllowedBasePeriodsHistoryAggregationTime ?? [])}] " +
                $"| stepTick={md.DownloadingStep_Tick} | stepMin={md.DownloadingStep_Minute}");
            Console.WriteLine($"Comptes : {string.Join(", ", core.Accounts.Select(a => a.Name))}");
            conn.Disconnect();
            Step("Déconnecté proprement");
        }
        return conn.State == ConnectionState.Disconnected ? 0 : 1;
    }

    private static string Mask(string? s)
        => string.IsNullOrEmpty(s) ? "(vide)" : s[..Math.Min(5, s.Length)] + "***";

    private static void Step(string msg)
    {
        Console.WriteLine($"[step] {msg}");
        Console.Out.Flush();
    }
}
