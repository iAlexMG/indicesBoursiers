using System.Drawing;
using TradingPlatform.BusinessLayer;
using Hybrides;

namespace SmaBracketVisuel;

/// <summary>
/// Hybride H1 SMA Bracket — le VISUEL sur le graphique (graphe NQ 1 m). Rejoue la logique
/// EXACTE de la stratégie `Hybride H1 SMA Bracket (NQ)` et du jumeau `sma_bracket_nq.py`
/// (mêmes classes d'indicateurs — Compile Include de hybrides/Indicateurs.cs).
///
/// Rendu (2026-07-22, refonte du rendu) :
///   - deux SMA du déclencheur (rapide 9 = bleue, lente 21 = orange) — en line series ;
///   - pour CHAQUE trade, la ZONE peinte en `OnPaintChart` : encadré **vert** (entrée → TP,
///     le côté gagnant) et **rouge** (entrée → SL, le côté perdant), du bord de l'entrée au
///     bord de la sortie ; flèche d'entrée ; et **point de sortie posé EXACTEMENT sur le
///     niveau touché** (haut du vert = TP, bas du rouge = SL). Plus de line series SL/TP
///     (qui traçaient une oblique parasite et posaient le point au mauvais prix).
/// Décisions AUX CLÔTURES de barres. N'émet rien (ni ordre, ni pop-up, ni journal).
/// </summary>
public sealed class SmaBracketVisuelIndicator : Indicator
{
    [InputParameter("SMA rapide (1 m)", 0, 2, 100, 1, 0)]
    public int SmaRapide = 9;

    [InputParameter("SMA lente (1 m)", 1, 3, 200, 1, 0)]
    public int SmaLente = 21;

    [InputParameter("Période ATR (1 m)", 2, 2, 100, 1, 0)]
    public int AtrPeriode = 14;

    [InputParameter("Stop (× ATR)", 3, 0.5, 10, 0.5, 1)]
    public double StopMult = 1.5;

    [InputParameter("Take profit (× R)", 4, 0.5, 10, 0.5, 1)]
    public double TpR = 1.0;

    [InputParameter("Entrées à partir de (HH:mm ET)", 5)]
    public string EntreesDebutEt = "09:30";

    [InputParameter("Entrées jusqu'à (HH:mm ET)", 6)]
    public string EntreesFinEt = "15:30";

    [InputParameter("Flat forcé à (HH:mm ET)", 7)]
    public string HeureFlatEt = "16:55";

    [InputParameter("Cooldown après sortie (minutes)", 8, 0, 120, 1, 0)]
    public int CooldownMin = 2;

    [InputParameter("Restreindre à la séance NY (décoché = 24 h)", 9)]
    public bool SeanceNY = false;

    private const int LRapide = 0, LLente = 1;   // seules les 2 SMA restent en line series
    private const int MaxTrades = 1000;          // borne mémoire (les vieux trades tombent)

    private DeclencheurSmaCross _cross = null!;
    private AtrWilder _atr = null!;
    private int _debut, _fin, _flat;

    /// <summary>Un trade dessiné : entrée, niveaux, et sortie (temps + niveau touché).</summary>
    private sealed class Trade
    {
        public DateTime EntreeTemps;
        public double EntreePrix, Sl, Tp;
        public int Sens;                          // +1 long, -1 short
        public DateTime? SortieTemps;
        public double SortieNiveau;               // le prix EXACT où le trade s'est fermé
        public char SortieType;                   // 'T' TP, 'S' SL, 'F' flat
    }

    private readonly object _lock = new();
    private readonly List<Trade> _trades = new();
    private Trade? _courant;
    private DateTime _derniereBarreTraitee = DateTime.MinValue;
    private DateTime _dernierTempsBarre = DateTime.MinValue;
    private DateTime _sortieUtc = DateTime.MinValue;

    // GDI+ (créés une fois, réutilisés — patron VpSessionNq).
    private readonly Brush _fillVert = new SolidBrush(Color.FromArgb(38, Color.LimeGreen));
    private readonly Brush _fillRouge = new SolidBrush(Color.FromArgb(38, Color.OrangeRed));
    private readonly Pen _penVert = new(Color.FromArgb(140, Color.LimeGreen), 1f);
    private readonly Pen _penRouge = new(Color.FromArgb(140, Color.OrangeRed), 1f);
    private readonly Brush _dotVert = new SolidBrush(Color.LimeGreen);
    private readonly Brush _dotRouge = new SolidBrush(Color.Red);
    private readonly Brush _dotOrange = new SolidBrush(Color.Orange);
    private readonly Brush _triLong = new SolidBrush(Color.LimeGreen);
    private readonly Brush _triShort = new SolidBrush(Color.Red);

    public SmaBracketVisuelIndicator()
    {
        Name = "Hybride H1 SMA Bracket (visuel)";
        Description = "Croisement SMA 9/21 (1 m) + zone de trade (bracket) — visuel de la stratégie H1 (graphe NQ 1 m)";
        SeparateWindow = false;
        AddLineSeries("SMA rapide (9)", Color.DodgerBlue, 2, LineStyle.Solid);
        AddLineSeries("SMA lente (21)", Color.Orange, 2, LineStyle.Solid);
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

        // Prolonge les SMA sur la barre courante (sinon les courbes s'arrêtent une barre avant).
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
            _courant.SortieTemps = temps;
            _courant.SortieNiveau = niveau;
            _courant.SortieType = type;
            _courant = null;
        }
        _sortieUtc = temps.AddMinutes(1);
    }

    private bool CooldownOk(DateTime finUtc) =>
        _sortieUtc == DateTime.MinValue || (finUtc - _sortieUtc).TotalMinutes >= CooldownMin;

    // ─────────────────────────────────────────────────────── LE RENDU DE LA ZONE ──────
    public override void OnPaintChart(PaintChartEventArgs args)
    {
        var conv = this.CurrentChart?.MainWindow?.CoordinatesConverter;
        if (conv is null) return;
        Trade[] trades;
        DateTime finOuverte;
        lock (_lock) { trades = _trades.ToArray(); finOuverte = _dernierTempsBarre; }

        var gr = args.Graphics;
        var rect = args.Rectangle;
        foreach (var t in trades)
        {
            var borneDroite = t.SortieTemps ?? finOuverte;      // trade ouvert : jusqu'à la barre courante
            float xL = (float)conv.GetChartX(t.EntreeTemps);
            float xR = (float)conv.GetChartX(borneDroite);
            if (xR < xL) xR = xL;
            if (xR < rect.Left - 4 || xL > rect.Right + 4) continue;   // hors écran : rien à peindre
            float w = Math.Max(2f, xR - xL);

            float yEntree = (float)conv.GetChartY(t.EntreePrix);
            float ySl = (float)conv.GetChartY(t.Sl);
            float yTp = (float)conv.GetChartY(t.Tp);

            // Encadré VERT (entrée → TP, côté gagnant) et ROUGE (entrée → SL, côté perdant).
            RectVertical(gr, _fillVert, _penVert, xL, w, yEntree, yTp);
            RectVertical(gr, _fillRouge, _penRouge, xL, w, yEntree, ySl);

            // Flèche d'entrée (triangle plein dans le sens du trade), au prix d'entrée.
            Triangle(gr, t.Sens > 0 ? _triLong : _triShort, xL, yEntree, t.Sens);

            // Point de sortie, EXACTEMENT sur le niveau touché.
            if (t.SortieTemps is not null)
            {
                var brush = t.SortieType switch { 'T' => _dotVert, 'S' => _dotRouge, _ => _dotOrange };
                float yNiv = (float)conv.GetChartY(t.SortieNiveau);
                gr.FillEllipse(brush, xR - 4f, yNiv - 4f, 8f, 8f);
            }
        }
    }

    private static void RectVertical(Graphics gr, Brush fill, Pen pen, float x, float w, float yA, float yB)
    {
        float top = Math.Min(yA, yB), h = Math.Max(1f, Math.Abs(yB - yA));
        gr.FillRectangle(fill, x, top, w, h);
        gr.DrawRectangle(pen, x, top, w, h);
    }

    private static void Triangle(Graphics gr, Brush brush, float x, float y, int sens)
    {
        // Long : pointe vers le haut, sous le prix d'entrée ; short : pointe vers le bas, au-dessus.
        float d = sens > 0 ? 1f : -1f;   // d>0 : le triangle descend sous l'entrée (base en bas)
        var pts = new[]
        {
            new PointF(x - 5f, y + 9f * d),
            new PointF(x + 5f, y + 9f * d),
            new PointF(x, y + 2f * d),
        };
        gr.FillPolygon(brush, pts);
    }
}
