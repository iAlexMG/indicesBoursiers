using System.Drawing;
using System.Globalization;
using TradingPlatform.BusinessLayer;

namespace VpSessionNq;

// Mode d'affichage de l'histogramme. EXTENSIBLE : +1 valeur ici, +1 paire dans les variants de
// [InputParameter("Affichage",…)], +1 case dans DessineBloc. Rien d'autre à toucher.
public enum VpAffichage { VolumeBuySell, VolumeTotal, Delta }

// Granularité du profil : la session entière, ou des sous-VP de 30 min / 1 h à l'intérieur.
public enum VpPeriode { Session, Min30, Heure1 }

/// <summary>
/// Phase 3 — Volume Profile PAR SESSION + SOUS-VP (NQ), footprint calculé MAISON depuis les ticks
/// Rithmic gratuits (la fonction « volume analysis » de Quantower est payante). Portage de
/// backtests/volume_profile_features.py : sous-briques 30 min, niveaux 5 pts, sessions NY, value
/// area 70 %. Chaque bloc de profil = un encadré (période × plage de prix) + histogramme
/// volume-at-price aligné à gauche + lignes VAH/VAL. Deux axes indépendants :
///   • Période : Session (défaut) | 30 min | 1 h — la granularité des profils ;
///   • Affichage : Volume buy/sell | Volume total | Delta (défaut) — le rendu des barres.
/// Ticks + calcul en tâche de fond. Export session-developing → parité vs features_vp.csv.
/// </summary>
public sealed class VpSessionNqIndicator : Indicator
{
    [InputParameter("Affichage", 0, variants: new object[]
    {
        "Volume (buy/sell)", VpAffichage.VolumeBuySell,
        "Volume total", VpAffichage.VolumeTotal,
        "Delta par niveau", VpAffichage.Delta,
    })]
    public VpAffichage Affichage = VpAffichage.Delta;

    // Superposition : cocher plusieurs granularités pour les afficher EN MÊME TEMPS
    // (ex. VP session + sous-VP 30 min). Le plus fin est dessiné par-dessus.
    [InputParameter("VP par session", 1)]
    public bool AfficheSession = true;

    [InputParameter("Sous-VP 1 h", 2)]
    public bool Affiche1h = false;

    [InputParameter("Sous-VP 30 min", 3)]
    public bool Affiche30m = false;

    [InputParameter("Profondeur ticks (jours)", 4, 1, 15, 1, 0)]
    public int LookbackDays = 15;

    [InputParameter("Taille niveau (points)", 5, 1, 50, 1, 0)]
    public double TickPrix = 5.0;

    [InputParameter("Couverture value area (%)", 6, 50, 90, 1, 0)]
    public int CouverturePct = 70;

    [InputParameter("Remplissage histo (% largeur bloc)", 7, 20, 100, 1, 0)]
    public int RemplissagePct = 85;

    [InputParameter("Exporter la parité (CSV)", 8)]
    public bool ExportParite = true;

    [InputParameter("Fichier parité", 9)]
    public string FichierParite = @"H:\IndicesBoursiers\parity\NQ-vp-quantower.csv";

    private const long DemiMs = 1_800_000;
    private const long HeureMs = 3_600_000;
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo NyTz = ResolveNy();

    private static readonly SolidBrush BuyBrush = new(Color.FromArgb(120, 38, 166, 91));
    private static readonly SolidBrush SellBrush = new(Color.FromArgb(120, 214, 69, 65));
    private static readonly SolidBrush TotalBrush = new(Color.FromArgb(120, 90, 140, 200));
    private static readonly Pen VahPen = new(Color.FromArgb(220, 90, 190, 240), 1);
    private static readonly Pen ValPen = new(Color.FromArgb(220, 200, 120, 220), 1);
    // Encadré par granularité (superposition lisible).
    private static readonly Pen BoxSession = new(Color.FromArgb(120, 200, 200, 200), 1);
    private static readonly Pen Box1h = new(Color.FromArgb(90, 120, 200, 160), 1);
    private static readonly Pen Box30m = new(Color.FromArgb(80, 150, 160, 210), 1);

    private sealed class VpBlock
    {
        public DateTime Start, End;
        public string Name = "";
        public VpPeriode Periode;
        public Dictionary<double, (double buy, double sell)> Profile = new();
        public double Poc, Vah, Val, MinLevel, MaxLevel, MaxTot, MaxAbsDelta;
    }

    private volatile VpBlock[]? _blocks;
    private volatile SortedDictionary<long, Dictionary<double, (double buy, double sell)>>? _bricks;
    private int _launched;

    public VpSessionNqIndicator()
    {
        Name = "VP Session NQ (ticks)";
        SeparateWindow = false;
    }

    protected override void OnInit()
    {
        if (System.Threading.Interlocked.Exchange(ref _launched, 1) == 0)
            System.Threading.Tasks.Task.Run(ComputeSafe);
    }

    protected override void OnSettingsUpdated()
    {
        // Changer la période/couverture ne recalcule que les blocs (ticks en cache) ; le
        // téléchargement des ticks n'a lieu que si on n'a pas encore la base (ou re-add pour
        // changer profondeur/taille de niveau).
        var br = _bricks;
        if (br is not null)
            System.Threading.Tasks.Task.Run(() =>
            {
                try { ExportFeatures(br); _blocks = BuildAllBlocks(br); this.Refresh(); }
                catch (Exception ex) { Core.Instance.Loggers.Log($"VP Session NQ : {ex}"); }
            });
        else
            System.Threading.Tasks.Task.Run(ComputeSafe);
    }

    public override void OnPaintChart(PaintChartEventArgs args)
    {
        var blocks = _blocks;
        var conv = this.CurrentChart?.MainWindow?.CoordinatesConverter;
        if (blocks is null || conv is null) return;

        var gr = args.Graphics;
        var rect = args.Rectangle;
        float hLevel = Math.Max(1f, (float)Math.Abs(conv.GetChartY(0) - conv.GetChartY(TickPrix)));
        using var font = new Font("Segoe UI", 7.5f);

        foreach (var b in blocks)
        {
            float xL = (float)conv.GetChartX(b.Start);
            float xR = (float)conv.GetChartX(b.End);
            if (xR < rect.Left - 4 || xL > rect.Right + 4) continue;
            DessineBloc(gr, conv, b, xL, xR, hLevel, font);
        }
    }

    private void DessineBloc(Graphics gr, TradingPlatform.BusinessLayer.Chart.IChartWindowCoordinatesConverter conv,
        VpBlock b, float xL, float xR, float hLevel, Font font)
    {
        double echelle = Affichage == VpAffichage.Delta ? b.MaxAbsDelta : b.MaxTot;
        if (echelle <= 0) return;
        float w = Math.Max(3f, xR - xL);
        float yTop = (float)conv.GetChartY(b.MaxLevel + TickPrix / 2);
        float yBot = (float)conv.GetChartY(b.MinLevel - TickPrix / 2);
        var boxPen = b.Periode switch { VpPeriode.Min30 => Box30m, VpPeriode.Heure1 => Box1h, _ => BoxSession };
        gr.DrawRectangle(boxPen, xL, yTop, w, Math.Max(1f, yBot - yTop));

        float wMax = w * (RemplissagePct / 100f);
        foreach (var kv in b.Profile)
        {
            var (buy, sell) = kv.Value;
            float yc = (float)conv.GetChartY(kv.Key);
            float top = yc - hLevel / 2f + 0.5f;
            float bh = Math.Max(1f, hLevel - 1f);
            switch (Affichage)
            {
                case VpAffichage.VolumeBuySell:
                    float wBuy = (float)(buy / echelle * wMax);
                    float wSell = (float)(sell / echelle * wMax);
                    if (wBuy > 0) gr.FillRectangle(BuyBrush, xL, top, wBuy, bh);
                    if (wSell > 0) gr.FillRectangle(SellBrush, xL + wBuy, top, wSell, bh);
                    break;
                case VpAffichage.VolumeTotal:
                    float wTot = (float)((buy + sell) / echelle * wMax);
                    if (wTot > 0) gr.FillRectangle(TotalBrush, xL, top, wTot, bh);
                    break;
                case VpAffichage.Delta:
                    double d = buy - sell;
                    float wD = (float)(Math.Abs(d) / echelle * wMax);
                    if (wD > 0) gr.FillRectangle(d >= 0 ? BuyBrush : SellBrush, xL, top, wD, bh);
                    break;
            }
        }

        gr.DrawLine(VahPen, xL, (float)conv.GetChartY(b.Vah), xR, (float)conv.GetChartY(b.Vah));
        gr.DrawLine(ValPen, xL, (float)conv.GetChartY(b.Val), xR, (float)conv.GetChartY(b.Val));
        if (w > 26) gr.DrawString(b.Name, font, Brushes.Gainsboro, xL + 2, yTop - 13);
    }

    private void ComputeSafe()
    {
        try { Compute(); }
        catch (Exception ex) { Core.Instance.Loggers.Log($"VP Session NQ (ticks) : {ex}"); }
    }

    private void Compute()
    {
        // 1) Ticks (gratuits), par jour → sous-briques 30 min (niveau -> buy/sell) + session.
        var bricks = new SortedDictionary<long, Dictionary<double, (double buy, double sell)>>();
        var now = DateTime.UtcNow;
        for (int d = LookbackDays; d >= 0; d--)
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
                    bool buy = t.AggressorFlag == AggressorFlag.Buy;
                    bool sell = t.AggressorFlag == AggressorFlag.Sell;
                    if (!buy && !sell) continue;
                    long b = ToMs(t.TimeLeft) / DemiMs * DemiMs;
                    if (!bricks.TryGetValue(b, out var levels))
                    { levels = new Dictionary<double, (double, double)>(); bricks[b] = levels; }
                    double niveau = Math.Round(t.Price / TickPrix) * TickPrix;
                    var cell = levels.TryGetValue(niveau, out var cur) ? cur : (0.0, 0.0);
                    levels[niveau] = buy ? (cell.Item1 + t.Volume, cell.Item2) : (cell.Item1, cell.Item2 + t.Volume);
                }
            }
            finally { hd?.Dispose(); }
        }

        _bricks = bricks;                                    // cache (évite de re-télécharger au changement de période)
        ExportFeatures(bricks);                              // parité (session-developing, inchangé)
        _blocks = BuildAllBlocks(bricks);                    // blocs des granularités cochées (superposition)
        this.Refresh();
    }

    /// <summary>Concatène les blocs des granularités cochées (Session dessous, 30 min dessus).</summary>
    private VpBlock[] BuildAllBlocks(SortedDictionary<long, Dictionary<double, (double buy, double sell)>> bricks)
    {
        var all = new List<VpBlock>();
        if (AfficheSession) all.AddRange(BuildBlocksFor(bricks, VpPeriode.Session));
        if (Affiche1h) all.AddRange(BuildBlocksFor(bricks, VpPeriode.Heure1));
        if (Affiche30m) all.AddRange(BuildBlocksFor(bricks, VpPeriode.Min30));   // finest -> dessiné en dernier
        return all.ToArray();
    }

    /// <summary>Regroupe les sous-briques 30 min en blocs de profil selon la granularité choisie.</summary>
    private VpBlock[] BuildBlocksFor(SortedDictionary<long, Dictionary<double, (double buy, double sell)>> bricks, VpPeriode periode)
    {
        var blocks = new List<VpBlock>();
        var run = new Dictionary<double, (double buy, double sell)>();
        long groupKey = -1; long startTs = 0, lastTs = 0; string session = "hors";

        void Flush()
        {
            if (run.Count == 0 || session == "hors") { run.Clear(); return; }
            long endTs = periode == VpPeriode.Session ? lastTs + DemiMs
                       : (groupKey + (periode == VpPeriode.Min30 ? DemiMs : HeureMs));
            blocks.Add(MakeBlock(run, startTs, endTs, session, periode));
            run.Clear();
        }

        foreach (var kv in bricks)
        {
            long ts = kv.Key;
            string s = ClassifySession(FromMs(ts));
            long key = periode switch
            {
                VpPeriode.Session => 0,                      // regroupé par session (via changement de s)
                VpPeriode.Min30 => ts,
                _ => ts / HeureMs * HeureMs,
            };
            bool nouveau = s != session || (periode != VpPeriode.Session && key != groupKey);
            if (nouveau) { Flush(); session = s; groupKey = key; startTs = ts; }
            lastTs = ts;
            if (s != "hors")
                foreach (var lv in kv.Value)
                {
                    var cell = run.TryGetValue(lv.Key, out var cur) ? cur : (0.0, 0.0);
                    run[lv.Key] = (cell.Item1 + lv.Value.buy, cell.Item2 + lv.Value.sell);
                }
        }
        Flush();
        return blocks.ToArray();
    }

    private VpBlock MakeBlock(Dictionary<double, (double buy, double sell)> profil, long startTs, long endTs, string name, VpPeriode periode)
    {
        var (poc, vah, val) = ValueArea(profil, CouverturePct / 100.0);
        double min = double.MaxValue, max = double.MinValue, maxTot = 0, maxAbsDelta = 0;
        foreach (var lv in profil)
        {
            min = Math.Min(min, lv.Key); max = Math.Max(max, lv.Key);
            maxTot = Math.Max(maxTot, lv.Value.buy + lv.Value.sell);
            maxAbsDelta = Math.Max(maxAbsDelta, Math.Abs(lv.Value.buy - lv.Value.sell));
        }
        return new VpBlock
        {
            Start = FromMs(startTs), End = FromMs(endTs), Name = name, Periode = periode,
            Profile = new Dictionary<double, (double, double)>(profil),
            Poc = poc, Vah = vah, Val = val, MinLevel = min, MaxLevel = max,
            MaxTot = maxTot, MaxAbsDelta = maxAbsDelta
        };
    }

    /// <summary>Export session-developing (1 ligne / barre 1H) → parité vs features_vp.csv.</summary>
    private void ExportFeatures(SortedDictionary<long, Dictionary<double, (double buy, double sell)>> bricks)
    {
        if (!ExportParite) return;
        System.IO.StreamWriter w;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FichierParite)!);
            w = new System.IO.StreamWriter(FichierParite, false);
        }
        catch { return; }
        using (w)
        {
            w.WriteLine("time,session,barres,delta,poc,vah,val");
            var run = new Dictionary<double, (double buy, double sell)>();
            string session = "hors"; int barres = 0; double deltaHeure = 0;
            double poc = 0, vah = 0, val = 0; bool has = false;
            foreach (var kv in bricks)
            {
                long ts = kv.Key;
                string s = ClassifySession(FromMs(ts));
                if (s != session) { session = s; barres = 0; has = false; if (s != "hors") run.Clear(); }
                if (session != "hors")
                    foreach (var lv in kv.Value)
                    {
                        var cell = run.TryGetValue(lv.Key, out var cur) ? cur : (0.0, 0.0);
                        run[lv.Key] = (cell.Item1 + lv.Value.buy, cell.Item2 + lv.Value.sell);
                    }
                foreach (var lv in kv.Value) deltaHeure += lv.Value.buy - lv.Value.sell;
                if (ts % HeureMs == 0) continue;
                DateTime tOpen = FromMs(ts - DemiMs);
                if (session != "hors" && run.Count > 0) { barres++; (poc, vah, val) = ValueArea(run, CouverturePct / 100.0); has = true; }
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss}+00:00,{1},{2},{3},{4},{5},{6}",
                    tOpen, session, barres, deltaHeure,
                    has ? poc : (object)"", has ? vah : (object)"", has ? val : (object)""));
                deltaHeure = 0;
            }
        }
    }

    private static (double poc, double vah, double val) ValueArea(
        Dictionary<double, (double buy, double sell)> profil, double couverture)
    {
        var niveaux = new List<double>(profil.Keys);
        niveaux.Sort();
        double Tot(double lv) => profil[lv].buy + profil[lv].sell;
        double total = 0; foreach (var lv in niveaux) total += Tot(lv);
        int pocIdx = 0; double best = double.MinValue;
        for (int k = 0; k < niveaux.Count; k++)
            if (Tot(niveaux[k]) > best) { best = Tot(niveaux[k]); pocIdx = k; }

        int lo = pocIdx, hi = pocIdx;
        double acquis = Tot(niveaux[pocIdx]);
        while (acquis < couverture * total && (lo > 0 || hi < niveaux.Count - 1))
        {
            double haut = hi < niveaux.Count - 1 ? Tot(niveaux[hi + 1]) : -1.0;
            double bas = lo > 0 ? Tot(niveaux[lo - 1]) : -1.0;
            if (haut >= bas) { hi++; acquis += Tot(niveaux[hi]); }
            else { lo--; acquis += Tot(niveaux[lo]); }
        }
        return (niveaux[pocIdx], niveaux[hi], niveaux[lo]);
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
