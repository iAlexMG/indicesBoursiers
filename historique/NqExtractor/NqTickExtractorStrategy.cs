using System.Data.SQLite;
using System.Globalization;
using TradingPlatform.BusinessLayer;

namespace NqTickExtractor;

/// <summary>
/// Phase 1 — extracteur incrémental de ticks NQ (Rithmic) vers SQLite, au schéma EXACT du
/// projet frère (`trades(trade_id PK, ts, price, size, side)` + `_meta` k/v + `_ingested`),
/// pour que toute la chaîne Python aval (candles.py, volume_profile_features.py) fonctionne
/// telle quelle. Tourne DANS Quantower (connexion Rithmic live). La profondeur tick Rithmic est
/// LIMITÉE (Phase 0 : « ≥ 20 jours, probablement plus ») → une SONDE ARRIÈRE mesure la limite
/// réelle au premier run et aspire tout ; ensuite, lancer quotidiennement pour accumuler
/// l'historique irrécupérable autrement.
///
/// Idempotent : les jours complets passés sont marqués dans `_ingested` (jamais re-téléchargés) ;
/// le jour courant (partiel) et le reliquat non marqué sont supprimés puis ré-insérés à chaque
/// run. Insertion en ordre chronologique → `trade_id` (rowid) croissant = hypothèse de candles.py
/// (après un backfill arrière, la table est réordonnancée pour préserver ce contrat).
/// </summary>
public sealed class NqTickExtractorStrategy : Strategy
{
    [InputParameter("Symbole (NQ front)", 0)]
    public Symbol? Instrument { get; set; }

    [InputParameter("Base SQLite (vide = auto F:\\data\\NQ-<contrat>.db)", 1)]
    public string DbPath = "";

    [InputParameter("Sonde arrière max (jours)", 2, 1, 365, 1, 0)]
    public int MaxBackfillDays = 90;

    [InputParameter("Sonde : arrêt après N jours vides consécutifs", 3, 1, 30, 1, 0)]
    public int EmptyDaysStop = 7;

    [InputParameter("Collecte auto toutes les N heures (0 = one-shot)", 4, 0, 24, 1, 0)]
    public int IntervalHours = 6;

    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private System.Threading.Timer? _timer;
    private readonly object _lock = new();
    private volatile bool _busy;

    public NqTickExtractorStrategy() => Name = "NQ Tick Extractor";

    protected override void OnRun()
    {
        RunOnce();
        if (IntervalHours > 0)
        {
            var period = TimeSpan.FromHours(IntervalHours);
            _timer = new System.Threading.Timer(_ => RunOnce(), null, period, period);
            this.LogInfo($"Collecte automatique ACTIVE : toutes les {IntervalHours} h tant que "
                       + "la stratégie tourne (Quantower ouvert + Rithmic connecté). Laisser en Working.");
        }
        else { this.Stop(); } // mode one-shot
    }

    protected override void OnStop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Une passe de collecte, protégée contre le recouvrement (timer + démarrage).</summary>
    private void RunOnce()
    {
        if (_busy) return;
        lock (_lock)
        {
            _busy = true;
            try { Extract(); }
            catch (Exception ex) { this.LogError($"Extracteur EXCEPTION : {ex}"); }
            finally { _busy = false; }
        }
    }

    private void Extract()
    {
        var s = Instrument;
        if (s is null) { this.LogError("Aucun symbole sélectionné (choisir NQ)."); return; }

        string dbPath = ResolveDbPath(s);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        this.LogInfo($"Base : {dbPath} | symbole {s.Name} ({s.Id})");

        string cs = new SQLiteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SQLiteConnection(cs);
        conn.Open();
        Pragmas(conn);
        EnsureSchema(conn);
        WriteMeta(conn, s);

        // CANARI : re-télécharger un jour connu non vide. Si le serveur d'historique est muet
        // (maintenance week-end Rithmic…), on N'ÉCRIT RIEN : ni purge, ni marquage, ni sonde —
        // la base reste intacte et la passe sera retentée au prochain cycle.
        if (!ServeurHistoriqueVivant(conn, s))
        {
            this.LogInfo("⚠️ Serveur d'historique MUET (maintenance ?) : passe annulée, base intacte. "
                       + "Nouvel essai au prochain cycle.");
            LogFooter(conn);
            return;
        }

        // Jour de reprise : lendemain du dernier jour complet marqué, sinon aujourd'hui
        // (la sonde ARRIÈRE, plus bas, va chercher toute la profondeur disponible).
        var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        DateTime fromDay = LastIngestedDay(conn) is DateTime last ? last.AddDays(1) : today;

        long grand = 0;
        if (fromDay <= today)
        {
            for (var day = fromDay; day <= today; day = day.AddDays(1))
            {
                var dayEnd = day.AddDays(1);
                int rows = IngestDay(conn, s, day, dayEnd);
                grand += rows;
                bool complete = dayEnd <= today; // jour entièrement passé
                // Marquage prudent : jamais sur « 0 reçu alors qu'on avait des données »
                // (serveur tombé en cours de passe) — le jour sera retenté.
                if (complete && (rows > 0 || !ADesLignes(conn, day, dayEnd)))
                    MarkDay(conn, day, rows);
                this.LogInfo($"{day:yyyy-MM-dd} : {rows,8} ticks{(complete ? " [marqué]" : " [courant, partiel]")}"
                           + (rows == 0 ? " (0 reçu : existant conservé)" : ""));
            }
            this.LogInfo($"Collecte avant : +{grand} ticks sur cette passe.");
        }
        else this.LogInfo("Collecte avant : déjà à jour.");

        grand += ProbeBackward(conn, s, today);

        EnsureTsIndex(conn);
        this.LogInfo($"Extraction terminée : +{grand} ticks sur cette passe.");
        LogFooter(conn);
    }

    /// <summary>
    /// Sonde ARRIÈRE : descend jour par jour SOUS le plus ancien tick en base et aspire tout
    /// ce que Rithmic sert encore, jusqu'à `EmptyDaysStop` jours vides consécutifs (week-ends
    /// et fériés compris, d'où le défaut 7) ou `MaxBackfillDays` jours sondés. La profondeur
    /// mesurée est mémorisée dans _meta (tick_probe_oldest) → jamais re-sondée : la fenêtre
    /// Rithmic est glissante, l'ancien ne réapparaît pas.
    /// Les jours arrière étant insérés APRÈS des jours plus récents, la table est ensuite
    /// RÉORDONNANCÉE (rowid croissant = chrono, contrat de candles.py / features_vp).
    /// </summary>
    private long ProbeBackward(SQLiteConnection conn, Symbol s, DateTime today)
    {
        if (MetaGet(conn, "tick_probe_oldest") is not null) return 0; // déjà mesurée

        DateTime oldestKnown;
        using (var cmd = new SQLiteCommand("SELECT MIN(ts) FROM trades", conn))
            oldestKnown = cmd.ExecuteScalar() is long ms ? FromMs(ms).Date : today;

        int emptyRun = 0, probed = 0;
        long grand = 0;
        DateTime? oldestServed = null;
        for (var day = oldestKnown.AddDays(-1);
             emptyRun < EmptyDaysStop && probed < MaxBackfillDays;
             day = day.AddDays(-1), probed++)
        {
            var d = DateTime.SpecifyKind(day, DateTimeKind.Utc);
            int rows = IngestDay(conn, s, d, d.AddDays(1));
            grand += rows;
            MarkDay(conn, d, rows);
            if (rows == 0) { emptyRun++; this.LogInfo($"{d:yyyy-MM-dd} :        0 tick  (vide {emptyRun}/{EmptyDaysStop})"); continue; }
            emptyRun = 0;
            oldestServed = d;
            this.LogInfo($"{d:yyyy-MM-dd} : {rows,8} ticks [marqué, arrière]");
        }

        if (emptyRun >= EmptyDaysStop)
        {
            var borne = (oldestServed ?? oldestKnown);
            MetaSet(conn, "tick_probe_oldest", borne.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            this.LogInfo($"PROFONDEUR TICKS MESURÉE : plus ancien jour servi = {borne:yyyy-MM-dd} "
                       + $"({(today - borne).TotalDays:F0} jours). Sonde close (ne sera plus relancée).");
        }
        else if (probed >= MaxBackfillDays)
            this.LogInfo($"Sonde arrêtée à la borne MaxBackfillDays ({MaxBackfillDays} j) — il y a peut-être plus ancien ; relancer avec une borne plus large.");

        if (grand > 0)
        {
            this.LogInfo($"Sonde arrière : +{grand} ticks → réordonnancement chronologique de la table…");
            ResortChronologically(conn);
        }
        return grand;
    }

    /// <summary>Reconstruit `trades` triée par ts (rowid croissant = chrono, contrat aval).</summary>
    private void ResortChronologically(SQLiteConnection c)
    {
        using var tx = c.BeginTransaction();
        Exec(c, "DROP TABLE IF EXISTS trades_sorted");
        Exec(c, @"CREATE TABLE trades_sorted(
                    trade_id INTEGER PRIMARY KEY,
                    ts       INTEGER NOT NULL,
                    price    REAL    NOT NULL,
                    size     REAL    NOT NULL,
                    side     TEXT    NOT NULL)");
        Exec(c, "INSERT INTO trades_sorted(ts,price,size,side) "
              + "SELECT ts,price,size,side FROM trades ORDER BY ts, trade_id");
        Exec(c, "DROP TABLE trades");
        Exec(c, "ALTER TABLE trades_sorted RENAME TO trades");
        tx.Commit();
        EnsureTsIndex(c); // l'index est tombé avec l'ancienne table
    }

    /// <summary>Canari : le dernier jour non vide déjà ingéré doit re-répondre.
    /// Premier run (rien d'ingéré) : pas de référence, on laisse passer.</summary>
    private bool ServeurHistoriqueVivant(SQLiteConnection conn, Symbol s)
    {
        using var cmd = new SQLiteCommand(
            "SELECT name FROM _ingested WHERE name LIKE 'day/%' AND rows > 0 "
          + "ORDER BY name DESC LIMIT 1", conn);
        if (cmd.ExecuteScalar() is not string name) return true;
        if (!DateTime.TryParse(name.Substring(4), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return true;
        var day = DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);
        HistoricalData? hd = null;
        try
        {
            hd = s.GetTickHistory(HistoryType.Last, day, day.AddDays(1));
            if (hd is not null)
                foreach (var raw in hd)
                    if (raw is HistoryItemLast) return true; // premier tick suffit
        }
        finally { hd?.Dispose(); }
        return false;
    }

    private static bool ADesLignes(SQLiteConnection c, DateTime de, DateTime a)
    {
        using var cmd = new SQLiteCommand("SELECT 1 FROM trades WHERE ts >= @a AND ts < @b LIMIT 1", c);
        cmd.Parameters.AddWithValue("@a", ToMs(de));
        cmd.Parameters.AddWithValue("@b", ToMs(a));
        return cmd.ExecuteScalar() is not null;
    }

    private void MarkDay(SQLiteConnection conn, DateTime day, int rows)
    {
        using var mk = new SQLiteCommand("INSERT OR REPLACE INTO _ingested VALUES(@n,@r,@a)", conn);
        mk.Parameters.AddWithValue("@n", $"day/{day:yyyy-MM-dd}");
        mk.Parameters.AddWithValue("@r", rows);
        mk.Parameters.AddWithValue("@a", DateTime.UtcNow.ToString("o"));
        mk.ExecuteNonQuery();
    }

    private static string? MetaGet(SQLiteConnection c, string k)
    {
        using var cmd = new SQLiteCommand("SELECT v FROM _meta WHERE k=@k", c);
        cmd.Parameters.AddWithValue("@k", k);
        return cmd.ExecuteScalar() as string;
    }

    private static void MetaSet(SQLiteConnection c, string k, string v)
    {
        using var cmd = new SQLiteCommand("INSERT OR REPLACE INTO _meta VALUES(@k,@v)", c);
        cmd.Parameters.AddWithValue("@k", k);
        cmd.Parameters.AddWithValue("@v", v);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Télécharge un jour de ticks Last puis REMPLACE la plage en base — uniquement si
    /// le téléchargement a rapporté quelque chose (0 reçu = on ne touche pas à l'existant).</summary>
    private int IngestDay(SQLiteConnection conn, Symbol s, DateTime dayStart, DateTime dayEnd)
    {
        var ticks = new List<(long ts, double price, double size, string side)>();
        HistoricalData? hd = null;
        try
        {
            hd = s.GetTickHistory(HistoryType.Last, dayStart, dayEnd);
            foreach (var raw in hd)
            {
                if (raw is not HistoryItemLast t) continue;
                string side = t.AggressorFlag switch
                {
                    AggressorFlag.Buy => "buy",
                    AggressorFlag.Sell => "sell",
                    _ => "", // agresseur inconnu (0,0002 % mesuré) : exclu, jamais de side vide en base
                };
                if (side.Length == 0) continue;
                ticks.Add((ToMs(t.TimeLeft), t.Price, t.Volume, side));
            }
        }
        finally { hd?.Dispose(); }

        if (ticks.Count == 0) return 0; // rien reçu -> on conserve l'existant tel quel

        ticks.Sort((a, b) => a.ts.CompareTo(b.ts)); // rowid croissant = chrono (hypothèse candles.py)

        using var tx = conn.BeginTransaction();
        // Remplacement de la plage : purge APRÈS un téléchargement réussi seulement. Pour le
        // jour courant/reliquat, ces lignes sont la QUEUE de la table (rowids les plus hauts)
        // → ré-insertion en append conserve l'ordre chronologique.
        using (var del = new SQLiteCommand("DELETE FROM trades WHERE ts >= @a AND ts < @b", conn, tx))
        {
            del.Parameters.AddWithValue("@a", ToMs(dayStart));
            del.Parameters.AddWithValue("@b", ToMs(dayEnd));
            del.ExecuteNonQuery();
        }
        // trade_id NON spécifié → rowid = max+1 : append, ordre chronologique préservé.
        using var cmd = new SQLiteCommand(
            "INSERT INTO trades(ts,price,size,side) VALUES(@ts,@p,@sz,@sd)", conn, tx);
        var pTs = cmd.Parameters.Add("@ts", System.Data.DbType.Int64);
        var pP = cmd.Parameters.Add("@p", System.Data.DbType.Double);
        var pSz = cmd.Parameters.Add("@sz", System.Data.DbType.Double);
        var pSd = cmd.Parameters.Add("@sd", System.Data.DbType.String);
        foreach (var (ts, price, size, side) in ticks)
        {
            pTs.Value = ts; pP.Value = price; pSz.Value = size; pSd.Value = side;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return ticks.Count;
    }

    // --- SQLite : schéma identique au frère --------------------------------------------- //
    private static void Pragmas(SQLiteConnection c)
    {
        foreach (var p in new[] { "journal_mode=WAL", "synchronous=NORMAL", "cache_size=-262144" })
            using (var cmd = new SQLiteCommand($"PRAGMA {p}", c)) cmd.ExecuteNonQuery();
    }

    private static void EnsureSchema(SQLiteConnection c)
    {
        Exec(c, @"CREATE TABLE IF NOT EXISTS trades(
                    trade_id INTEGER PRIMARY KEY,
                    ts       INTEGER NOT NULL,
                    price    REAL    NOT NULL,
                    size     REAL    NOT NULL,
                    side     TEXT    NOT NULL)");
        Exec(c, "CREATE TABLE IF NOT EXISTS _ingested(name TEXT PRIMARY KEY, rows INTEGER, at TEXT)");
        Exec(c, "CREATE TABLE IF NOT EXISTS _meta(k TEXT PRIMARY KEY, v TEXT)");
    }

    private static void EnsureTsIndex(SQLiteConnection c)
        => Exec(c, "CREATE INDEX IF NOT EXISTS idx_trades_ts ON trades(ts)");

    private void WriteMeta(SQLiteConnection c, Symbol s)
    {
        double tick = s.TickSize;
        double tickCost = s.GetTickCost(1);
        double mult = tick != 0 ? tickCost / tick : 0; // NQ : 5/0.25 = 20 $/pt
        var meta = new (string k, string v)[]
        {
            ("symbol", s.Root ?? s.Name),                 // "NQ" — candles.py lit cette clé
            ("market", s.Exchange?.ExchangeName ?? "CME"),// candles.py lit cette clé
            ("exchange", s.Exchange?.ExchangeName ?? "CME"),
            ("contract", s.Name),
            ("expiration", s.ExpirationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("tick_size", tick.ToString(CultureInfo.InvariantCulture)),
            ("multiplier", mult.ToString(CultureInfo.InvariantCulture)),
            ("tick_value", tickCost.ToString(CultureInfo.InvariantCulture)),
            ("source", "rithmic"),
        };
        foreach (var (k, v) in meta)
            using (var cmd = new SQLiteCommand("INSERT OR REPLACE INTO _meta VALUES(@k,@v)", c))
            { cmd.Parameters.AddWithValue("@k", k); cmd.Parameters.AddWithValue("@v", v); cmd.ExecuteNonQuery(); }
    }

    private DateTime? LastIngestedDay(SQLiteConnection c)
    {
        using var cmd = new SQLiteCommand(
            "SELECT name FROM _ingested WHERE name LIKE 'day/%' ORDER BY name DESC LIMIT 1", c);
        if (cmd.ExecuteScalar() is string name &&
            DateTime.TryParse(name.Substring(4), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);
        return null;
    }

    private void LogFooter(SQLiteConnection c)
    {
        using var cmd = new SQLiteCommand("SELECT COUNT(*), MIN(ts), MAX(ts) FROM trades", c);
        using var r = cmd.ExecuteReader();
        if (r.Read() && !r.IsDBNull(1))
            this.LogInfo($"Base : {r.GetInt64(0)} ticks | {FromMs(r.GetInt64(1)):o} → {FromMs(r.GetInt64(2)):o}");
    }

    private string ResolveDbPath(Symbol s)
    {
        if (!string.IsNullOrWhiteSpace(DbPath)) return DbPath;
        string tag = s.ExpirationDate.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return $@"F:\data\NQ-{tag}.db";
    }

    private static void Exec(SQLiteConnection c, string sql)
    { using var cmd = new SQLiteCommand(sql, c); cmd.ExecuteNonQuery(); }

    private static long ToMs(DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        return (long)(utc - Epoch).TotalMilliseconds;
    }

    private static DateTime FromMs(long ms) => Epoch.AddMilliseconds(ms);
}
