using System.Drawing;
using TradingPlatform.BusinessLayer;
using Hybrides;

namespace SmaBracketVisuel;

/// <summary>
/// Hybride H1 SMA Bracket — le VISUEL sur le graphique (graphe NQ 1 m). Rejoue la logique
/// EXACTE de la stratégie `Hybride H1 SMA Bracket (NQ)` et du jumeau `sma_bracket_nq.py`
/// (mêmes classes d'indicateurs — Compile Include de hybrides/Indicateurs.cs) et DESSINE :
///   - les deux SMA du déclencheur commun (rapide 9 = bleue, lente 21 = orange) ;
///   - le marqueur d'ENTRÉE (flèche verte ↑ long / rouge ↓ short) au croisement ;
///   - les lignes SL (rouge pointillé) / TP (vert pointillé) du bracket pendant le trade ;
///   - le marqueur de SORTIE (rond : vert = TP, rouge = SL, orange = flat 16:55).
/// H1 IGNORE les croisements suivants tant qu'en position (le bracket referme seul).
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

    // Décoché (défaut) = 24 h : cassures dessinées quand le marché est ouvert (aussi le
    // soir), sans fenêtre ni flat de séance — pour observer le graphe à toute heure.
    [InputParameter("Restreindre à la séance NY (décoché = 24 h)", 9)]
    public bool SeanceNY = false;

    private const int LRapide = 0, LLente = 1, LSl = 2, LTp = 3, LMarqueurs = 4;

    private DeclencheurSmaCross _cross = null!;
    private AtrWilder _atr = null!;
    private int _debut, _fin, _flat;

    private int _sens;                       // 0 flat, ±1 en position simulée
    private double _stop = double.NaN, _take = double.NaN;
    private DateTime _derniereBarreTraitee = DateTime.MinValue;
    private DateTime _sortieUtc = DateTime.MinValue;

    public SmaBracketVisuelIndicator()
    {
        Name = "Hybride H1 SMA Bracket (visuel)";
        Description = "Croisement SMA 9/21 (1 m) + bracket — le visuel de la stratégie H1 (graphe NQ 1 m)";
        SeparateWindow = false;
        AddLineSeries("SMA rapide (9)", Color.DodgerBlue, 2, LineStyle.Solid);
        AddLineSeries("SMA lente (21)", Color.Orange, 2, LineStyle.Solid);
        AddLineSeries("SL (bracket)", Color.Red, 1, LineStyle.Dash);
        AddLineSeries("TP (bracket)", Color.LimeGreen, 1, LineStyle.Dash);
        AddLineSeries("signaux", Color.FromArgb(0, 0, 0, 0), 1, LineStyle.Solid); // porte les marqueurs
    }

    protected override void OnInit()
    {
        _cross = new DeclencheurSmaCross(SmaRapide, SmaLente);
        _atr = new AtrWilder(AtrPeriode);
        _debut = CadreSeance.ParseHeure(EntreesDebutEt);
        _fin = CadreSeance.ParseHeure(EntreesFinEt);
        _flat = CadreSeance.ParseHeure(HeureFlatEt);
        _sens = 0;
        _stop = _take = double.NaN;
        _derniereBarreTraitee = DateTime.MinValue;
        _sortieUtc = DateTime.MinValue;
    }

    protected override void OnUpdate(UpdateArgs args)
    {
        if (args.Reason == UpdateReason.HistoricalBar)
            TraiterBarreClose(0);
        else if (Count > 1)
            TraiterBarreClose(1);
        DessinerCourant(0);
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
        if (_sens != 0)
        {
            if ((_sens > 0 && bas <= _stop) || (_sens < 0 && haut >= _stop))
                Sortir(offset, finUtc, Color.Red);
            else if ((_sens > 0 && haut >= _take) || (_sens < 0 && bas <= _take))
                Sortir(offset, finUtc, Color.LimeGreen);
            else if (SeanceNY && m >= _flat)
                Sortir(offset, finUtc, Color.Orange);
        }
        // 2) ENTRÉE sur croisement (flat + fenêtre[si séance NY] + cooldown + ATR prêt).
        else if (_cross.Croisement != 0 && _atr.Pret && CooldownOk(finUtc)
                 && (!SeanceNY || (m > _debut && m <= _fin)))
        {
            _sens = _cross.Croisement;
            double r = StopMult * _atr.Valeur;
            _stop = _sens > 0 ? close - r : close + r;
            _take = _sens > 0 ? close + TpR * r : close - TpR * r;
            LinesSeries[LMarqueurs].SetMarker(offset, new IndicatorLineMarker
            {
                Color = _sens > 0 ? Color.LimeGreen : Color.Red,
                UpperIcon = _sens > 0 ? IndicatorLineMarkerIconType.None : IndicatorLineMarkerIconType.DownArrow,
                BottomIcon = _sens > 0 ? IndicatorLineMarkerIconType.UpArrow : IndicatorLineMarkerIconType.None,
            });
        }

        DessinerNiveaux(offset);
    }

    private bool CooldownOk(DateTime finUtc) =>
        _sortieUtc == DateTime.MinValue || (finUtc - _sortieUtc).TotalMinutes >= CooldownMin;

    private void Sortir(int offset, DateTime finUtc, Color couleur)
    {
        _sens = 0;
        _stop = _take = double.NaN;
        _sortieUtc = finUtc;
        LinesSeries[LMarqueurs].SetMarker(offset, new IndicatorLineMarker
        {
            Color = couleur,
            UpperIcon = IndicatorLineMarkerIconType.FillCircle,
        });
    }

    /// <summary>SL/TP tracés tant que la position simulée vit (sur la barre `offset`).</summary>
    private void DessinerNiveaux(int offset)
    {
        SetValue(this.GetPrice(PriceType.Close, offset), LMarqueurs, offset);
        if (_sens != 0)
        {
            SetValue(_stop, LSl, offset);
            SetValue(_take, LTp, offset);
        }
    }

    /// <summary>Prolonge les SMA (et les niveaux) sur la barre COURANTE en formation, pour
    /// que les courbes ne s'arrêtent pas une barre avant le bord droit.</summary>
    private void DessinerCourant(int offset)
    {
        if (_cross.Pret)
        {
            SetValue(_cross.Rapide, LRapide, offset);
            SetValue(_cross.Lente, LLente, offset);
        }
        if (_sens != 0)
        {
            SetValue(_stop, LSl, offset);
            SetValue(_take, LTp, offset);
        }
    }
}
