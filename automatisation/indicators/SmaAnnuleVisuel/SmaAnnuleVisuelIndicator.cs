using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using TradingPlatform.BusinessLayer;
using Hybrides;

namespace SmaAnnuleVisuel;

/// <summary>
/// Hybride H3 SMA Annulation — le VISUEL sur le graphique (graphe NQ 1 m). Rejoue la logique
/// EXACTE de la stratégie `Hybride H3 SMA Annulation (NQ)` et du jumeau `sma_annule_nq.py`.
///
/// H3 = le bracket de H1 (zones vert/rouge, pointillés SL/TP) MAIS elle sort aussi au
/// **croisement inverse en ANNULANT** le bracket avant SL/TP — c'est ce mécanisme qu'elle
/// prouve. Le visuel distingue les trois issues :
///   - TP touché : cercle vert sur la ligne TP, zone verte mise en évidence ;
///   - SL touché : cercle rouge sur la ligne SL, zone rouge mise en évidence ;
///   - **ANNULATION** (croisement inverse) : **losange** au prix de sortie (au milieu de la
///     zone), les DEUX côtés estompés — ni SL ni TP n'a été atteint, le bracket a été annulé.
/// Étiquette de résultat (points + R) et panneau. Décisions aux clôtures. N'émet rien.
/// </summary>
public sealed class SmaAnnuleVisuelIndicator : Indicator
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
    public double TpR = 2.0;

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

    // GDI+.
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
    private readonly Pen _lnAnnul = new(Color.FromArgb(225, 255, 200, 90), 1.6f) { DashStyle = DashStyle.Dash };   // sortie signal (ambre)
    private readonly Brush _dotVert = new SolidBrush(Color.LimeGreen);
    private readonly Brush _dotRouge = new SolidBrush(Color.Red);
    private readonly Brush _dotOrange = new SolidBrush(Color.Orange);
    private readonly Pen _dotBord = new(Color.FromArgb(230, 20, 24, 30), 1.2f);
    private readonly Pen _losangeBord = new(Color.FromArgb(235, 235, 240, 245), 1.4f);   // annulation : liseré clair
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

    public SmaAnnuleVisuelIndicator()
    {
        Name = "Hybride H3 SMA Annulation (visuel)";
        Description = "Croisement SMA 2/6 (1 m), bracket + annulation au croisement inverse — visuel de H3 (graphe NQ 1 m)";
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
        int cr = _cross.Croisement;

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
                Fermer(ouverture, close, 'X');       // annulation du bracket + sortie
        }
        // 2) ENTRÉE sur croisement.
        else if (cr != 0 && _atr.Pret && CooldownOk(finUtc) && (!SeanceNY || (m > _debut && m <= _fin)))
        {
            double r = StopMult * _atr.Valeur;
            var t = new Trade
            {
                EntreeTemps = ouverture,
                EntreePrix = close,
                Sens = cr,
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

            // Zones : TP -> vert en évidence ; SL -> rouge ; ANNULATION 'X' -> les DEUX estompés
            // (ni SL ni TP atteint) ; flat/ouvert -> neutre.
            Brush bVert = t.SortieType switch { 'T' => _vFort, 'S' or 'X' => _vFaible, _ => _vNeutre };
            Brush bRouge = t.SortieType switch { 'S' => _rFort, 'T' or 'X' => _rFaible, _ => _rNeutre };
            RectVertical(gr, bVert, xL, w, yEntree, yTp);
            RectVertical(gr, bRouge, xL, w, yEntree, ySl);

            gr.DrawLine(t.SortieType == 'T' ? _lnTpFort : _lnTp, xL, yTp, xR, yTp);
            gr.DrawLine(t.SortieType == 'S' ? _lnSlFort : _lnSl, xL, ySl, xR, ySl);
            gr.DrawLine(_lnEntree, xL, yEntree, xR, yEntree);

            Triangle(gr, t.Sens > 0 ? _triLong : _triShort, xL, yEntree, t.Sens);

            if (t.SortieTemps is not null)
            {
                bool gain = t.Pts >= 0;
                float yNiv = (float)conv.GetChartY(t.SortieNiveau);
                // Ligne de sortie SIGNAL (annulation / flat) : au niveau de sortie, sur toute
                // la durée du trade, en AMBRE — distincte du SL/TP puisque le bracket n'a pas
                // été atteint mais annulé.
                if (t.SortieType is 'X' or 'F')
                    gr.DrawLine(_lnAnnul, xL, yNiv, xR, yNiv);

                if (t.SortieType == 'X')      // ANNULATION : losange sur la ligne de sortie
                    Losange(gr, gain ? _dotVert : _dotRouge, xR, yNiv, 6f);
                else                          // TP / SL / flat : cercle sur le niveau
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
                    // Ancrée sur la LIGNE DE SORTIE (yNiv) : TP -> à sa ligne, SL -> à la sienne,
                    // annulation -> à la ligne d'annulation. Gain au-dessus, perte en dessous.
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

        string l1 = "H3 SMA Annulation";
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
