using System.Drawing;
using System.Globalization;
using TradingPlatform.BusinessLayer;

namespace SmaCrossNq;

// Résolution de calcul des SMA, INDÉPENDANTE de l'affichage. "Graphe" = timeframe du graphe
// (ancien comportement) ; sinon les SMA sont calculées sur cette résolution fixe et projetées.
public enum ResoSma { Graphe, Min1, Min5, Min15, Min30, Hour1, Hour4, Jour1 }

/// <summary>
/// Phase 3 — Croisement SMA 50/200 sur NQ, avec RÉSOLUTION DE CALCUL indépendante de l'affichage.
/// Par défaut les SMA sont calculées en 1 h : afficher le graphe en 1 min ne change pas les
/// moyennes (elles restent des SMA 50/200 horaires, projetées en marches sur le graphe). Deux
/// lignes + flèches aux croisements. Export time,close,sma_rapide,sma_lente,signal → parité vs
/// Python (indicators/parity_sma.py). SMA = moyenne simple → parité exacte attendue.
/// </summary>
public sealed class SmaCrossNqIndicator : Indicator
{
    [InputParameter("Période rapide", 0, 2, 1000, 1, 0)]
    public int PeriodeRapide = 50;

    [InputParameter("Période lente", 1, 2, 1000, 1, 0)]
    public int PeriodeLente = 200;

    [InputParameter("Résolution de calcul", 2, variants: new object[]
    {
        "Graphe (résolution d'affichage)", ResoSma.Graphe,
        "1 min", ResoSma.Min1, "5 min", ResoSma.Min5, "15 min", ResoSma.Min15,
        "30 min", ResoSma.Min30, "1 h", ResoSma.Hour1, "4 h", ResoSma.Hour4, "1 jour", ResoSma.Jour1,
    })]
    public ResoSma Resolution = ResoSma.Hour1;

    [InputParameter("Profondeur calcul (jours)", 3, 1, 400, 1, 0)]
    public int Lookback = 45;

    [InputParameter("Exporter la parité (CSV)", 4)]
    public bool ExportParite = true;

    [InputParameter("Fichier parité", 5)]
    public string FichierParite = @"H:\IndicesBoursiers\parity\NQ-sma-quantower.csv";

    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Mode résolution fixe : SMA par barre HTF, indexées par bucket temporel (ms UTC).
    private volatile Dictionary<long, (double rapide, double lente, bool cross, bool haussier)>? _htf;
    private long _periodMs;
    private long _lastBucket = long.MinValue;
    // Mode graphe : signe précédent pour détecter le croisement en direct.
    private int _diffPrecSigne;
    private DateTime _derniereBarre;
    private int _launched;

    public SmaCrossNqIndicator()
    {
        Name = "SMA Cross NQ (50/200)";
        SeparateWindow = false;
        AddLineSeries("SMA rapide", Color.DodgerBlue, 2, LineStyle.Solid);
        AddLineSeries("SMA lente", Color.Orange, 2, LineStyle.Solid);
    }

    protected override void OnInit()
    {
        _diffPrecSigne = 0; _derniereBarre = default; _lastBucket = long.MinValue;
        if (Resolution != ResoSma.Graphe)
        {
            if (System.Threading.Interlocked.Exchange(ref _launched, 1) == 0)
                System.Threading.Tasks.Task.Run(ComputeHtfSafe);
        }
        else InitExport();
    }

    protected override void OnSettingsUpdated()
    {
        _launched = 0;
        OnInit();
    }

    protected override void OnUpdate(UpdateArgs args)
    {
        if (Resolution != ResoSma.Graphe)
        {
            var htf = _htf;
            if (htf is null || _periodMs <= 0) return;
            long bucket = ToMs(this.Time(0)) / _periodMs * _periodMs;
            if (!htf.TryGetValue(bucket, out var v)) return;
            SetValue(v.rapide, 0);
            SetValue(v.lente, 1);
            if (bucket != _lastBucket)                       // 1re barre graphe de cette barre HTF
            {
                _lastBucket = bucket;
                if (v.cross) PoseMarqueur(v.haussier);
            }
            return;
        }

        // Mode "Graphe" : calcul direct sur les barres du graphe (ancien comportement).
        if (Count < PeriodeLente) return;
        double rapide = SmaChart(PeriodeRapide);
        double lente = SmaChart(PeriodeLente);
        SetValue(rapide, 0);
        SetValue(lente, 1);
        double diff = rapide - lente;
        int signe = diff > 0 ? 1 : (diff < 0 ? -1 : 0);
        string signal = "";
        if (_diffPrecSigne != 0 && signe != 0 && signe != _diffPrecSigne)
        { signal = signe > 0 ? "ACHAT" : "VENTE"; PoseMarqueur(signe > 0); }
        if (signe != 0) _diffPrecSigne = signe;
        Exporter(this.Time(0), this.GetPrice(PriceType.Close, 0), rapide, lente, signal);
    }

    private void PoseMarqueur(bool haussier)
    {
        LinesSeries[0].SetMarker(0, new IndicatorLineMarker
        {
            Color = haussier ? Color.LimeGreen : Color.Red,
            UpperIcon = haussier ? IndicatorLineMarkerIconType.None : IndicatorLineMarkerIconType.DownArrow,
            BottomIcon = haussier ? IndicatorLineMarkerIconType.UpArrow : IndicatorLineMarkerIconType.None,
        });
    }

    private double SmaChart(int n)
    {
        double s = 0;
        for (int i = 0; i < n; i++) s += this.GetPrice(PriceType.Close, i);
        return s / n;
    }

    private void ComputeHtfSafe()
    {
        try { ComputeHtf(); }
        catch (Exception ex) { Core.Instance.Loggers.Log($"SMA Cross NQ : {ex}"); }
    }

    private void ComputeHtf()
    {
        Period p = Resolution switch
        {
            ResoSma.Min1 => Period.MIN1, ResoSma.Min5 => Period.MIN5, ResoSma.Min15 => Period.MIN15,
            ResoSma.Min30 => Period.MIN30, ResoSma.Hour4 => Period.HOUR4, ResoSma.Jour1 => Period.DAY1,
            _ => Period.HOUR1,
        };
        _periodMs = (long)p.Duration.TotalMilliseconds;

        var from = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-Lookback), DateTimeKind.Utc);
        var to = DateTime.UtcNow;
        var times = new List<long>();
        var closes = new List<double>();
        HistoricalData? hd = null;
        try
        {
            hd = this.Symbol.GetHistory(p, from, to);
            for (int i = 0; i < hd.Count; i++)
            {
                var item = hd[i, SeekOriginHistory.Begin];
                times.Add(ToMs(item.TimeLeft) / _periodMs * _periodMs);
                closes.Add(item[PriceType.Close]);
            }
        }
        finally { hd?.Dispose(); }

        var map = new Dictionary<long, (double, double, bool, bool)>();
        InitExport();
        System.IO.StreamWriter? w = OpenExport();
        double sR = 0, sL = 0; int prevSigne = 0;
        for (int i = 0; i < closes.Count; i++)
        {
            sR += closes[i]; sL += closes[i];
            if (i >= PeriodeRapide) sR -= closes[i - PeriodeRapide];
            if (i >= PeriodeLente) sL -= closes[i - PeriodeLente];
            if (i < PeriodeLente) continue;                  // warmup
            double rapide = sR / PeriodeRapide, lente = sL / PeriodeLente;
            double diff = rapide - lente;
            int signe = diff > 0 ? 1 : (diff < 0 ? -1 : 0);
            bool cross = prevSigne != 0 && signe != 0 && signe != prevSigne;
            bool haussier = signe > 0;
            if (signe != 0) prevSigne = signe;
            map[times[i]] = (rapide, lente, cross, haussier);
            w?.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss}+00:00,{1},{2},{3},{4}",
                FromMs(times[i]), closes[i], rapide, lente, cross ? (haussier ? "ACHAT" : "VENTE") : ""));
        }
        w?.Flush(); w?.Dispose();
        _htf = map;
        this.Refresh();
    }

    private void InitExport()
    {
        if (!ExportParite) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FichierParite)!);
            if (Resolution == ResoSma.Graphe)
                System.IO.File.WriteAllText(FichierParite, "time,close,sma_rapide,sma_lente,signal\n");
        }
        catch { }
    }

    private System.IO.StreamWriter? OpenExport()
    {
        if (!ExportParite) return null;
        try
        {
            var sw = new System.IO.StreamWriter(FichierParite, false);
            sw.WriteLine("time,close,sma_rapide,sma_lente,signal");
            return sw;
        }
        catch { return null; }
    }

    private void Exporter(DateTime t, double close, double rapide, double lente, string signal)
    {
        if (!ExportParite) return;
        if (t == _derniereBarre) return;
        _derniereBarre = t;
        try
        {
            System.IO.File.AppendAllText(FichierParite, string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss}+00:00,{1},{2},{3},{4}\n", t, close, rapide, lente, signal));
        }
        catch { }
    }

    private static long ToMs(DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        return (long)(utc - Epoch).TotalMilliseconds;
    }

    private static DateTime FromMs(long ms) => Epoch.AddMilliseconds(ms);
}
