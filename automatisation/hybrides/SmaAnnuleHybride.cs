using TradingPlatform.BusinessLayer;

namespace Hybrides;

/// <summary>
/// H3 — Croisement SMA 9/21 (1 m) + bracket + annulation · mécanique : sortie sur signal ·
/// PROUVE : l'ANNULATION du bracket (fermeture au croisement inverse, AVANT SL/TP).
/// Refonte 2026-07-20 : même déclencheur COMMUN que H1/H2 (croisement SMA 1 m). H3 = H1
/// (bracket) MAIS elle sort aussi au croisement inverse en annulant le bracket encore
/// ouvert — c'est la seule différence, et c'est ce mécanisme qu'elle prouve.
/// Jumeau LEAN : backtesting/backtests/algorithms/sma_annule_nq.py.
///   - Signal : croisement SMA 9/21 sur closes 1 m (seedé). Croisement → market ×1 +
///     bracket SL/TP complet.
///   - SL = 1,5 × ATR14 (1 m) | TP = 2R (large : le croisement inverse tombe souvent avant,
///     ce qui exerce l'annulation).
///   - Sortie anticipée : croisement inverse → ANNULATION du bracket + market.
///   - Sorties : TP, SL (plateforme), croisement inverse, flat.
/// </summary>
public sealed class SmaAnnuleHybride : HybrideStrategyBase
{
    [InputParameter("SMA rapide (barres 1 m)", 20, 2, 100, 1, 0)]
    public int SmaRapide = 9;

    [InputParameter("SMA lente (barres 1 m)", 21, 3, 200, 1, 0)]
    public int SmaLente = 21;

    [InputParameter("Période ATR (barres 1 m)", 22, 2, 100, 1, 0)]
    public int AtrPeriode = 14;

    [InputParameter("Stop (× ATR)", 23, 0.5, 10, 0.5, 1)]
    public double StopMult = 1.5;

    [InputParameter("Take profit (× R)", 24, 0.5, 10, 0.5, 1)]
    public double TpR = 2.0;

    private DeclencheurSmaCross _cross = null!;
    private AtrWilder _atr = null!;

    public SmaAnnuleHybride()
    {
        Name = "Hybride H3 SMA Annulation (NQ)";
        Description = "Croisement SMA 9/21 (1 m), bracket + annulation au croisement inverse — jumeau LEAN : sma_annule_nq.py";
    }

    protected override string Slug => "sma_annule_nq";

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
        var indicateurs = new[]
        {
            ("sma_rapide", _cross.Rapide), ("sma_lente", _cross.Lente), ("atr", _atr.Valeur),
        };

        // 1) EN POSITION : sortie anticipée au croisement inverse (annulation du bracket).
        if (EnPosition && !SortieEnCours)
        {
            if ((SensPosition == Side.Buy && _cross.Croisement < 0)
                || (SensPosition == Side.Sell && _cross.Croisement > 0))
            {
                Journal("signal", prix: b.Close, raison: "croisement inverse -> sortie anticipée",
                        indicateurs: indicateurs);
                EnvoyerSortieSignal(b.FinUtc, "croisement inverse");
            }
            return;    // sinon on laisse le bracket (SL/TP) travailler
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
                    indicateurs: indicateurs);
            return;
        }
        double slPoints = StopMult * _atr.Valeur;
        Journal("signal", prix: b.Close, raison: $"croisement {sensTxt}", indicateurs: indicateurs);
        EnvoyerEntree(b.FinUtc, sens, b.Close, EnTicks(slPoints), EnTicks(TpR * slPoints), indicateurs);
    }
}
