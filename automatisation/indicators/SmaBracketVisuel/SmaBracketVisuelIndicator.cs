using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using TradingPlatform.BusinessLayer;
using Hybrides;

namespace SmaBracketVisuel;

/// <summary>
/// Hybride H1 SMA Bracket — le VISUEL sur le graphique (graphe NQ 1 m). Rejoue la logique
/// EXACTE de la stratégie `Hybride H1 SMA Bracket (NQ)` et du jumeau `sma_bracket_nq.py`
/// (mêmes classes d'indicateurs — Compile Include de hybrides/Indicateurs.cs).
///
/// Rendu (2026-07-22) : deux SMA (rapide 2 = bleue, lente 6 = orange) en line series ; le
/// reste peint en `OnPaintChart`. Pour CHAQUE trade :
///   - encadré **vert** (entrée → TP) et **rouge** (entrée → SL), de l'entrée à la sortie ;
///     à la clôture, la zone ATTEINTE est mise en évidence (forte opacité) et l'autre estompée ;
///   - **lignes pointillées** SL (rouge) et TP (vert) aux niveaux, + fine ligne d'entrée ;
///   - flèche d'entrée (triangle) et **point de sortie posé EXACTEMENT sur le niveau touché** ;
///   - **étiquette de résultat** du trade (points + R) près de la sortie.
/// Plus un **panneau de résultats** (haut-droite) : trades, TP/SL, taux, cumul (points, R).
/// Décisions AUX CLÔTURES de barres. N'émet rien (ni ordre, ni pop-up, ni journal).
/// </summary>
public sealed class SmaBracketVisuelIndicator : Indicator
{
    [InputParameter("SMA rapide (1 m)", 0, 2, 100, 1, 0)]
    public int SmaRapide = 2;

    [InputParameter("SMA lente (1 m)", 1, 3, 200, 1, 0)]
    public int SmaLente = 6;

    [InputParameter("Période ATR (1 m)", 2, 2, 100, 1, 0)]
    public int AtrPeriode = 7;

    [InputParameter("Stop (× ATR)", 3, 0.5, 10, 0.5, 1)]
    public double StopMult = 1.0;

    [InputParameter("Take profit (× R)", 4, 0.5, 10, 0.5, 1)]
    public double TpR = 1.0;

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

    [InputParameter("Panneau de résultats", 10)]
    public bool AfficherPanneau = true;

    [InputParameter("Étiquette de résultat par trade", 11)]
    public bool AfficherEtiquettes = true;

    private const int LRapide = 0, LLente = 1;
    private const int MaxTrades = 1000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private DeclencheurSmaCross _cross = null!;
    private AtrWilder _atr = null!;
    private int _debut, _fin, _flat;

    private sealed class Trade
    {
        public DateTime EntreeTemps;
        public double EntreePrix, Sl, Tp;
        public int Sens;                          // +1 long, -1 short
        public DateTime? SortieTemps;
        public double SortieNiveau;               // le prix EXACT où le trade s'est fermé
        public char SortieType;                   // 'T' TP, 'S' SL, 'F' flat
        public double Pts;                         // résultat en points (signé)
        public double R;                           // résultat en R (signé)
    }

    private readonly object _lock = new();
    private readonly List<Trade> _trades = new();
    private Trade? _courant;
    private DateTime _derniereBarreTraitee = DateTime.MinValue;
    private DateTime _dernierTempsBarre = DateTime.MinValue;
    private DateTime _sortieUtc = DateTime.MinValue;

    // GDI+ (créés une fois — patron VpSessionNq).
    private readonly Brush _vFort = new SolidBrush(Color.FromArgb(90, Color.LimeGreen));
    private readonly Brush _vNeutre = new SolidBrush(Color.FromArgb(34, Color.LimeGreen));
    private readonly Brush _vFaible = new SolidBrush(Color.FromArgb(14, Color.LimeGreen));
    private readonly Brush _rFort = new SolidBrush(Color.FromArgb(90, Color.OrangeRed));
    private readonly Brush _rNeutre = new SolidBrush(Color.FromArgb(34, Color.OrangeRed));
    private readonly Brush _rFaible = new SolidBrush(Color.FromArgb(14, Color.OrangeRed));
    private readonly Pen _lnTp = new(Color.FromArgb(200, Color.LimeGreen), 1.2f) { DashStyle = DashStyle.Dash };
    private readonly Pen _lnTpFort = new(Color.LimeGreen, 2.2f) { DashStyle = DashStyle.Dash };
    private readonly Pen _lnSl = new(Color.FromArgb(200, Color.OrangeRed), 1.2f) { DashStyle = DashStyle.Dash };
    private readonly Pen _lnSlFort = new(Color.OrangeRed, 2.2f) { DashStyle = DashStyle.Dash };
    private readonly Pen _lnEntree = new(Color.FromArgb(120, Color.Gainsboro), 1f) { DashStyle = DashStyle.Dot };
    private readonly Brush _dotVert = new SolidBrush(Color.LimeGreen);
    private readonly Brush _dotRouge = new SolidBrush(Color.Red);
    private readonly Brush _dotOrange = new SolidBrush(Color.Orange);
    private readonly Pen _dotBord = new(Color.FromArgb(230, 20, 24, 30), 1.2f);
    private readonly Brush _triLong = new SolidBrush(Color.LimeGreen);
    private readonly Brush _triShort = new SolidBrush(Color.Red);
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

    public SmaBracketVisuelIndicator()
    {
        Name = "Hybride H1 SMA Bracket (visuel)";
        Description = "Croisement SMA 2/6 (1 m) + zone de trade (bracket) — visuel de la stratégie H1 (graphe NQ 1 m)";
        SeparateWindow = false;
        AddLineSeries("SMA rapide (2)", Color.DodgerBlue, 2, LineStyle.Solid);
        AddLineSeries("SMA lente (6)", Color.Orange, 2, LineStyle.Solid);
    }

    protected override void OnInit()
    {
        _cross = new DeclencheurSmaCross(SmaRapide, SmaLente);
        _atr = new AtrWilder(AtrPeriode);
        _debut = CadreSeance.ParseHeure(EntreesDebutEt);
        _fin = CadreSeance.ParseHeure(EntreesFinEt);
        _flat = CadreSeance.ParseHeure(HeureFlatEt);
        _derniereBarreTraitee = DateTime.MinValue;
        _dernierTempsBarre = DateTime.MinValue;
        _sortieUtc = DateTime.MinValue;
        lock (_lock) { _trades.Clear(); _courant = null; }
    }

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

        // 1) EN POSITION : bracket simulé (SL prioritaire), puis flat forcé. Ignore les croisements.
        if (_courant is { } tr)
        {
            if ((tr.Sens > 0 && bas <= tr.Sl) || (tr.Sens < 0 && haut >= tr.Sl))
                Fermer(ouverture, tr.Sl, 'S');
            else if ((tr.Sens > 0 && haut >= tr.Tp) || (tr.Sens < 0 && bas <= tr.Tp))
                Fermer(ouverture, tr.Tp, 'T');
            else if (SeanceNY && m >= _flat)
                Fermer(ouverture, close, 'F');
        }
        // 2) ENTRÉE sur croisement (flat + [fenêtre si séance NY] + cooldown + ATR prêt).
        else if (_cross.Croisement != 0 && _atr.Pret && CooldownOk(finUtc)
                 && (!SeanceNY || (m > _debut && m <= _fin)))
        {
            int sens = _cross.Croisement;
            double r = StopMult * _atr.Valeur;
            var t = new Trade
            {
                EntreeTemps = ouverture,
                EntreePrix = close,
                Sens = sens,
                Sl = sens > 0 ? close - r : close + r,
                Tp = sens > 0 ? close + TpR * r : close - TpR * r,
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
            t.Pts = (niveau - t.EntreePrix) * t.Sens;                    // points signés
            double risque = Math.Abs(t.EntreePrix - t.Sl);
            t.R = risque > 0 ? t.Pts / risque : 0;                       // en R (SL = -1, TP = +TpR)
            _courant = null;
        }
        _sortieUtc = temps.AddMinutes(1);
    }

    private bool CooldownOk(DateTime finUtc) =>
        _sortieUtc == DateTime.MinValue || (finUtc - _sortieUtc).TotalMinutes >= CooldownMin;

    // ─────────────────────────────────────────────────────── LE RENDU ────────────────
    public override void OnPaintChart(PaintChartEventArgs args)
    {
        var conv = this.CurrentChart?.MainWindow?.CoordinatesConverter;
        if (conv is null) return;
        Trade[] trades;
        DateTime finOuverte;
        lock (_lock) { trades = _trades.ToArray(); finOuverte = _dernierTempsBarre; }

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

            // Zones : à la clôture, mettre en ÉVIDENCE le côté atteint, estomper l'autre.
            Brush bVert = t.SortieType == 'T' ? _vFort : t.SortieType == 'S' ? _vFaible : _vNeutre;
            Brush bRouge = t.SortieType == 'S' ? _rFort : t.SortieType == 'T' ? _rFaible : _rNeutre;
            RectVertical(gr, bVert, xL, w, yEntree, yTp);
            RectVertical(gr, bRouge, xL, w, yEntree, ySl);

            // Lignes pointillées SL/TP (+ fine ligne d'entrée), la touchée épaissie.
            gr.DrawLine(t.SortieType == 'T' ? _lnTpFort : _lnTp, xL, yTp, xR, yTp);
            gr.DrawLine(t.SortieType == 'S' ? _lnSlFort : _lnSl, xL, ySl, xR, ySl);
            gr.DrawLine(_lnEntree, xL, yEntree, xR, yEntree);

            Triangle(gr, t.Sens > 0 ? _triLong : _triShort, xL, yEntree, t.Sens);

            if (t.SortieTemps is not null)
            {
                var brush = t.SortieType switch { 'T' => _dotVert, 'S' => _dotRouge, _ => _dotOrange };
                float yNiv = (float)conv.GetChartY(t.SortieNiveau);
                gr.FillEllipse(brush, xR - 4.5f, yNiv - 4.5f, 9f, 9f);
                gr.DrawEllipse(_dotBord, xR - 4.5f, yNiv - 4.5f, 9f, 9f);

                if (AfficherEtiquettes && w >= 6f)
                {
                    bool gain = t.Pts >= 0;
                    string s = $"{t.Pts.ToString("+0.0;-0.0", Inv)} ({t.R.ToString("+0.0;-0.0", Inv)}R)";
                    var sz = gr.MeasureString(s, _font);
                    // Au bout de la zone (au-dessus du TP / sous le SL), centré sur la boîte,
                    // avec un fond sombre : lisible même par-dessus les chandelles.
                    float lx = xL + w / 2f - sz.Width / 2f;
                    lx = Math.Max(rect.Left + 2f, Math.Min(lx, rect.Right - sz.Width - 6f));
                    float ly = gain ? yTp - sz.Height - 3f : ySl + 3f;
                    gr.FillRectangle(_pillBg, lx - 3f, ly - 1f, sz.Width + 6f, sz.Height + 2f);
                    gr.DrawString(s, _font, gain ? _txtVert : _txtRouge, lx, ly);
                }
            }
        }

        if (AfficherPanneau) DessinerPanneau(gr, rect, trades);
    }

    private void DessinerPanneau(Graphics gr, Rectangle rect, Trade[] trades)
    {
        int nb = 0, tp = 0, sl = 0;
        double cumPts = 0, cumR = 0;
        foreach (var t in trades)
        {
            if (t.SortieTemps is null) continue;
            nb++; cumPts += t.Pts; cumR += t.R;
            if (t.SortieType == 'T') tp++; else if (t.SortieType == 'S') sl++;
        }
        double taux = (tp + sl) > 0 ? 100.0 * tp / (tp + sl) : 0;

        string l1 = "H1 SMA Bracket";
        string l2 = $"Trades {nb}   ·   TP {tp} / SL {sl}   ·   {taux.ToString("0", Inv)}%";
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
