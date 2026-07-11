using System.Data.SQLite;
using System.Globalization;
using TradingPlatform.BusinessLayer;

namespace NqTickExtractor;

/// <summary>
/// Extracteur de BARRES MINUTE NQ (Rithmic) vers SQLite — le complément « profondeur » de
/// l'extracteur de ticks : Rithmic ne sert que ~2-3 semaines de ticks, mais les barres
/// agrégées affichées sur le graphique remontent beaucoup plus loin. Cette stratégie les
/// télécharge (HistoryType.Last, Period minute) aussi loin que le serveur en fournit,
/// mesurant au passage la profondeur réelle.
///
/// Base produite : F:\data\NQ-&lt;contrat&gt;-1m.db
///   bars(ts PK ms UTC ouverture, open, high, low, close, volume, ticks) + _meta + _ingested.
/// Pas de côté agresseur dans une barre : le footprint/VP reste réservé à la fenêtre de
/// ticks ; ces barres étendent l'OHLCV (backtests SMA/MACD/RSI…) via normalize_ohlcv.py
/// --bars-db (les barres issues des ticks restent prioritaires sur le recouvrement).
///
/// Idempotent : mois complets marqués dans _ingested ('month/YYYY-MM'), mois courant purgé
/// puis ré-inséré à chaque run. Base vide → sonde VERS L'ARRIÈRE depuis le mois courant
/// jusqu'à N mois vides consécutifs (fin des données serveur) ou MaxBackfillYears.
/// </summary>
public sealed class NqBarsExtractorStrategy : Strategy
{
    [InputParameter("Symbole (NQ front)", 0)]
    public Symbol? Instrument { get; set; }

    [InputParameter("Base SQLite (vide = auto F:\\data\\NQ-<contrat>-1m.db)", 1)]
    public string DbPath = "";

    [InputParameter("Période (minutes)", 2, 1, 60, 1, 0)]
    public int PeriodMinutes = 1;

    [InputParameter("Sonde max vers l'arrière (années)", 3, 1, 20, 1, 0)]
    public int MaxBackfillYears = 6;

    [InputParameter("Arrêt après N mois vides consécutifs", 4, 1, 12, 1, 0)]
    public int EmptyMonthsStop = 3;

    [InputParameter("Collecte auto toutes les N heures (0 = one-shot)", 5, 0, 24, 1, 0)]
    public int IntervalHours = 6;

    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private System.Threading.Timer? _timer;
    private readonly object _lock = new();
    private volatile bool _busy;

    public NqBarsExtractorStrategy() => Name = "NQ Bars Extractor";

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

    private void RunOnce()
    {
        if (_busy) return;
        lock (_lock)
        {
            _busy = true;
            try { Extract(); }
            catch (Exception ex) { this.LogError($"Extracteur barres EXCEPTION : {ex}"); }
            finally { _busy = false; }
        }
    }

    private void Extract()
    {
        var s = Instrument;
        if (s is null) { this.LogError("Aucun symbole sélectionné (choisir NQ)."); return; }

        string dbPath = ResolveDbPath(s);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        this.LogInfo($"Base : {dbPath} | symbole {s.Name} ({s.Id}) | période {PeriodMinutes} min");

        string cs = new SQLiteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SQLiteConnection(cs);
        conn.Open();
        Pragmas(conn);
        EnsureSchema(conn);
        WriteMeta(conn, s);

        var now = DateTime.UtcNow;
        var thisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // CANARI : re-télécharger une donnée connue non vide. Si le serveur d'historique est
        // muet (maintenance week-end Rithmic…), on N'ÉCRIT RIEN : ni purge, ni marquage, ni
        // sonde — la base reste intacte et la passe sera retentée au prochain cycle.
        if (!ServeurHistoriqueVivant(conn, s))
        {
            this.LogInfo("⚠️ Serveur d'historique MUET (maintenance ?) : passe annulée, base intacte. "
                       + "Nouvel essai au prochain cycle.");
            LogFooter(conn);
            return;
        }

        if (LastIngestedMonth(conn) is DateTime last)
            CollectForward(conn, s, last.AddMonths(1) <= thisMonth ? last.AddMonths(1) : thisMonth, thisMonth);
        else
            ProbeBackward(conn, s, thisMonth);

        LogFooter(conn);
    }

    /// <summary>Canari : la fin du dernier mois non vide déjà ingéré doit re-répondre.
    /// Premier run (rien d'ingéré) : pas de référence, on laisse passer.</summary>
    private bool ServeurHistoriqueVivant(SQLiteConnection conn, Symbol s)
    {
        using var cmd = new SQLiteCommand(
            "SELECT name FROM _ingested WHERE name LIKE 'month/%' AND rows > 0 "
          + "ORDER BY name DESC LIMIT 1", conn);
        if (cmd.ExecuteScalar() is not string name) return true;
        var m = DateTime.ParseExact(name.Substring(6), "yyyy-MM", CultureInfo.InvariantCulture);
        var monthEnd = new DateTime(m.Year, m.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        HistoricalData? hd = null;
        try
        {
            hd = s.GetHistory(new Period(BasePeriod.Minute, PeriodMinutes), HistoryType.Last,
                              monthEnd.AddDays(-3), monthEnd);
            if (hd is not null)
                foreach (var raw in hd)
                    if (raw is HistoryItemBar) return true;
        }
        finally { hd?.Dispose(); }
        return false;
    }

    /// <summary>Base déjà amorcée : compléter du dernier mois marqué jusqu'au mois courant.</summary>
    private void CollectForward(SQLiteConnection conn, Symbol s, DateTime fromMonth, DateTime thisMonth)
    {
        long grand = 0;
        for (var m = fromMonth; m <= thisMonth; m = m.AddMonths(1))
        {
            int rows = IngestMonth(conn, s, m);
            grand += rows;
            // Marquage prudent : jamais sur « 0 reçue alors qu'on avait des données »
            // (serveur tombé en cours de passe) — le mois sera retenté.
            if (rows > 0 || !ADesLignes(conn, m, m.AddMonths(1)))
                MarkIfComplete(conn, m, rows, thisMonth);
            this.LogInfo($"{m:yyyy-MM} : {rows,7} barres{(m < thisMonth ? " [marqué]" : " [courant, partiel]")}"
                       + (rows == 0 ? " (0 reçue : existant conservé)" : ""));
        }
        this.LogInfo($"Collecte terminée : +{grand} barres sur cette passe.");
    }

    /// <summary>Base vide : sonde vers l'arrière depuis le mois courant, mesure la profondeur réelle.</summary>
    private void ProbeBackward(SQLiteConnection conn, Symbol s, DateTime thisMonth)
    {
        var floor = thisMonth.AddYears(-MaxBackfillYears);
        int emptyRun = 0;
        long grand = 0;
        DateTime? oldest = null;
        for (var m = thisMonth; m >= floor && emptyRun < EmptyMonthsStop; m = m.AddMonths(-1))
        {
            int rows = IngestMonth(conn, s, m);
            grand += rows;
            if (rows == 0) { emptyRun++; this.LogInfo($"{m:yyyy-MM} :       0 barre  (vide {emptyRun}/{EmptyMonthsStop})"); continue; }
            emptyRun = 0;
            oldest = m;
            MarkIfComplete(conn, m, rows, thisMonth);
            this.LogInfo($"{m:yyyy-MM} : {rows,7} barres{(m < thisMonth ? " [marqué]" : " [courant, partiel]")}");
        }
        this.LogInfo(emptyRun >= EmptyMonthsStop
            ? $"Fin des données serveur atteinte : plus ancien mois servi = {oldest:yyyy-MM} (PROFONDEUR MESURÉE)."
            : $"Sonde arrêtée à la borne MaxBackfillYears ({MaxBackfillYears} ans) — il y a peut-être plus ancien.");
        this.LogInfo($"Sonde terminée : +{grand} barres.");
    }

    /// <summary>Télécharge un mois de barres puis REMPLACE la plage en base — uniquement si le
    /// téléchargement a rapporté quelque chose (0 reçue = on ne touche pas à l'existant).</summary>
    private int IngestMonth(SQLiteConnection conn, Symbol s, DateTime monthStart)
    {
        var monthEnd = monthStart.AddMonths(1);
        var bars = new List<(long ts, double o, double h, double l, double c, double v, long n)>();
        HistoricalData? hd = null;
        try
        {
            hd = s.GetHistory(new Period(BasePeriod.Minute, PeriodMinutes), HistoryType.Last,
                              monthStart, monthEnd);
            if (hd is not null)
                foreach (var raw in hd)
                {
                    if (raw is not HistoryItemBar b) continue;
                    long ts = ToMs(b.TimeLeft);
                    if (ts < ToMs(monthStart) || ts >= ToMs(monthEnd)) continue; // stricte au mois
                    bars.Add((ts, b.Open, b.High, b.Low, b.Close, b.Volume, b.Ticks));
                }
        }
        finally { hd?.Dispose(); }

        if (bars.Count == 0) return 0; // rien reçu -> on conserve l'existant tel quel

        using var tx = conn.BeginTransaction();
        // Remplacement de la plage : purge APRÈS un téléchargement réussi seulement.
        using (var del = new SQLiteCommand("DELETE FROM bars WHERE ts >= @a AND ts < @b", conn, tx))
        {
            del.Parameters.AddWithValue("@a", ToMs(monthStart));
            del.Parameters.AddWithValue("@b", ToMs(monthEnd));
            del.ExecuteNonQuery();
        }
        using var cmd = new SQLiteCommand(
            "INSERT OR REPLACE INTO bars(ts,open,high,low,close,volume,ticks) "
          + "VALUES(@ts,@o,@h,@l,@c,@v,@n)", conn, tx);
        var pTs = cmd.Parameters.Add("@ts", System.Data.DbType.Int64);
        var pO = cmd.Parameters.Add("@o", System.Data.DbType.Double);
        var pH = cmd.Parameters.Add("@h", System.Data.DbType.Double);
        var pL = cmd.Parameters.Add("@l", System.Data.DbType.Double);
        var pC = cmd.Parameters.Add("@c", System.Data.DbType.Double);
        var pV = cmd.Parameters.Add("@v", System.Data.DbType.Double);
        var pN = cmd.Parameters.Add("@n", System.Data.DbType.Int64);
        foreach (var (ts, o, h, l, c, v, n) in bars)
        {
            pTs.Value = ts; pO.Value = o; pH.Value = h; pL.Value = l;
            pC.Value = c; pV.Value = v; pN.Value = n;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return bars.Count;
    }

    private static bool ADesLignes(SQLiteConnection c, DateTime de, DateTime a)
    {
        using var cmd = new SQLiteCommand("SELECT 1 FROM bars WHERE ts >= @a AND ts < @b LIMIT 1", c);
        cmd.Parameters.AddWithValue("@a", ToMs(de));
        cmd.Parameters.AddWithValue("@b", ToMs(a));
        return cmd.ExecuteScalar() is not null;
    }

    private void MarkIfComplete(SQLiteConnection conn, DateTime month, int rows, DateTime thisMonth)
    {
        if (month >= thisMonth) return; // mois courant = partiel, jamais marqué
        using var mk = new SQLiteCommand("INSERT OR REPLACE INTO _ingested VALUES(@n,@r,@a)", conn);
        mk.Parameters.AddWithValue("@n", $"month/{month:yyyy-MM}");
        mk.Parameters.AddWithValue("@r", rows);
        mk.Parameters.AddWithValue("@a", DateTime.UtcNow.ToString("o"));
        mk.ExecuteNonQuery();
    }

    // --- SQLite ------------------------------------------------------------------------ //
    private static void Pragmas(SQLiteConnection c)
    {
        foreach (var p in new[] { "journal_mode=WAL", "synchronous=NORMAL", "cache_size=-65536" })
            using (var cmd = new SQLiteCommand($"PRAGMA {p}", c)) cmd.ExecuteNonQuery();
    }

    private static void EnsureSchema(SQLiteConnection c)
    {
        Exec(c, @"CREATE TABLE IF NOT EXISTS bars(
                    ts     INTEGER PRIMARY KEY,
                    open   REAL NOT NULL,
                    high   REAL NOT NULL,
                    low    REAL NOT NULL,
                    close  REAL NOT NULL,
                    volume REAL NOT NULL,
                    ticks  INTEGER NOT NULL)");
        Exec(c, "CREATE TABLE IF NOT EXISTS _ingested(name TEXT PRIMARY KEY, rows INTEGER, at TEXT)");
        Exec(c, "CREATE TABLE IF NOT EXISTS _meta(k TEXT PRIMARY KEY, v TEXT)");
    }

    private void WriteMeta(SQLiteConnection c, Symbol s)
    {
        double tick = s.TickSize;
        double tickCost = s.GetTickCost(1);
        double mult = tick != 0 ? tickCost / tick : 0;
        var meta = new (string k, string v)[]
        {
            ("symbol", s.Root ?? s.Name),
            ("market", s.Exchange?.ExchangeName ?? "CME"),
            ("exchange", s.Exchange?.ExchangeName ?? "CME"),
            ("contract", s.Name),
            ("expiration", s.ExpirationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("tick_size", tick.ToString(CultureInfo.InvariantCulture)),
            ("multiplier", mult.ToString(CultureInfo.InvariantCulture)),
            ("tick_value", tickCost.ToString(CultureInfo.InvariantCulture)),
            ("source", "rithmic"),
            ("period_min", PeriodMinutes.ToString(CultureInfo.InvariantCulture)),
        };
        foreach (var (k, v) in meta)
            using (var cmd = new SQLiteCommand("INSERT OR REPLACE INTO _meta VALUES(@k,@v)", c))
            { cmd.Parameters.AddWithValue("@k", k); cmd.Parameters.AddWithValue("@v", v); cmd.ExecuteNonQuery(); }
    }

    private DateTime? LastIngestedMonth(SQLiteConnection c)
    {
        using var cmd = new SQLiteCommand(
            "SELECT name FROM _ingested WHERE name LIKE 'month/%' ORDER BY name DESC LIMIT 1", c);
        if (cmd.ExecuteScalar() is string name &&
            DateTime.TryParseExact(name.Substring(6), "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return null;
    }

    private void LogFooter(SQLiteConnection c)
    {
        using var cmd = new SQLiteCommand("SELECT COUNT(*), MIN(ts), MAX(ts) FROM bars", c);
        using var r = cmd.ExecuteReader();
        if (r.Read() && !r.IsDBNull(1))
        {
            var lo = FromMs(r.GetInt64(1));
            var hi = FromMs(r.GetInt64(2));
            this.LogInfo($"Base : {r.GetInt64(0)} barres | {lo:yyyy-MM-dd} → {hi:yyyy-MM-dd} "
                       + $"({(hi - lo).TotalDays:F0} jours de profondeur)");
        }
    }

    private string ResolveDbPath(Symbol s)
    {
        if (!string.IsNullOrWhiteSpace(DbPath)) return DbPath;
        string tag = s.ExpirationDate.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return $@"F:\data\NQ-{tag}-{PeriodMinutes}m.db";
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
