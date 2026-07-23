using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using TradingPlatform.BusinessLayer;
using Hybrides;

namespace SmaSuiveurVisuel;

/// <summary>
/// Hybride H2 SMA Suiveur — le VISUEL sur le graphique (graphe NQ 1 m). Rejoue la logique
/// EXACTE de la stratégie `Hybride H2 SMA Suiveur (NQ)` et du jumeau `sma_suiveur_nq.py`
/// (mêmes classes d'indicateurs — Compile Include de hybrides/Indicateurs.cs).
///
/// Ce que H2 prouve = la MODIFICATION : le stop suiveur qui remonte marche par marche. Rendu :
///   - deux SMA (rapide 2 = bleue, lente 6 = orange) en line series ;
///   - le **stop suiveur en ESCALIER** (ligne ambre, une marche par barre) — le cœur du visuel ;
///   - la **bande entre l'entrée et le stop** qui vire du **rouge** (encore à risque, stop sous
///     l'entrée) au **vert** (profit verrouillé, stop passé de l'autre côté) ;
///   - flèche d'entrée, **point de sortie sur le niveau de sortie** (coloré selon le gain/perte),
///     étiquette de résultat (points + R), et un panneau de résultats (haut-droite).
/// Pas de TP (H2 n'en a pas). Sorties : stop, croisement inverse, flat (si séance NY).
/// Décisions AUX CLÔTURES de barres. N'émet rien.
/// </summary>
public sealed class SmaSuiveurVisuelIndicator : Indicator
{
    [InputParameter("SMA rapide (1 m)", 0, 2, 100, 1, 0)]
    public int SmaRapide = 2;

    [InputParameter("SMA lente (1 m)", 1, 3, 200, 1, 0)]
    public int SmaLente = 6;

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
    private double _tick = 0.25;

    private sealed class Trade
    {
        public DateTime EntreeTemps;
        public double EntreePrix, StopInitial, Stop, Extreme;
        public int Sens;                          // +1 long, -1 short
        public readonly List<(DateTime t, double stop)> Trail = new();  // le stop à chaque barre
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

    // GDI+ (créés une fois).
    private readonly Brush _fillVert = new SolidBrush(Color.FromArgb(40, Color.LimeGreen));
    private readonly Brush _fillRouge = new SolidBrush(Color.FromArgb(40, Color.OrangeRed));
    private readonly Pen _stopPen = new(Color.FromArgb(235, 255, 190, 70), 2.2f);      // escalier ambre
    private readonly Pen _lnEntree = new(Color.FromArgb(120, Color.Gainsboro), 1f) { DashStyle = DashStyle.Dot };
    private readonly Brush _dotVert = new SolidBrush(Color.LimeGreen);
    private readonly Brush _dotRouge = new SolidBrush(Color.Red);
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

    public SmaSuiveurVisuelIndicator()
    {
        Name = "Hybride H2 SMA Suiveur (visuel)";
        Description = "Croisement SMA 2/6 (1 m) + stop suiveur en escalier — visuel de la stratégie H2 (graphe NQ 1 m)";
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
        _tick = this.Symbol?.TickSize is { } ts && ts > 0 ? ts : 0.25;
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

        // 1) EN POSITION : stop (prioritaire), flat, croisement inverse, sinon suiveur.
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
                lock (_lock) tr.Trail.Add((ouverture, tr.Stop));   // une marche par barre
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
                EntreeTemps = ouverture,
                EntreePrix = close,
                Sens = cr,
                StopInitial = stopInit,
                Stop = stopInit,
                Extreme = close,
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
            double risque = Math.Abs(t.EntreePrix - t.StopInitial);   // 1R = risque initial
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
            float yEntree = (float)conv.GetChartY(t.EntreePrix);

            (DateTime t, double stop)[] trail;
            lock (_lock) trail = t.Trail.ToArray();

            // Ligne d'entrée (référence) + escalier du stop avec bande risque(rouge)/profit(vert).
            gr.DrawLine(_lnEntree, xL, yEntree, xR, yEntree);
            for (int i = 0; i < trail.Length; i++)
            {
                float xi = (float)conv.GetChartX(trail[i].t);
                float xn = i + 1 < trail.Length ? (float)conv.GetChartX(trail[i + 1].t) : xR;
                if (xn < xi) xn = xi;
                float yi = (float)conv.GetChartY(trail[i].stop);
                bool profit = t.Sens > 0 ? trail[i].stop > t.EntreePrix : trail[i].stop < t.EntreePrix;
                RectVertical(gr, profit ? _fillVert : _fillRouge, xi, xn - xi, yi, yEntree);
                gr.DrawLine(_stopPen, xi, yi, xn, yi);                       // marche horizontale
                if (i + 1 < trail.Length)                                    // contremarche verticale
                {
                    float yn = (float)conv.GetChartY(trail[i + 1].stop);
                    gr.DrawLine(_stopPen, xn, yi, xn, yn);
                }
            }

            Triangle(gr, t.Sens > 0 ? _triLong : _triShort, xL, yEntree, t.Sens);

            if (t.SortieTemps is not null)
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

        string l1 = "H2 SMA Suiveur";
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
