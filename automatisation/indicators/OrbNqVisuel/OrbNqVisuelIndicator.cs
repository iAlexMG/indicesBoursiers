using System.Drawing;
using TradingPlatform.BusinessLayer;
using Hybrides;

namespace OrbNqVisuel;

/// <summary>
/// Hybride H1 ORB — le VISUEL sur le graphique (à poser sur un graphe **NQ 1 m**).
/// Rejoue la logique EXACTE de la stratégie `Hybride H1 ORB (NQ)` et du jumeau LEAN
/// `orb_nq.py` — mêmes classes d'indicateurs (Compile Include de hybrides/Indicateurs.cs,
/// une seule implémentation des formules) — et DESSINE :
///   - les BORNES de la plage d'ouverture (09:30 → 10:00 ET), tracées de 10:00 à 16:55 ;
///   - le marqueur d'ENTRÉE (flèche verte ↑ long / rouge ↓ short) à la première clôture
///     1 m au-delà d'une borne (10:00 → 12:00 ET, une par jour) ;
///   - les lignes SL (rouge pointillé) et TP (vert pointillé) du bracket pendant le trade ;
///   - le marqueur de SORTIE (rond : vert = TP, rouge = SL, orange = flat 16:55).
/// Décisions AUX CLÔTURES de barres seulement (jamais sur le tick en cours — parité avec
/// la stratégie et le jumeau). Nuance assumée : le fill est pris AU CLOSE du signal (la
/// stratégie, elle, se remplit au trade suivant) — l'écart est de l'ordre d'un tick.
/// Ce visuel n'émet RIEN : ni ordre, ni pop-up, ni journal — il montre.
/// </summary>
public sealed class OrbNqVisuelIndicator : Indicator
{
    [InputParameter("Plage d'ouverture (minutes)", 0, 15, 60, 15, 0)]
    public int PlageMin = 30;

    [InputParameter("Entrées à partir de (HH:mm ET)", 1)]
    public string EntreesDebutEt = "09:30";

    [InputParameter("Fin de la fenêtre d'entrée (HH:mm ET)", 2)]
    public string FenetreFinEt = "12:00";

    [InputParameter("Flat forcé à (HH:mm ET)", 3)]
    public string HeureFlatEt = "16:55";

    [InputParameter("Période ATR (barres 5 m)", 4, 2, 100, 1, 0)]
    public int AtrPeriode = 14;

    [InputParameter("Stop (× ATR)", 5, 0.5, 10, 0.5, 1)]
    public double StopMult = 1.5;

    [InputParameter("Take profit (× R)", 6, 0.5, 10, 0.5, 1)]
    public double TpR = 2.0;

    private const int TfAtr = 5;
    private const int LBorneHaute = 0, LBorneBasse = 1, LSl = 2, LTp = 3, LMarqueurs = 4;

    private AggregateurBarres _agg5 = null!;
    private AtrWilder _atr = null!;
    private int _debut, _finFenetre, _flat;

    private DateTime _jourEt = DateTime.MinValue;
    private double _borneHaute = double.NaN, _borneBasse = double.NaN;
    private bool _entreeFaite;
    private int _sens;                       // 0 flat, ±1 en position simulée
    private double _stop = double.NaN, _take = double.NaN;
    private DateTime _derniereBarreTraitee = DateTime.MinValue;

    public OrbNqVisuelIndicator()
    {
        Name = "Hybride H1 ORB (visuel)";
        Description = "Plage d'ouverture, cassure, bracket — le visuel de la stratégie H1 (graphe NQ 1 m)";
        SeparateWindow = false;
        AddLineSeries("Borne haute (plage ET)", Color.DodgerBlue, 2, LineStyle.Solid);
        AddLineSeries("Borne basse (plage ET)", Color.DodgerBlue, 2, LineStyle.Solid);
        AddLineSeries("SL (bracket)", Color.Red, 1, LineStyle.Dash);
        AddLineSeries("TP (bracket)", Color.LimeGreen, 1, LineStyle.Dash);
        AddLineSeries("signaux", Color.FromArgb(0, 0, 0, 0), 1, LineStyle.Solid); // porte les marqueurs
    }

    protected override void OnInit()
    {
        _agg5 = new AggregateurBarres(TfAtr);
        _atr = new AtrWilder(AtrPeriode);
        _debut = CadreSeance.ParseHeure(EntreesDebutEt);
        _finFenetre = CadreSeance.ParseHeure(FenetreFinEt);
        _flat = CadreSeance.ParseHeure(HeureFlatEt);
        _jourEt = DateTime.MinValue;
        _borneHaute = _borneBasse = _stop = _take = double.NaN;
        _entreeFaite = false;
        _sens = 0;
        _derniereBarreTraitee = DateTime.MinValue;
    }

    protected override void OnUpdate(UpdateArgs args)
    {
        // Décisions AUX CLÔTURES seulement. Pendant l'historique, la barre 0 est déjà
        // close ; en live, on traite la barre 1 quand une nouvelle barre 0 s'ouvre.
        if (args.Reason == UpdateReason.HistoricalBar)
            TraiterBarreClose(0);
        else if (Count > 1)
            TraiterBarreClose(1);

        // Et on prolonge les lignes sur la barre COURANTE (le tracé suit le temps réel).
        DessinerNiveaux(0);
    }

    private void TraiterBarreClose(int offset)
    {
        var ouverture = this.Time(offset);
        if (ouverture <= _derniereBarreTraitee) return;      // déjà traitée
        _derniereBarreTraitee = ouverture;

        double open = this.GetPrice(PriceType.Open, offset);
        double haut = this.GetPrice(PriceType.High, offset);
        double bas = this.GetPrice(PriceType.Low, offset);
        double close = this.GetPrice(PriceType.Close, offset);
        var finUtc = ouverture.AddMinutes(1);

        // ATR sur barres 5 m agrégées — la même règle que la stratégie et le jumeau.
        if (_agg5.Ajouter(new Barre1m(ouverture, open, haut, bas, close)) is { } b5)
            _atr.Ajouter(b5.H, b5.L, b5.C);

        var (jourEt, m) = CadreSeance.HeureEt(finUtc);
        if (jourEt != _jourEt)
        {
            _jourEt = jourEt;
            _borneHaute = _borneBasse = double.NaN;
            _entreeFaite = false;
        }

        // 1) EN POSITION : bracket simulé sur extrêmes intra-barre (SL prioritaire), flat.
        if (_sens != 0)
        {
            if ((_sens > 0 && bas <= _stop) || (_sens < 0 && haut >= _stop))
                Sortir(offset, Color.Red);
            else if ((_sens > 0 && haut >= _take) || (_sens < 0 && bas <= _take))
                Sortir(offset, Color.LimeGreen);
            else if (m >= _flat)
                Sortir(offset, Color.Orange);
        }

        // 2) Construction de la plage (barres closes entre l'ouverture et ouverture+30).
        if (m > _debut && m <= _debut + PlageMin)
        {
            _borneHaute = double.IsNaN(_borneHaute) ? haut : Math.Max(_borneHaute, haut);
            _borneBasse = double.IsNaN(_borneBasse) ? bas : Math.Min(_borneBasse, bas);
        }
        // 3) ENTRÉE : première clôture au-delà d'une borne, fenêtre 10:00 → 12:00.
        else if (!_entreeFaite && _sens == 0 && !double.IsNaN(_borneHaute) && _atr.Pret
                 && m > _debut + PlageMin && m <= _finFenetre
                 && (close > _borneHaute || close < _borneBasse))
        {
            _sens = close > _borneHaute ? 1 : -1;
            _entreeFaite = true;
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

    private void Sortir(int offset, Color couleur)
    {
        _sens = 0;
        _stop = _take = double.NaN;
        LinesSeries[LMarqueurs].SetMarker(offset, new IndicatorLineMarker
        {
            Color = couleur,
            UpperIcon = IndicatorLineMarkerIconType.FillCircle,
        });
    }

    /// <summary>Trace les niveaux du moment sur la barre `offset` : bornes de 10:00 à
    /// 16:55 ET, SL/TP tant que la position simulée vit. Pas de valeur = pas de trait
    /// (les lignes s'interrompent toutes seules).</summary>
    private void DessinerNiveaux(int offset)
    {
        var (_, m) = CadreSeance.HeureEt(this.Time(offset).AddMinutes(1));
        double close = this.GetPrice(PriceType.Close, offset);
        SetValue(close, LMarqueurs, offset);                 // ligne invisible des marqueurs
        if (!double.IsNaN(_borneHaute) && m > _debut + PlageMin && m <= _flat)
        {
            SetValue(_borneHaute, LBorneHaute, offset);
            SetValue(_borneBasse, LBorneBasse, offset);
        }
        if (_sens != 0)
        {
            SetValue(_stop, LSl, offset);
            SetValue(_take, LTp, offset);
        }
    }
}
