using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using TradingPlatform.BusinessLayer;
using Hybrides;

namespace SmaAnnuleVisuel;

/// <summary>
/// Hybride H3 SMA Annulation — le VISUEL sur le graphique (graphe NQ 1 m).
///
/// DEUX sources (paramètre « Source ») : Simulation (auto) ou Réel (journal NDJSON du jour via
/// <see cref="LecteurJournalTrades"/> — ne dessine QUE tes trades confirmés). H3 = le bracket de
/// H1, MAIS la sortie au croisement inverse ANNULE le bracket : elle se distingue par un
/// **losange** au prix de sortie + une **ligne ambre** (ni SL ni TP atteint). Couleurs /
/// épaisseurs / opacité / visibilité paramétrables. Décisions aux clôtures. N'émet rien.
/// </summary>
public sealed class SmaAnnuleVisuelIndicator : Indicator
{
    [InputParameter("SMA rapide (1 m)", 0, 2, 100, 1, 0)] public int SmaRapide = 3;
    [InputParameter("SMA lente (1 m)", 1, 3, 200, 1, 0)] public int SmaLente = 9;
    [InputParameter("Période ATR (1 m)", 2, 2, 100, 1, 0)] public int AtrPeriode = 7;
    [InputParameter("Stop (× ATR)", 3, 0.5, 10, 0.5, 1)] public double StopMult = 1.0;
    [InputParameter("Take profit (× R)", 4, 0.5, 10, 0.5, 1)] public double TpR = 2.0;
    [InputParameter("Entrées à partir de (HH:mm ET)", 5)] public string EntreesDebutEt = "09:30";
    [InputParameter("Entrées jusqu'à (HH:mm ET)", 6)] public string EntreesFinEt = "15:30";
    [InputParameter("Flat forcé à (HH:mm ET)", 7)] public string HeureFlatEt = "16:55";
    [InputParameter("Cooldown après sortie (minutes)", 8, 0, 120, 1, 0)] public int CooldownMin = 0;
    [InputParameter("Restreindre à la séance NY (décoché = 24 h)", 9)] public bool SeanceNY = false;

    [InputParameter("Source", 12, variants: new object[]
        { "Simulation (auto)", ModeSource.Simulation, "Réel (journal confirmé)", ModeSource.Reel })]
    public ModeSource Source = ModeSource.Simulation;
    [InputParameter("Dossier des journaux (mode Réel)", 13)]
    public string DossierJournaux = @"H:\IndicesBoursiers\automatisation\journaux";

    [InputParameter("Afficher les SMA", 14)] public bool AfficherSma = true;
    [InputParameter("Afficher les zones", 15)] public bool AfficherZones = true;
    [InputParameter("Afficher les lignes SL/TP", 16)] public bool AfficherLignes = true;
    [InputParameter("Afficher l'entrée (flèche + ligne)", 17)] public bool AfficherEntree = true;
    [InputParameter("Afficher la sortie / annulation", 18)] public bool AfficherSortie = true;
    [InputParameter("Panneau de résultats", 10)] public bool AfficherPanneau = true;
    [InputParameter("Étiquette de résultat par trade", 11)] public bool AfficherEtiquettes = true;

    [InputParameter("Couleur SMA rapide", 20)] public Color CoulSmaRapide = Color.DodgerBlue;
    [InputParameter("Couleur SMA lente", 21)] public Color CoulSmaLente = Color.Orange;
    [InputParameter("Couleur zone/TP (profit)", 22)] public Color CoulProfit = Color.LimeGreen;
    [InputParameter("Couleur zone/SL (risque)", 23)] public Color CoulRisque = Color.OrangeRed;
    [InputParameter("Couleur annulation", 24)] public Color CoulAnnulation = Color.FromArgb(255, 255, 200, 90);
    [InputParameter("Couleur entrée longue", 25)] public Color CoulEntreeLong = Color.LimeGreen;
    [InputParameter("Couleur entrée courte", 26)] public Color CoulEntreeShort = Color.Red;

    [InputParameter("Épaisseur des SMA", 30, 1, 6, 1, 0)] public int EpaisseurSma = 2;
    [InputParameter("Épaisseur lignes SL/TP", 31, 0.5, 6, 0.5, 1)] public double EpaisseurLignes = 1.2;
    [InputParameter("Style ligne d'entrée", 32, variants: new object[]
        { "Points", StyleTrait.Points, "Tirets", StyleTrait.Tirets, "Plein", StyleTrait.Plein })]
    public StyleTrait StyleEntree = StyleTrait.Points;
    [InputParameter("Opacité des zones (0-255)", 33, 0, 255, 1, 0)] public int OpaciteZones = 34;

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
        public double EntreePrix, Sl, Tp;
        public int Sens;
        public DateTime? SortieTemps;
        public double SortieNiveau;
        public char SortieType;                   // 'T' TP, 'S' SL, 'X' annulation, 'F' flat
        public double Pts, R;
    }

    private readonly object _lock = new();
    private readonly List<Trade> _trades = new();
    private Trade? _courant;
    private DateTime _derniereBarreTraitee = DateTime.MinValue;
    private DateTime _dernierTempsBarre = DateTime.MinValue;
    private DateTime _sortieUtc = DateTime.MinValue;

    // GDI+ reconstruits dans OnInit.
    private Brush _vFort = null!, _vNeutre = null!, _vFaible = null!;
    private Brush _rFort = null!, _rNeutre = null!, _rFaible = null!;
    private Pen _lnTp = null!, _lnTpFort = null!, _lnSl = null!, _lnSlFort = null!, _lnEntree = null!, _lnAnnul = null!;
    private Brush _triLong = null!, _triShort = null!, _dotVert = null!, _dotRouge = null!;
    private readonly Brush _dotOrange = new SolidBrush(Color.Orange);
    private readonly Pen _dotBord = new(Color.FromArgb(230, 20, 24, 30), 1.2f);
    private readonly Pen _losangeBord = new(Color.FromArgb(235, 235, 240, 245), 1.4f);
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

    public SmaAnnuleVisuelIndicator()
    {
        Name = "SMA Annulation";
        Description = "Croisement SMA 3/9 (1 m), bracket + annulation au croisement inverse — simulation OU journal réel";
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
        _lecteur = new LecteurJournalTrades(DossierJournaux, "sma_annule_nq", _tick);
        ConstruireStyles();
    }

    private void ConstruireStyles()
    {
        int op = Math.Clamp(OpaciteZones, 0, 255);
        int opFort = Math.Min(255, (int)(op * 2.4));
        int opFaible = Math.Max(0, (int)(op * 0.4));
        _vFort = new SolidBrush(Color.FromArgb(opFort, CoulProfit));
        _vNeutre = new SolidBrush(Color.FromArgb(op, CoulProfit));
        _vFaible = new SolidBrush(Color.FromArgb(opFaible, CoulProfit));
        _rFort = new SolidBrush(Color.FromArgb(opFort, CoulRisque));
        _rNeutre = new SolidBrush(Color.FromArgb(op, CoulRisque));
        _rFaible = new SolidBrush(Color.FromArgb(opFaible, CoulRisque));

        float wl = (float)Math.Max(0.5, EpaisseurLignes);
        _lnTp = new Pen(Color.FromArgb(200, CoulProfit), wl) { DashStyle = DashStyle.Dash };
        _lnTpFort = new Pen(CoulProfit, wl + 1f) { DashStyle = DashStyle.Dash };
        _lnSl = new Pen(Color.FromArgb(200, CoulRisque), wl) { DashStyle = DashStyle.Dash };
        _lnSlFort = new Pen(CoulRisque, wl + 1f) { DashStyle = DashStyle.Dash };
        _lnEntree = new Pen(Color.FromArgb(120, Color.Gainsboro), 1f) { DashStyle = Dash(StyleEntree) };
        _lnAnnul = new Pen(CoulAnnulation, wl + 0.4f) { DashStyle = DashStyle.Dash };
        _triLong = new SolidBrush(CoulEntreeLong);
        _triShort = new SolidBrush(CoulEntreeShort);
        _dotVert = new SolidBrush(CoulProfit);
        _dotRouge = new SolidBrush(CoulRisque);

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
        if (args.Reason == UpdateReason.HistoricalBar) TraiterBarreClose(0);
        else if (Count > 1) TraiterBarreClose(1);

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
        int cr = _cross.Croisement;

        if (Source == ModeSource.Reel) return;   // trades = journal, pas de simulation

        // 1) EN POSITION : bracket (SL prioritaire), flat, puis ANNULATION au croisement inverse.
        if (_courant is { } tr)
        {
            if ((tr.Sens > 0 && bas <= tr.Sl) || (tr.Sens < 0 && haut >= tr.Sl))
                Fermer(ouverture, tr.Sl, 'S');
            else if ((tr.Sens > 0 && haut >= tr.Tp) || (tr.Sens < 0 && bas <= tr.Tp))
                Fermer(ouverture, tr.Tp, 'T');
            else if (SeanceNY && m >= _flat)
                Fermer(ouverture, close, 'F');
            else if ((tr.Sens > 0 && cr < 0) || (tr.Sens < 0 && cr > 0))
                Fermer(ouverture, close, 'X');
        }
        // 2) ENTRÉE sur croisement.
        else if (cr != 0 && _atr.Pret && CooldownOk(finUtc) && (!SeanceNY || (m > _debut && m <= _fin)))
        {
            double r = StopMult * _atr.Valeur;
            var t = new Trade
            {
                EntreeTemps = ouverture, EntreePrix = close, Sens = cr,
                Sl = cr > 0 ? close - r : close + r,
                Tp = cr > 0 ? close + TpR * r : close - TpR * r,
            };
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
            double risque = Math.Abs(t.EntreePrix - t.Sl);
            t.R = risque > 0 ? t.Pts / risque : 0;
            _courant = null;
        }
        _sortieUtc = temps.AddMinutes(1);
    }

    private bool CooldownOk(DateTime finUtc) =>
        _sortieUtc == DateTime.MinValue || (finUtc - _sortieUtc).TotalMinutes >= CooldownMin;

    private Trade[] TradesReels()
    {
        var reels = _lecteur?.Trades(DateTime.UtcNow) ?? Array.Empty<LecteurJournalTrades.TradeReel>();
        var list = new List<Trade>(reels.Length);
        foreach (var r in reels)
        {
            double sl = double.IsNaN(r.StopInitial) ? r.Sl : r.StopInitial;
            list.Add(new Trade
            {
                EntreeTemps = r.EntreeTemps, EntreePrix = r.EntreePrix, Sens = r.Sens,
                Sl = sl, Tp = r.Tp,
                SortieTemps = r.SortieTemps, SortieNiveau = r.SortieNiveau,
                SortieType = r.SortieType, Pts = r.Pts, R = r.R,
            });
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
        if (Source == ModeSource.Reel) trades = TradesReels();
        else lock (_lock) trades = _trades.ToArray();

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
            float w = Math.Max(2f, xR - xL);
            float yEntree = (float)conv.GetChartY(t.EntreePrix);
            float ySl = (float)conv.GetChartY(t.Sl);
            float yTp = (float)conv.GetChartY(t.Tp);

            if (AfficherZones)
            {
                Brush bVert = t.SortieType switch { 'T' => _vFort, 'S' or 'X' => _vFaible, _ => _vNeutre };
                Brush bRouge = t.SortieType switch { 'S' => _rFort, 'T' or 'X' => _rFaible, _ => _rNeutre };
                RectVertical(gr, bVert, xL, w, yEntree, yTp);
                RectVertical(gr, bRouge, xL, w, yEntree, ySl);
            }

            if (AfficherLignes)
            {
                gr.DrawLine(t.SortieType == 'T' ? _lnTpFort : _lnTp, xL, yTp, xR, yTp);
                gr.DrawLine(t.SortieType == 'S' ? _lnSlFort : _lnSl, xL, ySl, xR, ySl);
            }
            if (AfficherEntree)
            {
                gr.DrawLine(_lnEntree, xL, yEntree, xR, yEntree);
                Triangle(gr, t.Sens > 0 ? _triLong : _triShort, xL, yEntree, t.Sens);
            }

            if (t.SortieTemps is not null && AfficherSortie)
            {
                bool gain = t.Pts >= 0;
                float yNiv = (float)conv.GetChartY(t.SortieNiveau);
                // Sortie SIGNAL (annulation / flat) : ligne ambre au niveau de sortie.
                if (t.SortieType is 'X' or 'F')
                    gr.DrawLine(_lnAnnul, xL, yNiv, xR, yNiv);

                if (t.SortieType == 'X')       // ANNULATION : losange
                    Losange(gr, gain ? _dotVert : _dotRouge, xR, yNiv, 6f);
                else
                {
                    var brush = t.SortieType switch { 'T' => _dotVert, 'S' => _dotRouge, _ => _dotOrange };
                    gr.FillEllipse(brush, xR - 4.5f, yNiv - 4.5f, 9f, 9f);
                    gr.DrawEllipse(_dotBord, xR - 4.5f, yNiv - 4.5f, 9f, 9f);
                }

                if (AfficherEtiquettes && w >= 6f)
                {
                    string s = $"{t.Pts.ToString("+0.0;-0.0", Inv)} ({t.R.ToString("+0.0;-0.0", Inv)}R)"
                             + (t.SortieType == 'X' ? " ✕" : "");
                    var sz = gr.MeasureString(s, _font);
                    float lx = Math.Max(rect.Left + 2f, Math.Min(xL + w / 2f - sz.Width / 2f, rect.Right - sz.Width - 6f));
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
        int nb = 0, tp = 0, sl = 0, an = 0;
        double cumPts = 0, cumR = 0;
        foreach (var t in trades)
        {
            if (t.SortieTemps is null) continue;
            nb++; cumPts += t.Pts; cumR += t.R;
            if (t.SortieType == 'T') tp++; else if (t.SortieType == 'S') sl++;
            else if (t.SortieType == 'X') an++;
        }

        string l1 = "SMA Annulation" + (Source == ModeSource.Reel ? " · réel" : " · simulation");
        string l2 = $"Trades {nb}   ·   TP {tp} / SL {sl} / annul. {an}";
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
        gr.FillRectangle(fill, x, top, w, h);
    }

    private void Losange(Graphics gr, Brush fill, float x, float y, float r)
    {
        var pts = new[] { new PointF(x, y - r), new PointF(x + r, y), new PointF(x, y + r), new PointF(x - r, y) };
        gr.FillPolygon(fill, pts);
        gr.DrawPolygon(_losangeBord, pts);
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
