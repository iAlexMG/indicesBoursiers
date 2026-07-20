using TradingPlatform.BusinessLayer;

namespace Hybrides;

/// <summary>
/// H2 — Croisement SMA 9/21 (5 m) + stop suiveur · mécanique : tendance · PROUVE : la
/// MODIFICATION d'ordre (le stop remonté plusieurs fois dans un même trade).
/// Jumeau LEAN : backtesting/backtests/algorithms/sma_suiveur_nq.py.
///   - Signal : SMA 9/21 sur closes 5 m, SANS filtre de régime. Croisement → market ×1 +
///     SL attaché initial à 2 × ATR14 (5 m). PAS de TP.
///   - Suiveur : à chaque clôture 5 m, ModifyOrder du stop vers
///     max(stop courant, extrême favorable depuis l'entrée ∓ 2×ATR) — ne recule JAMAIS.
///     L'extrême favorable est suivi sur les extrêmes des barres 1 m (au plus près du tick).
///   - Sorties : stop (plateforme), croisement inverse (annulation + market), flat forcé.
///   - Perte pleine (garde-fou) : stop touché DU CÔTÉ PERDANT seulement — un suiveur pris
///     en gain n'est pas une perte pleine.
/// </summary>
public sealed class SmaSuiveurHybride : HybrideStrategyBase
{
    [InputParameter("SMA rapide (barres 5 m)", 20, 2, 100, 1, 0)]
    public int SmaRapide = 9;

    [InputParameter("SMA lente (barres 5 m)", 21, 3, 200, 1, 0)]
    public int SmaLente = 21;

    [InputParameter("Période ATR (barres 5 m)", 22, 2, 100, 1, 0)]
    public int AtrPeriode = 14;

    [InputParameter("Stop / suiveur (× ATR)", 23, 0.5, 10, 0.5, 1)]
    public double StopMult = 2.0;

    private const int TfSignal = 5;

    private AggregateurBarres _agg5 = null!;
    private Sma _rapide = null!, _lente = null!;
    private AtrWilder _atr = null!;
    private double _diffPrec = double.NaN;
    private double _extreme = double.NaN;      // extrême favorable depuis l'entrée (suivi 1 m)
    private double _stopVoulu = double.NaN;    // dernier niveau de stop que NOUS avons posé/visé

    public SmaSuiveurHybride()
    {
        Name = "Hybride H2 SMA Suiveur (NQ)";
        Description = "Croisement SMA 9/21 en 5 m + stop suiveur par modification d'ordre — jumeau LEAN : sma_suiveur_nq.py";
    }

    protected override string Slug => "sma_suiveur_nq";

    protected override void OnRun()
    {
        _agg5 = new AggregateurBarres(TfSignal);
        _rapide = new Sma(SmaRapide);
        _lente = new Sma(SmaLente);
        _atr = new AtrWilder(AtrPeriode);
        base.OnRun();
    }

    protected override void SurBarre1m(in Barre1m b)
    {
        // Extrême favorable depuis l'entrée, à CHAQUE barre 1 m (le suiveur s'en nourrit).
        if (EnPosition && !SortieEnCours && !double.IsNaN(_extreme))
            _extreme = SensPosition == Side.Buy ? Math.Max(_extreme, b.High)
                                                : Math.Min(_extreme, b.Low);

        // SIGNAL aux bornes de 5 min (même règle d'agrégation que le jumeau).
        if (_agg5.Ajouter(b) is not { } b5) return;
        _atr.Ajouter(b5.H, b5.L, b5.C);
        _rapide.Ajouter(b5.C);
        _lente.Ajouter(b5.C);
        if (!(_rapide.Prete && _lente.Prete && _atr.Pret)) return;

        double diff = _rapide.Valeur - _lente.Valeur;
        bool croiseHaut = !double.IsNaN(_diffPrec) && _diffPrec <= 0 && diff > 0;
        bool croiseBas = !double.IsNaN(_diffPrec) && _diffPrec >= 0 && diff < 0;
        _diffPrec = diff;

        var indicateurs = new[]
        {
            ("sma_rapide", _rapide.Valeur), ("sma_lente", _lente.Valeur), ("atr", _atr.Valeur),
        };

        if (EnPosition && !SortieEnCours)
        {
            // a) Croisement inverse -> sortie signal (annulation du stop + market de sortie).
            if ((SensPosition == Side.Buy && croiseBas) || (SensPosition == Side.Sell && croiseHaut))
            {
                Journal("signal", prix: b5.C, raison: "croisement inverse -> sortie", indicateurs: indicateurs);
                EnvoyerSortieSignal(b5.FinUtc, "croisement inverse");
                return;
            }
            // b) Stop suiveur : extrême favorable ∓ 2×ATR, arrondi au tick, ne recule jamais.
            if (double.IsNaN(_extreme)) return;
            double tick = Instrument!.TickSize;
            double candidat = SensPosition == Side.Buy
                ? Math.Round((_extreme - StopMult * _atr.Valeur) / tick) * tick
                : Math.Round((_extreme + StopMult * _atr.Valeur) / tick) * tick;
            double reference = double.IsNaN(_stopVoulu) ? StopCourantPlateforme : _stopVoulu;
            bool ameliore = double.IsNaN(reference)
                || (SensPosition == Side.Buy ? candidat > reference : candidat < reference);
            if (ameliore && ModifierStop(b5.FinUtc, candidat,
                    new[] { ("extreme", _extreme), ("atr", _atr.Valeur) }))
                _stopVoulu = candidat;
            return;
        }

        // c) ENTRÉE sur croisement (les refus se journalisent, comme le jumeau).
        if (!croiseHaut && !croiseBas) return;
        var refus = RaisonRefus(b5.FinUtc);
        if (refus == "seed") return;
        string sensTxt = croiseHaut ? "haussier" : "baissier";
        if (refus is not null)
        {
            Journal("signal", prix: b5.C, raison: $"croisement {sensTxt} REFUSÉ : {refus}",
                    indicateurs: indicateurs);
            return;
        }
        Journal("signal", prix: b5.C, raison: $"croisement {sensTxt} -> entrée", indicateurs: indicateurs);
        int slTicks = EnTicks(StopMult * _atr.Valeur);
        _stopVoulu = double.NaN;    // le vrai niveau se fixera au fill (offset en ticks)
        EnvoyerEntree(b5.FinUtc, croiseHaut ? Side.Buy : Side.Sell, b5.C, slTicks, 0, indicateurs);
    }

    protected override void SurPositionOuverte(Position p)
    {
        _extreme = p.OpenPrice;      // l'extrême favorable démarre au fill, comme le jumeau
        _stopVoulu = double.NaN;     // référence = stop réellement posé (StopCourantPlateforme)
    }

    protected override void SurPositionFermee(string raison)
    {
        _extreme = _stopVoulu = double.NaN;
        StopCourantPlateforme = double.NaN;
    }

    /// <summary>Un stop suiveur pris AU-DESSUS de l'entrée (long) est un gain : pas une
    /// perte pleine. Le garde-fou ne compte que les stops du côté perdant.</summary>
    protected override bool EstPertePleine(string raison, double prixSortie) =>
        raison == "SL" && !double.IsNaN(prixSortie) && !double.IsNaN(PrixEntree)
        && (SensPosition == Side.Buy ? prixSortie < PrixEntree : prixSortie > PrixEntree);
}
