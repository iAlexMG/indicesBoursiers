using TradingPlatform.BusinessLayer;

namespace Hybrides;

/// <summary>
/// H2 — Croisement SMA 2/6 (1 m) + stop suiveur · mécanique : tendance · PROUVE : la
/// MODIFICATION d'ordre (le stop remonté plusieurs fois dans un même trade).
/// Refonte 2026-07-20 : même déclencheur COMMUN que H1/H3 (croisement SMA 1 m), porté du
/// 5 m au 1 m et sans filtre de régime. La différence de H2, c'est le stop suiveur.
/// Jumeau LEAN : backtesting/backtests/algorithms/sma_suiveur_nq.py.
///   - Signal : croisement SMA 2/6 sur closes 1 m (seedé). Croisement → market ×1 +
///     SL attaché initial à 2 × ATR14 (1 m). PAS de TP.
///   - Suiveur : à CHAQUE barre 1 m, ModifyOrder du stop vers
///     max(stop courant, extrême favorable depuis l'entrée ∓ 2×ATR) — ne recule JAMAIS.
///   - Sorties : stop (plateforme), croisement inverse (annulation + market), flat.
///   - Perte pleine (garde-fou) : stop touché DU CÔTÉ PERDANT seulement.
/// </summary>
public sealed class SmaSuiveurHybride : HybrideStrategyBase
{
    [InputParameter("SMA rapide (barres 1 m)", 20, 2, 100, 1, 0)]
    public int SmaRapide = 2;

    [InputParameter("SMA lente (barres 1 m)", 21, 3, 200, 1, 0)]
    public int SmaLente = 6;

    [InputParameter("Période ATR (barres 1 m)", 22, 2, 100, 1, 0)]
    public int AtrPeriode = 7;

    [InputParameter("Stop / suiveur (× ATR)", 23, 0.5, 10, 0.5, 1)]
    public double StopMult = 2.0;

    private DeclencheurSmaCross _cross = null!;
    private AtrWilder _atr = null!;
    private double _extreme = double.NaN;      // extrême favorable depuis l'entrée (suivi 1 m)
    private double _stopVoulu = double.NaN;    // dernier niveau de stop que NOUS avons visé

    public SmaSuiveurHybride()
    {
        Name = "Hybride H2 SMA Suiveur (NQ)";
        Description = "Croisement SMA 2/6 (1 m) + stop suiveur par modification d'ordre — jumeau LEAN : sma_suiveur_nq.py";
    }

    protected override string Slug => "sma_suiveur_nq";

    protected override void OnRun()
    {
        _cross = new DeclencheurSmaCross(SmaRapide, SmaLente);
        _atr = new AtrWilder(AtrPeriode);
        base.OnRun();
    }

    protected override void SurBarre1m(in Barre1m b)
    {
        _atr.Ajouter(b.High, b.Low, b.Close);
        _cross.Ajouter(b.Close);

        // 1) EN POSITION : extrême favorable + stop suiveur, à CHAQUE barre 1 m.
        if (EnPosition && !SortieEnCours && !double.IsNaN(_extreme))
        {
            _extreme = SensPosition == Side.Buy ? Math.Max(_extreme, b.High)
                                                : Math.Min(_extreme, b.Low);
            // a) Croisement inverse -> sortie signal (annulation du stop + market).
            if ((SensPosition == Side.Buy && _cross.Croisement < 0)
                || (SensPosition == Side.Sell && _cross.Croisement > 0))
            {
                Journal("signal", prix: b.Close, raison: "croisement inverse -> sortie",
                        indicateurs: Indic(b));
                EnvoyerSortieSignal(b.FinUtc, "croisement inverse");
                return;
            }
            // b) Sinon, stop suiveur : extrême favorable ∓ 2×ATR, arrondi au tick, jamais en recul.
            if (_atr.Pret)
            {
                double tick = Instrument!.TickSize;
                double candidat = SensPosition == Side.Buy
                    ? Math.Round((_extreme - StopMult * _atr.Valeur) / tick) * tick
                    : Math.Round((_extreme + StopMult * _atr.Valeur) / tick) * tick;
                double reference = double.IsNaN(_stopVoulu) ? StopCourantPlateforme : _stopVoulu;
                bool ameliore = double.IsNaN(reference)
                    || (SensPosition == Side.Buy ? candidat > reference : candidat < reference);
                if (ameliore && ModifierStop(b.FinUtc, candidat,
                        new[] { ("extreme", _extreme), ("atr", _atr.Valeur) }))
                    _stopVoulu = candidat;
            }
            return;
        }

        // 2) ENTRÉE sur croisement (déclencheur commun).
        if (_cross.Croisement == 0 || !_atr.Pret) return;
        Side sens = _cross.Croisement > 0 ? Side.Buy : Side.Sell;
        string sensTxt = sens == Side.Buy ? "haussier -> long" : "baissier -> short";
        var refus = RaisonRefus(b.FinUtc);
        if (refus is "seed" or "en_position") return;
        if (refus is not null)
        {
            Journal("signal", prix: b.Close, raison: $"croisement {sensTxt} REFUSÉ : {refus}",
                    indicateurs: Indic(b));
            return;
        }
        Journal("signal", prix: b.Close, raison: $"croisement {sensTxt}", indicateurs: Indic(b));
        _stopVoulu = double.NaN;    // le vrai niveau se fixe au fill (offset en ticks)
        EnvoyerEntree(b.FinUtc, sens, b.Close, EnTicks(StopMult * _atr.Valeur), 0, Indic(b));
    }

    private (string, double)[] Indic(in Barre1m b) =>
        new[] { ("sma_rapide", _cross.Rapide), ("sma_lente", _cross.Lente), ("atr", _atr.Valeur) };

    protected override void SurPositionOuverte(double prixEntree)
    {
        _extreme = prixEntree;      // l'extrême favorable démarre au fill
        _stopVoulu = double.NaN;
    }

    protected override void SurPositionFermee(string raison)
    {
        _extreme = _stopVoulu = double.NaN;
        StopCourantPlateforme = double.NaN;
    }

    /// <summary>Stop pris DU CÔTÉ PERDANT = perte pleine ; un suiveur pris en gain, non.</summary>
    protected override bool EstPertePleine(string raison, double prixSortie) =>
        raison == "SL" && !double.IsNaN(prixSortie) && !double.IsNaN(PrixEntree)
        && (SensPosition == Side.Buy ? prixSortie < PrixEntree : prixSortie > PrixEntree);
}
