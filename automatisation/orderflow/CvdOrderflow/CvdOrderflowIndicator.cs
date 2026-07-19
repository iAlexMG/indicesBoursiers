using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
using TradingPlatform.BusinessLayer;

namespace CvdOrderflow;

public enum ResetCvd { Session, Jour, Aucun }

/// <summary>
/// Orderflow #1 — CVD (Cumulative Volume Delta) TEMPS RÉEL + imbalance du carnet, sans dépendre
/// des chandelles. Le delta (agression achat − vente) vient des TICKS : seed depuis l'historique
/// (`GetTickHistory`), puis incrément **tick par tick en direct** via `Symbol.NewLast`. Le CVD est
/// cumulé et remis à zéro par session (ou jour). En prime, l'**imbalance du carnet** (taille bid vs
/// ask sur N niveaux) est lue en direct sur le DOM (`Symbol.NewLevel2`) et affichée. Fenêtre séparée.
/// Fondation du scalping orderflow (Phase orderflow).
/// </summary>
public sealed class CvdOrderflowIndicator : Indicator
{
    // Le CVD suit la résolution des BARRES DU GRAPHE (change le timeframe pour changer la
    // granularité). Le CVD étant cumulé, la courbe est la même à toute résolution.
    [InputParameter("Remise à zéro", 0, variants: new object[]
    {
        "Par session (Asia/London/NY)", ResetCvd.Session, "Par jour (UTC)", ResetCvd.Jour, "Aucune", ResetCvd.Aucun,
    })]
    public ResetCvd Reset = ResetCvd.Session;

    [InputParameter("Niveaux carnet (imbalance)", 1, 1, 50, 1, 0)]
    public int NiveauxCarnet = 10;

    [InputParameter("Seed historique (jours)", 2, 1, 15, 1, 0)]
    public int Lookback = 3;

    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo NyTz = ResolveNy();
    private static readonly Font Font = new("Segoe UI", 9f);

    private long _resMs;
    private readonly ConcurrentDictionary<long, double> _map = new();
    private double _runningCvd;
    private string _resetKey = "";
    private bool _seeded;
    private int _launched;
    private long _lastRefreshTicks;
    private double _bookImb; private double _bookBid, _bookAsk;

    public CvdOrderflowIndicator()
    {
        Name = "CVD Orderflow (NQ)";
        SeparateWindow = true;
        AddLineSeries("CVD", Color.DodgerBlue, 2, LineStyle.Solid);
        AddLineLevel(0, "0", Color.Gray, 1, LineStyle.Dash);
    }

    protected override void OnInit()
    {
        // Bucket du CVD = période des barres du graphe (aligne la projection sur les barres).
        _resMs = this.HistoricalData?.Aggregation is HistoryAggregationTime agg
            ? (long)agg.Period.Duration.TotalMilliseconds : 60_000;
        if (_resMs <= 0) _resMs = 60_000;
        _map.Clear(); _runningCvd = 0; _resetKey = ""; _seeded = false;

        this.Symbol.NewLast += OnLast;
        this.Symbol.NewLevel2 += OnLevel2;
        if (System.Threading.Interlocked.Exchange(ref _launched, 1) == 0)
            System.Threading.Tasks.Task.Run(SeedSafe);
    }

    protected override void OnClear()
    {
        try { this.Symbol.NewLast -= OnLast; this.Symbol.NewLevel2 -= OnLevel2; } catch { }
        _launched = 0;
    }

    protected override void OnUpdate(UpdateArgs args)
    {
        if (!_seeded) return;
        long bucket = ToMs(this.Time(0)) / _resMs * _resMs;
        if (_map.TryGetValue(bucket, out double cvd)) SetValue(cvd);
    }

    // --- Tape (ticks) : delta cumulé ---------------------------------------------------- //
    private void OnLast(Symbol s, Last last)
    {
        if (!_seeded) return;
        double d = last.AggressorFlag == AggressorFlag.Buy ? last.Size
                 : last.AggressorFlag == AggressorFlag.Sell ? -last.Size : 0;
        if (d == 0) return;
        long ts = ToMs(last.Time);
        long bucket = ts / _resMs * _resMs;
        string rk = ResetKey(bucket);
        if (rk != _resetKey) { _runningCvd = 0; _resetKey = rk; }
        _runningCvd += d;
        _map[bucket] = _runningCvd;
        ThrottleRefresh();
    }

    // --- Carnet (DOM) : imbalance ------------------------------------------------------- //
    private void OnLevel2(Symbol s, Level2Quote l2, DOMQuote dom)
    {
        var now = DateTime.UtcNow.Ticks;
        if (now - _lastRefreshTicks < 1_500_000) return;     // ~150 ms de throttle
        try
        {
            var col = this.Symbol.DepthOfMarket.GetDepthOfMarketAggregatedCollections(new GetDepthOfMarketParameters());
            double b = 0, a = 0;
            var bids = col?.Bids; var asks = col?.Asks;
            for (int i = 0; bids != null && i < Math.Min(NiveauxCarnet, bids.Length); i++) b += bids[i].Size;
            for (int i = 0; asks != null && i < Math.Min(NiveauxCarnet, asks.Length); i++) a += asks[i].Size;
            _bookBid = b; _bookAsk = a;
            _bookImb = (b + a) > 0 ? (b - a) / (b + a) : 0;
        }
        catch { }
    }

    public override void OnPaintChart(PaintChartEventArgs args)
    {
        var gr = args.Graphics;
        var r = args.Rectangle;
        string sens = _bookImb > 0.05 ? "ACHETEUR" : _bookImb < -0.05 ? "VENDEUR" : "neutre";
        var col = _bookImb > 0.05 ? Brushes.LimeGreen : _bookImb < -0.05 ? Brushes.OrangeRed : Brushes.Gainsboro;
        gr.DrawString($"Imbalance carnet (top {NiveauxCarnet}) : {_bookImb * 100:+0;-0} %  [{sens}]   "
                    + $"bid {_bookBid:0} / ask {_bookAsk:0}", Font, col, r.Left + 6, r.Top + 4);
        gr.DrawString($"CVD reset : {Reset}", Font, Brushes.Gray, r.Left + 6, r.Top + 20);
    }

    private void ThrottleRefresh()
    {
        var now = DateTime.UtcNow.Ticks;
        if (now - _lastRefreshTicks < 1_500_000) return;     // ~150 ms
        _lastRefreshTicks = now;
        try { this.Refresh(); } catch { }
    }

    private void SeedSafe() { try { Seed(); } catch (Exception ex) { Core.Instance.Loggers.Log($"CVD : {ex}"); } }

    private void Seed()
    {
        var now = DateTime.UtcNow;
        var bricks = new SortedDictionary<long, double>();   // bucket -> delta
        long ticks = 0;
        for (int d = Lookback; d >= 0; d--)
        {
            var from = DateTime.SpecifyKind(now.Date.AddDays(-d), DateTimeKind.Utc);
            var to = from.AddDays(1);
            HistoricalData? hd = null;
            try
            {
                hd = this.Symbol.GetTickHistory(HistoryType.Last, from, to);
                foreach (var raw in hd)
                {
                    if (raw is not HistoryItemLast t) continue;
                    ticks++;
                    double dd = t.AggressorFlag == AggressorFlag.Buy ? t.Volume
                              : t.AggressorFlag == AggressorFlag.Sell ? -t.Volume : 0;
                    if (dd == 0) continue;
                    long bucket = ToMs(t.TimeLeft) / _resMs * _resMs;
                    bricks[bucket] = (bricks.TryGetValue(bucket, out var c) ? c : 0) + dd;
                }
            }
            finally { hd?.Dispose(); }
        }

        double cvd = 0; string rk = "";
        foreach (var kv in bricks)
        {
            string k = ResetKey(kv.Key);
            if (k != rk) { cvd = 0; rk = k; }
            cvd += kv.Value;
            _map[kv.Key] = cvd;
        }
        _runningCvd = cvd; _resetKey = rk;
        _seeded = true;

        // Remplir la ligne pour TOUTES les barres (OnUpdate n'a tourné qu'avant la fin du seed).
        try
        {
            for (int i = 0; i < Count; i++)
            {
                long bt = ToMs(this.Time(i)) / _resMs * _resMs;   // offset i (0 = barre courante)
                if (_map.TryGetValue(bt, out double v)) SetValue(v, 0, i);
            }
        }
        catch { }

        try
        {
            long tNow = Count > 0 ? ToMs(this.Time(0)) : 0;
            long bkt = _resMs > 0 ? tNow / _resMs * _resMs : 0;
            long first = _map.Count > 0 ? long.MaxValue : 0, last = 0;
            foreach (var k in _map.Keys) { if (k < first) first = k; if (k > last) last = k; }
            System.IO.File.WriteAllText(@"H:\IndicesBoursiers\parity\cvd-debug.txt",
                $"resMs={_resMs}\nticks={ticks}\nbricks={bricks.Count}\nmap={_map.Count}\n" +
                $"map_first={(first == long.MaxValue ? "-" : FromMs(first).ToString("o"))}\nmap_last={FromMs(last):o}\n" +
                $"Count={Count}\nTime(0)={(tNow > 0 ? FromMs(tNow).ToString("o") : "-")}\n" +
                $"bucket(Time0)={FromMs(bkt):o}\nmap_contient_bucket={_map.ContainsKey(bkt)}\n");
        }
        catch { }

        this.Refresh();
    }

    private string ResetKey(long bucketMs)
    {
        var t = FromMs(bucketMs);
        return Reset switch
        {
            ResetCvd.Session => ClassifySession(t) + t.Date.ToString("yyyyMMdd"),
            ResetCvd.Jour => t.Date.ToString("yyyyMMdd"),
            _ => "all",
        };
    }

    private static string ClassifySession(DateTime tUtc)
    {
        var ny = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(tUtc, DateTimeKind.Utc), NyTz);
        int m = ny.Hour * 60 + ny.Minute;
        if (m >= 1080 || m < 180) return "asia";
        if (m < 570) return "london";
        if (m < 1020) return "ny";
        return "hors";
    }

    private static long ToMs(DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        return (long)(utc - Epoch).TotalMilliseconds;
    }

    private static DateTime FromMs(long ms) => Epoch.AddMilliseconds(ms);

    private static TimeZoneInfo ResolveNy()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.Utc;
    }
}
