using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using TradingPlatform.BusinessLayer;
using Hybrides;

namespace SmaSuiveurVisuel;

/// <summary>
/// Hybride H2 SMA Suiveur — le VISUEL sur le graphique (graphe NQ 1 m).
///
/// DEUX sources (paramètre « Source ») :
///   • Simulation : rejoue la logique de la stratégie sur les barres — un trade à CHAQUE
///     croisement (utile pour montrer ce que ferait l'AUTO) ;
///   • Réel : lit le journal NDJSON du jour (via <see cref="LecteurJournalTrades"/>) et ne
///     dessine QUE tes trades confirmés — l'escalier RÉEL du stop, tes vrais chiffres.
///
/// Rendu (OnPaintChart) : escalier ambre du stop suiveur, bande risque(rouge)/profit(vert),
/// flèche d'entrée, point de sortie, étiquettes, panneau. Couleurs / épaisseurs / opacité /
/// visibilité de chaque élément sont paramétrables. Décisions aux clôtures. N'émet rien.
/// </summary>
public sealed class SmaSuiveurVisuelIndicator : Indicator
{
    // ── Stratégie (mêmes formules que H2) ──────────────────────────────────────────────
    [InputParameter("SMA rapide (1 m)", 0, 2, 100, 1, 0)]
    public int SmaRapide = 3;

    [InputParameter("SMA lente (1 m)", 1, 3, 200, 1, 0)]
    public int SmaLente = 9;

    [InputParameter("Période ATR (1 m)", 2, 2, 100, 1, 0)]
    public int AtrPeriode = 7;

    [InputParameter("Stop / suiveur (× ATR)", 3, 0.5, 10, 0.5, 1)]
    public double StopMult = 2.0;

    [InputParameter("Entrées à partir de (HH:mm ET)", 5)]
    public string EntreesDebutEt = "09:30";

    [InputParameter("Entrées jusqu'à (HH:mm ET)", 6)]
    public string EntreesFinEt = "15:30";

    [InputParameter("Flat forcé à (HH:mm ET)", 7)]
    public string HeureFlatEt = "16:55";

    [InputParameter("Cooldown après sortie (minutes)", 8, 0, 120, 1, 0)]
    public int CooldownMin = 0;

    [InputParameter("Restreindre à la séance NY (décoché = 24 h)", 9)]
    public bool SeanceNY = false;

    // ── Source ─────────────────────────────────────────────────────────────────────────
    [InputParameter("Source", 12, variants: new object[]
        { "Simulation (auto)", ModeSource.Simulation, "Réel (journal confirmé)", ModeSource.Reel })]
    public ModeSource Source = ModeSource.Simulation;

    [InputParameter("Dossier des journaux (mode Réel)", 13)]
    public string DossierJournaux = @"H:\IndicesBoursiers\automatisation\journaux";

    // ── Visibilité par élément ─────────────────────────────────────────────────────────
    [InputParameter("Afficher les SMA", 14)] public bool AfficherSma = true;
    [InputParameter("Afficher l'escalier du stop", 15)] public bool AfficherEscalier = true;
    [InputParameter("Afficher la bande risque/profit", 16)] public bool AfficherBande = true;
    [InputParameter("Afficher l'entrée (flèche + ligne)", 17)] public bool AfficherEntree = true;
    [InputParameter("Afficher le point de sortie", 18)] public bool AfficherSortie = true;
    [InputParameter("Panneau de résultats", 10)] public bool AfficherPanneau = true;
    [InputParameter("Étiquette de résultat par trade", 11)] public bool AfficherEtiquettes = true;

    // ── Couleurs ───────────────────────────────────────────────────────────────────────
    [InputParameter("Couleur SMA rapide", 20)] public Color CoulSmaRapide = Color.DodgerBlue;
    [InputParameter("Couleur SMA lente", 21)] public Color CoulSmaLente = Color.Orange;
    [InputParameter("Couleur escalier du stop", 22)] public Color CoulEscalier = Color.FromArgb(255, 255, 190, 70);
    [InputParameter("Couleur zone à risque", 23)] public Color CoulZoneRisque = Color.OrangeRed;
    [InputParameter("Couleur zone de profit", 24)] public Color CoulZoneProfit = Color.LimeGreen;
    [InputParameter("Couleur entrée longue", 25)] public Color CoulEntreeLong = Color.LimeGreen;
    [InputParameter("Couleur entrée courte", 26)] public Color CoulEntreeShort = Color.Red;

    // ── Traits / opacité ───────────────────────────────────────────────────────────────
    [InputParameter("Épaisseur des SMA", 30, 1, 6, 1, 0)] public int EpaisseurSma = 2;
    [InputParameter("Épaisseur de l'escalier", 31, 0.5, 6, 0.5, 1)] public double EpaisseurEscalier = 2.2;
    [InputParameter("Style ligne d'entrée", 32, variants: new object[]
        { "Points", StyleTrait.Points, "Tirets", StyleTrait.Tirets, "Plein", StyleTrait.Plein })]
    public StyleTrait StyleEntree = StyleTrait.Points;
    [InputParameter("Opacité des zones (0-255)", 33, 0, 255, 1, 0)] public int OpaciteZones = 40;

    private const int LRapide = 0, LLente = 1;
    private const int MaxTrades = 1000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private DeclencheurSmaCross _cross = null!;
    private AtrWilder _atr = null!;
    private LecteurJournalTrades? _lecteur;
    private int _debut, _fin, _flat;
    private double _tick = 0.25;

    private sealed class Trade
    {
        public DateTime EntreeTemps;
        public double EntreePrix, StopInitial, Stop, Extreme;
        public int Sens;
        public readonly List<(DateTime t, double stop)> Trail = new();
        public DateTime? SortieTemps;
        public double SortieNiveau;
        public char SortieType;                   // 'S' stop, 'X' croisement inverse, 'F' flat
        public double Pts, R;
    }

    private readonly object _lock = new();
    private readonly List<Trade> _trades = new();
    private Trade? _courant;
    private DateTime _derniereBarreTraitee = DateTime.MinValue;
    private DateTime _dernierTempsBarre = DateTime.MinValue;
    private DateTime _sortieUtc = DateTime.MinValue;

    // GDI+ reconstruits dans OnInit (dépendent des paramètres).
    private Brush _fillProfit = null!, _fillRisque = null!;
    private Pen _stopPen = null!, _lnEntree = null!;
    private Brush _triLong = null!, _triShort = null!, _dotVert = null!, _dotRouge = null!;
    private Pen _dotBord = null!;
    private readonly Brush _txtVert = new SolidBrush(Color.FromArgb(240, 190, 255, 190));
    private readonly Brush _txtRouge = new SolidBrush(Color.FromArgb(240, 255, 180, 165));
    private readonly Brush _pillBg = new SolidBrush(Color.FromArgb(195, 14, 18, 24));
    private readonly Brush _panelBg = new SolidBrush(Color.FromArgb(165, 18, 22, 28));
    private readonly Pen _panelBord = new(Color.FromArgb(90, 120, 130, 140), 1f);
    private readonly Brush _panelTitre = new SolidBrush(Color.FromArgb(235, 225, 231, 238));
    private readonly Brush _panelPos = new SolidBrush(Color.FromArgb(235, 150, 230, 160));
    private readonly Brush _panelNeg = new SolidBrush(Color.FromArgb(235, 240, 150, 130));
    private readonly Font _font = new("Segoe UI", 8f);
    private readonly Font _fontPan = new("Segoe UI", 9f);

    public SmaSuiveurVisuelIndicator()
    {
        Name = "SMA Suiveur";
        Description = "Croisement SMA 3/9 (1 m) + stop suiveur en escalier — simulation OU journal réel (graphe NQ 1 m)";
        SeparateWindow = false;
        AddLineSeries("SMA rapide", Color.DodgerBlue, 2, LineStyle.Solid);
        AddLineSeries("SMA lente", Color.Orange, 2, LineStyle.Solid);
    }

    protected override void OnInit()
    {
        _cross = new DeclencheurSmaCross(SmaRapide, SmaLente);
        _atr = new AtrWilder(AtrPeriode);
        _debut = CadreSeance.ParseHeure(EntreesDebutEt);
        _fin = CadreSeance.ParseHeure(EntreesFinEt);
        _flat = CadreSeance.ParseHeure(HeureFlatEt);
        _tick = this.Symbol?.TickSize is { } ts && ts > 0 ? ts : 0.25;
        _derniereBarreTraitee = DateTime.MinValue;
        _dernierTempsBarre = DateTime.MinValue;
        _sortieUtc = DateTime.MinValue;
        lock (_lock) { _trades.Clear(); _courant = null; }
        _lecteur = new LecteurJournalTrades(DossierJournaux, "sma_suiveur_nq", _tick);
        ConstruireStyles();
    }

    private void ConstruireStyles()
    {
        int op = Math.Clamp(OpaciteZones, 0, 255);
        _fillProfit = new SolidBrush(Color.FromArgb(op, CoulZoneProfit));
        _fillRisque = new SolidBrush(Color.FromArgb(op, CoulZoneRisque));
        _stopPen = new Pen(CoulEscalier, (float)Math.Max(0.5, EpaisseurEscalier));
        _lnEntree = new Pen(Color.FromArgb(120, Color.Gainsboro), 1f) { DashStyle = Dash(StyleEntree) };
        _triLong = new SolidBrush(CoulEntreeLong);
        _triShort = new SolidBrush(CoulEntreeShort);
        _dotVert = new SolidBrush(CoulZoneProfit);
        _dotRouge = new SolidBrush(CoulZoneRisque);
        _dotBord = new Pen(Color.FromArgb(230, 20, 24, 30), 1.2f);

        // Les SMA sont des line series natives : on applique couleur/épaisseur/visibilité ici.
        int w = Math.Max(1, EpaisseurSma);
        LinesSeries[LRapide].Color = AfficherSma ? CoulSmaRapide : Color.Transparent;
        LinesSeries[LLente].Color = AfficherSma ? CoulSmaLente : Color.Transparent;
        LinesSeries[LRapide].Width = w;
        LinesSeries[LLente].Width = w;
    }

    private static DashStyle Dash(StyleTrait s) => s switch
    {
        StyleTrait.Plein => DashStyle.Solid,
        StyleTrait.Tirets => DashStyle.Dash,
        _ => DashStyle.Dot,
    };

    protected override void OnUpdate(UpdateArgs args)
    {
        if (args.Reason == UpdateReason.HistoricalBar)
            TraiterBarreClose(0);
        else if (Count > 1)
            TraiterBarreClose(1);

        if (_cross.Pret)
        {
            SetValue(_cross.Rapide, LRapide, 0);
            SetValue(_cross.Lente, LLente, 0);
        }
        _dernierTempsBarre = this.Time(0);
    }

    private void TraiterBarreClose(int offset)
    {
        var ouverture = this.Time(offset);
        if (ouverture <= _derniereBarreTraitee) return;
        _derniereBarreTraitee = ouverture;

        double haut = this.GetPrice(PriceType.High, offset);
        double bas = this.GetPrice(PriceType.Low, offset);
        double close = this.GetPrice(PriceType.Close, offset);
        var finUtc = ouverture.AddMinutes(1);
        var (_, m) = CadreSeance.HeureEt(finUtc);

        _atr.Ajouter(haut, bas, close);
        _cross.Ajouter(close);
        if (_cross.Pret)
        {
            SetValue(_cross.Rapide, LRapide, offset);
            SetValue(_cross.Lente, LLente, offset);
        }

        // En mode Réel, les SMA restent calculées ci-dessus mais les TRADES viennent du journal
        // (pas de simulation) : on s'arrête ici.
        if (Source == ModeSource.Reel) return;

        int cr = _cross.Croisement;

        // 1) EN POSITION : extrême favorable + stop suiveur, à CHAQUE barre 1 m.
        if (_courant is { } tr)
        {
            tr.Extreme = tr.Sens > 0 ? Math.Max(tr.Extreme, haut) : Math.Min(tr.Extreme, bas);
            if ((tr.Sens > 0 && bas <= tr.Stop) || (tr.Sens < 0 && haut >= tr.Stop))
                Fermer(ouverture, tr.Stop, 'S');
            else if (SeanceNY && m >= _flat)
                Fermer(ouverture, close, 'F');
            else if ((tr.Sens > 0 && cr < 0) || (tr.Sens < 0 && cr > 0))
                Fermer(ouverture, close, 'X');
            else if (_atr.Pret)
            {
                double cand = tr.Sens > 0
                    ? Math.Round((tr.Extreme - StopMult * _atr.Valeur) / _tick) * _tick
                    : Math.Round((tr.Extreme + StopMult * _atr.Valeur) / _tick) * _tick;
                if ((tr.Sens > 0 && cand > tr.Stop) || (tr.Sens < 0 && cand < tr.Stop))
                    tr.Stop = cand;
                lock (_lock) tr.Trail.Add((ouverture, tr.Stop));
            }
            return;
        }

        // 2) ENTRÉE sur croisement.
        if (cr != 0 && _atr.Pret && CooldownOk(finUtc) && (!SeanceNY || (m > _debut && m <= _fin)))
        {
            double stopInit = cr > 0 ? close - StopMult * _atr.Valeur : close + StopMult * _atr.Valeur;
            stopInit = Math.Round(stopInit / _tick) * _tick;
            var t = new Trade
            {
                EntreeTemps = ouverture, EntreePrix = close, Sens = cr,
                StopInitial = stopInit, Stop = stopInit, Extreme = close,
            };
            t.Trail.Add((ouverture, stopInit));
            lock (_lock)
            {
                _trades.Add(t);
                if (_trades.Count > MaxTrades) _trades.RemoveAt(0);
                _courant = t;
            }
        }
    }

    private void Fermer(DateTime temps, double niveau, char type)
    {
        lock (_lock)
        {
            if (_courant is null) return;
            var t = _courant;
            t.SortieTemps = temps;
            t.SortieNiveau = niveau;
            t.SortieType = type;
            t.Pts = (niveau - t.EntreePrix) * t.Sens;
            double risque = Math.Abs(t.EntreePrix - t.StopInitial);
            t.R = risque > 0 ? t.Pts / risque : 0;
            _courant = null;
        }
        _sortieUtc = temps.AddMinutes(1);
    }

    private bool CooldownOk(DateTime finUtc) =>
        _sortieUtc == DateTime.MinValue || (finUtc - _sortieUtc).TotalMinutes >= CooldownMin;

    /// <summary>Mappe les trades réels du journal vers la structure de rendu.</summary>
    private Trade[] TradesReels()
    {
        var reels = _lecteur?.Trades(DateTime.UtcNow) ?? Array.Empty<LecteurJournalTrades.TradeReel>();
        var list = new List<Trade>(reels.Length);
        foreach (var r in reels)
        {
            var t = new Trade
            {
                EntreeTemps = r.EntreeTemps, EntreePrix = r.EntreePrix, Sens = r.Sens,
                StopInitial = double.IsNaN(r.StopInitial) ? r.Sl : r.StopInitial,
                Stop = r.Sl, Extreme = r.EntreePrix,
                SortieTemps = r.SortieTemps, SortieNiveau = r.SortieNiveau,
                SortieType = r.SortieType, Pts = r.Pts, R = r.R,
            };
            foreach (var st in r.Trail) t.Trail.Add(st);
            list.Add(t);
        }
        return list.ToArray();
    }

    // ─────────────────────────────────────────────────────── LE RENDU ────────────────
    public override void OnPaintChart(PaintChartEventArgs args)
    {
        var conv = this.CurrentChart?.MainWindow?.CoordinatesConverter;
        if (conv is null) return;
        Trade[] trades;
        DateTime finOuverte = _dernierTempsBarre;
        if (Source == ModeSource.Reel)
            trades = TradesReels();
        else
            lock (_lock) trades = _trades.ToArray();

        var gr = args.Graphics;
        var rect = args.Rectangle;
        gr.SmoothingMode = SmoothingMode.AntiAlias;

        foreach (var t in trades)
        {
            var borneDroite = t.SortieTemps ?? finOuverte;
            float xL = (float)conv.GetChartX(t.EntreeTemps);
            float xR = (float)conv.GetChartX(borneDroite);
            if (xR < xL) xR = xL;
            if (xR < rect.Left - 4 || xL > rect.Right + 4) continue;
            float yEntree = (float)conv.GetChartY(t.EntreePrix);

            (DateTime t, double stop)[] trail = t.Trail.ToArray();

            if (AfficherEntree)
                gr.DrawLine(_lnEntree, xL, yEntree, xR, yEntree);

            for (int i = 0; i < trail.Length; i++)
            {
                float xi = (float)conv.GetChartX(trail[i].t);
                float xn = i + 1 < trail.Length ? (float)conv.GetChartX(trail[i + 1].t) : xR;
                if (xn < xi) xn = xi;
                float yi = (float)conv.GetChartY(trail[i].stop);
                bool profit = t.Sens > 0 ? trail[i].stop > t.EntreePrix : trail[i].stop < t.EntreePrix;
                if (AfficherBande)
                    RectVertical(gr, profit ? _fillProfit : _fillRisque, xi, xn - xi, yi, yEntree);
                if (AfficherEscalier)
                {
                    gr.DrawLine(_stopPen, xi, yi, xn, yi);
                    if (i + 1 < trail.Length)
                    {
                        float yn = (float)conv.GetChartY(trail[i + 1].stop);
                        gr.DrawLine(_stopPen, xn, yi, xn, yn);
                    }
                }
            }

            if (AfficherEntree)
                Triangle(gr, t.Sens > 0 ? _triLong : _triShort, xL, yEntree, t.Sens);

            if (t.SortieTemps is not null && AfficherSortie)
            {
                bool gain = t.Pts >= 0;
                float yNiv = (float)conv.GetChartY(t.SortieNiveau);
                gr.FillEllipse(gain ? _dotVert : _dotRouge, xR - 4.5f, yNiv - 4.5f, 9f, 9f);
                gr.DrawEllipse(_dotBord, xR - 4.5f, yNiv - 4.5f, 9f, 9f);

                if (AfficherEtiquettes && (xR - xL) >= 6f)
                {
                    string s = $"{t.Pts.ToString("+0.0;-0.0", Inv)} ({t.R.ToString("+0.0;-0.0", Inv)}R)";
                    var sz = gr.MeasureString(s, _font);
                    float lx = Math.Max(rect.Left + 2f, Math.Min(xR - sz.Width / 2f, rect.Right - sz.Width - 6f));
                    float ly = gain ? yNiv - sz.Height - 3f : yNiv + 3f;
                    gr.FillRectangle(_pillBg, lx - 3f, ly - 1f, sz.Width + 6f, sz.Height + 2f);
                    gr.DrawString(s, _font, gain ? _txtVert : _txtRouge, lx, ly);
                }
            }
        }

        if (AfficherPanneau) DessinerPanneau(gr, rect, trades);
    }

    private void DessinerPanneau(Graphics gr, Rectangle rect, Trade[] trades)
    {
        int nb = 0, gagn = 0;
        double cumPts = 0, cumR = 0;
        foreach (var t in trades)
        {
            if (t.SortieTemps is null) continue;
            nb++; cumPts += t.Pts; cumR += t.R;
            if (t.Pts >= 0) gagn++;
        }
        double taux = nb > 0 ? 100.0 * gagn / nb : 0;

        string l1 = "SMA Suiveur" + (Source == ModeSource.Reel ? " · réel" : " · simulation");
        string l2 = $"Trades {nb}   ·   {taux.ToString("0", Inv)}% gagnants";
        string l3 = $"Cumul  {cumPts.ToString("+0.0;-0.0;0.0", Inv)} pts   ·   {cumR.ToString("+0.0;-0.0;0.0", Inv)} R";

        float wMax = 0;
        foreach (var s in new[] { l1, l2, l3 }) wMax = Math.Max(wMax, gr.MeasureString(s, _fontPan).Width);
        float pw = wMax + 20f, ph = 62f;
        float px = rect.Right - pw - 12f, py = rect.Top + 12f;

        gr.FillRectangle(_panelBg, px, py, pw, ph);
        gr.DrawRectangle(_panelBord, px, py, pw, ph);
        gr.DrawString(l1, _fontPan, _panelTitre, px + 10f, py + 6f);
        gr.DrawString(l2, _fontPan, _panelTitre, px + 10f, py + 23f);
        gr.DrawString(l3, _fontPan, cumPts >= 0 ? _panelPos : _panelNeg, px + 10f, py + 40f);
    }

    private static void RectVertical(Graphics gr, Brush fill, float x, float w, float yA, float yB)
    {
        float top = Math.Min(yA, yB), h = Math.Max(1f, Math.Abs(yB - yA));
        gr.FillRectangle(fill, x, top, Math.Max(1f, w), h);
    }

    private static void Triangle(Graphics gr, Brush brush, float x, float y, int sens)
    {
        float d = sens > 0 ? 1f : -1f;
        var pts = new[]
        {
            new PointF(x - 5f, y + 9f * d),
            new PointF(x + 5f, y + 9f * d),
            new PointF(x, y + 2f * d),
        };
        gr.FillPolygon(brush, pts);
    }
}
