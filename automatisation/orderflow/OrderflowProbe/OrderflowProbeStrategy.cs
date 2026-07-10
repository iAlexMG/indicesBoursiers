using System.Globalization;
using System.Text;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace OrderflowProbe;

/// <summary>
/// POC orderflow (« Phase 0 bis ») — MESURE le flux temps réel avant de concevoir des signaux
/// de scalping. Souscrit au Last (tape), au Quote (meilleur bid/ask) et au Level2 (carnet), puis
/// après N secondes écrit un rapport : débit ticks/s, aggressor peuplé EN DIRECT, latence estimée,
/// et surtout **le carnet DOM est-il disponible** (ou payant/absent) et à quelle profondeur.
/// Rien de tout ça n'est supposé : on le mesure, comme la Phase 0.
/// </summary>
public sealed class OrderflowProbeStrategy : Strategy
{
    [InputParameter("Symbole (NQ)", 0)]
    public Symbol? Instrument { get; set; }

    [InputParameter("Durée de mesure (s)", 1, 5, 300, 1, 0)]
    public int DureeSec = 30;

    [InputParameter("Rapport", 2)]
    public string RapportPath = @"C:\Users\Moi\Desktop\Claude_Code\Portfolio\Quantower\docs\orderflow-probe.md";

    private long _last, _buy, _sell, _none, _quote, _level2, _latSumMs;
    private DateTime _t0, _premierTick, _dernierTick;
    private volatile string _lastSample = "(aucun)", _quoteSample = "(aucun)";
    private System.Threading.Timer? _timer;

    public OrderflowProbeStrategy() => Name = "Orderflow Probe (NQ)";

    protected override void OnRun()
    {
        var s = Instrument;
        if (s is null) { this.LogError("Aucun symbole sélectionné."); this.Stop(); return; }
        _t0 = DateTime.UtcNow;

        // Le symbole choisi dans la stratégie est déjà actif : on s'abonne à ses events temps réel.
        s.NewLast += OnLast;
        s.NewQuote += OnQuote;
        s.NewLevel2 += OnLevel2;
        this.LogInfo($"Sonde orderflow lancée sur {s.Name} pour {DureeSec}s (Last/Quote/Level2)…");
        _timer = new System.Threading.Timer(_ => Finaliser(), null, DureeSec * 1000, Timeout.Infinite);
    }

    private void OnLast(Symbol s, Last last)
    {
        Interlocked.Increment(ref _last);
        switch (last.AggressorFlag)
        {
            case AggressorFlag.Buy: Interlocked.Increment(ref _buy); break;
            case AggressorFlag.Sell: Interlocked.Increment(ref _sell); break;
            default: Interlocked.Increment(ref _none); break;
        }
        var t = last.Time.Kind == DateTimeKind.Utc ? last.Time : last.Time.ToUniversalTime();
        Interlocked.Add(ref _latSumMs, (long)(DateTime.UtcNow - t).TotalMilliseconds);
        if (_premierTick == default) _premierTick = t;
        _dernierTick = t;
        _lastSample = string.Format(CultureInfo.InvariantCulture,
            "{0} x{1} {2} @ {3:HH:mm:ss.fff} UTC", last.Price, last.Size, last.AggressorFlag, t);
    }

    private void OnQuote(Symbol s, Quote q)
    {
        Interlocked.Increment(ref _quote);
        _quoteSample = string.Format(CultureInfo.InvariantCulture,
            "bid {0} x{1}  |  ask {2} x{3}", q.Bid, q.BidSize, q.Ask, q.AskSize);
    }

    private void OnLevel2(Symbol s, Level2Quote l2, DOMQuote dom) => Interlocked.Increment(ref _level2);

    private void Finaliser()
    {
        var s = Instrument;
        if (s is null) return;
        double sec = Math.Max(0.001, (DateTime.UtcNow - _t0).TotalSeconds);
        long last = Interlocked.Read(ref _last), quote = Interlocked.Read(ref _quote), l2 = Interlocked.Read(ref _level2);
        long buy = Interlocked.Read(ref _buy), sell = Interlocked.Read(ref _sell), none = Interlocked.Read(ref _none);
        long latSum = Interlocked.Read(ref _latSumMs);

        // Carnet DOM : disponible ou non, et à quelle profondeur ?
        int bidLvls = 0, askLvls = 0; string domSample = "(carnet vide / indisponible)";
        try
        {
            var col = s.DepthOfMarket.GetDepthOfMarketAggregatedCollections(new GetDepthOfMarketParameters());
            bidLvls = col?.Bids?.Length ?? 0; askLvls = col?.Asks?.Length ?? 0;
            if (bidLvls > 0 || askLvls > 0)
            {
                var sb2 = new StringBuilder();
                for (int i = 0; i < Math.Min(5, askLvls); i++) sb2.Insert(0, $"    ask {col!.Asks[i].Price} x{col.Asks[i].Size}\n");
                for (int i = 0; i < Math.Min(5, bidLvls); i++) sb2.Append($"    bid {col!.Bids[i].Price} x{col.Bids[i].Size}\n");
                domSample = sb2.ToString();
            }
        }
        catch (Exception ex) { domSample = $"(GetDepthOfMarket : {ex.Message})"; }

        var sb = new StringBuilder();
        void W(string l) { sb.AppendLine(l); this.LogInfo(l); }
        W($"# Orderflow Probe — {s.Name} ({s.Id})   {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        W($"Fenêtre de mesure : {sec:F1} s");
        W("");
        W("## Tape (Last)");
        W($"  ticks : {last}  →  **{last / sec:F0} ticks/s**");
        W($"  aggressor EN DIRECT : buy={buy} sell={sell} none={none}  →  renseigné {(last > 0 ? 100.0 * (buy + sell) / last : 0):F1} %");
        W($"  latence estimée (réception − horodatage tick) : {(last > 0 ? latSum / (double)last : 0):F0} ms  (⚠ inclut l'écart d'horloge)");
        W($"  couverture : {_premierTick:HH:mm:ss} → {_dernierTick:HH:mm:ss} UTC");
        W($"  dernier tick : {_lastSample}");
        W("");
        W("## Quote (meilleur bid/ask)");
        W($"  updates : {quote}  →  {quote / sec:F0}/s   |   dernier : {_quoteSample}");
        W("");
        W("## Level2 / carnet (DOM)");
        W($"  updates Level2 : {l2}  →  {l2 / sec:F0}/s");
        W($"  profondeur DOM : {bidLvls} niveaux bid, {askLvls} niveaux ask  →  **{((bidLvls > 0 || askLvls > 0) ? "DISPONIBLE" : "INDISPONIBLE (feed absent ou payant ?)")}**");
        W($"  extrait :\n{domSample}");

        try
        {
            System.IO.File.WriteAllText(RapportPath, sb.ToString());
            this.LogInfo($"Rapport écrit : {RapportPath}");
        }
        catch (Exception ex) { this.LogError($"Écriture rapport : {ex.Message}"); }

        this.Stop();
    }

    protected override void OnStop()
    {
        _timer?.Dispose(); _timer = null;
        var s = Instrument;
        if (s is null) return;
        s.NewLast -= OnLast; s.NewQuote -= OnQuote; s.NewLevel2 -= OnLevel2;
    }
}
