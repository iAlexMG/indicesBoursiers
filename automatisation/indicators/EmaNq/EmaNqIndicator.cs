using System.Drawing;
using System.Globalization;
using TradingPlatform.BusinessLayer;

namespace EmaNq;

// Résolution de calcul, indépendante de l'affichage (comme la SMA). "Graphe" = timeframe affiché
// (héberge l'EMA NATIVE de Quantower → sert aussi au test de parité). Sinon EMA calculée sur cette
// résolution fixe et projetée en marches sur le graphe.
public enum ResoEma { Graphe, Min1, Min5, Min15, Min30, Hour1, Hour4, Jour1 }

/// <summary>
/// Phase 3 — EMA sur NQ, avec RÉSOLUTION DE CALCUL indépendante de l'affichage (défaut 1 h).
/// Mode « Graphe » : héberge l'EMA native (`BuiltIn.EMA`) sur le timeframe affiché — c'est ce mode
/// qui sert au test de parité (native vs Python, seed). Mode résolution fixe : EMA calculée sur
/// l'historique de cette résolution (seed = SMA, lissage 2/(N+1)) et projetée → afficher en 1 min
/// ne change pas l'EMA horaire. Export time,close,ema → parité (indicators/parity_ema.py).
/// </summary>
public sealed class EmaNqIndicator : Indicator
{
    [InputParameter("Période EMA", 0, 2, 1000, 1, 0)]
    public int Periode = 20;

    [InputParameter("Résolution de calcul", 1, variants: new object[]
    {
        "Graphe (résolution d'affichage)", ResoEma.Graphe,
        "1 min", ResoEma.Min1, "5 min", ResoEma.Min5, "15 min", ResoEma.Min15,
        "30 min", ResoEma.Min30, "1 h", ResoEma.Hour1, "4 h", ResoEma.Hour4, "1 jour", ResoEma.Jour1,
    })]
    public ResoEma Resolution = ResoEma.Hour1;

    [InputParameter("Profondeur calcul (jours)", 2, 1, 400, 1, 0)]
    public int Lookback = 45;

    [InputParameter("Exporter la parité (CSV)", 3)]
    public bool ExportParite = true;

    [InputParameter("Fichier parité", 4)]
    public string FichierParite = @"F:\data\parity\NQ-ema-quantower.csv";

    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private Indicator? _ema;                                  // mode Graphe : EMA native hébergée
    private volatile Dictionary<long, double>? _htf;         // mode fixe : EMA par bucket temporel
    private long _periodMs;
    private DateTime _derniereBarre;
    private int _launched;

    public EmaNqIndicator()
    {
        Name = "EMA NQ (native)";
        SeparateWindow = false;
        AddLineSeries("EMA", Color.MediumSpringGreen, 2, LineStyle.Solid);
    }

    protected override void OnInit()
    {
        _derniereBarre = default;
        if (Resolution == ResoEma.Graphe)
        {
            _ema = Core.Instance.Indicators.BuiltIn.EMA(Periode, PriceType.Close,
                IndicatorCalculationType.AllAvailableData);
            AddIndicator(_ema);
            InitExport(entete: true);
        }
        else if (System.Threading.Interlocked.Exchange(ref _launched, 1) == 0)
            System.Threading.Tasks.Task.Run(ComputeHtfSafe);
    }

    protected override void OnSettingsUpdated()
    {
        _launched = 0; _htf = null; _ema = null;
        OnInit();
    }

    protected override void OnUpdate(UpdateArgs args)
    {
        if (Resolution == ResoEma.Graphe)
        {
            if (_ema is null || Periode >= Count) return;
            double ema = _ema.GetValue();                    // EMA native, barre courante
            SetValue(ema);
            ExportLigne(this.Time(0), this.GetPrice(PriceType.Close, 0), ema);
            return;
        }

        var htf = _htf;
        if (htf is null || _periodMs <= 0) return;
        long bucket = ToMs(this.Time(0)) / _periodMs * _periodMs;
        if (htf.TryGetValue(bucket, out var v)) SetValue(v);
    }

    private void ComputeHtfSafe()
    {
        try { ComputeHtf(); }
        catch (Exception ex) { Core.Instance.Loggers.Log($"EMA NQ : {ex}"); }
    }

    private void ComputeHtf()
    {
        Period p = Resolution switch
        {
            ResoEma.Min1 => Period.MIN1, ResoEma.Min5 => Period.MIN5, ResoEma.Min15 => Period.MIN15,
            ResoEma.Min30 => Period.MIN30, ResoEma.Hour4 => Period.HOUR4, ResoEma.Jour1 => Period.DAY1,
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

        var map = new Dictionary<long, double>();
        var sw = OpenExport();
        double a = 2.0 / (Periode + 1), ema = 0, sum = 0;
        for (int i = 0; i < closes.Count; i++)
        {
            if (i < Periode) sum += closes[i];
            if (i < Periode - 1) continue;                   // warmup
            if (i == Periode - 1) ema = sum / Periode;        // seed = SMA (lissage 2/(N+1) ensuite)
            else ema = a * closes[i] + (1 - a) * ema;
            map[times[i]] = ema;
            sw?.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss}+00:00,{1},{2}", FromMs(times[i]), closes[i], ema));
        }
        sw?.Flush(); sw?.Dispose();
        _htf = map;
        this.Refresh();
    }

    private void InitExport(bool entete)
    {
        if (!ExportParite) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FichierParite)!);
            if (entete) System.IO.File.WriteAllText(FichierParite, "time,close,ema\n");
        }
        catch { }
    }

    private System.IO.StreamWriter? OpenExport()
    {
        if (!ExportParite) return null;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FichierParite)!);
            var sw = new System.IO.StreamWriter(FichierParite, false);
            sw.WriteLine("time,close,ema");
            return sw;
        }
        catch { return null; }
    }

    private void ExportLigne(DateTime t, double close, double ema)
    {
        if (!ExportParite || t == _derniereBarre) return;
        _derniereBarre = t;
        try
        {
            System.IO.File.AppendAllText(FichierParite, string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss}+00:00,{1},{2}\n", t, close, ema));
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
