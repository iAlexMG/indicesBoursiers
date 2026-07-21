using TradingPlatform.BusinessLayer;

namespace Hybrides;

/// <summary>
/// H1 — Croisement SMA 9/21 (1 m) + bracket · mécanique : cassure de momentum · PROUVE :
/// le BRACKET (posé au fill, résolu par SL ou TP).
/// Refonte 2026-07-20 : déclencheur COMMUN aux 3 hybrides (croisement SMA 1 m) — H1, H2 et
/// H3 entrent sur le même signal, elles ne diffèrent que par la gestion d'ordre. H1 est la
/// plus simple : elle pose un bracket et le laisse se résoudre.
/// Jumeau LEAN : backtesting/backtests/algorithms/sma_bracket_nq.py.
///   - Signal : croisement SMA 9/21 sur closes 1 m (seedé par GetHistory).
///   - Entrée : croisement → market ×1 + bracket SL/TP ATTACHÉ (offsets en ticks du fill).
///   - SL = 1,5 × ATR14 (1 m, valeur au signal) | TP = 1R. Pas de stop suiveur.
///   - Sorties : TP, SL (plateforme), ou flat forcé (base). IGNORE les croisements suivants
///     tant qu'en position — c'est le bracket qui referme, et c'est ça qu'on prouve.
/// </summary>
public sealed class SmaBracketHybride : HybrideStrategyBase
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
    public double TpR = 1.0;

    private DeclencheurSmaCross _cross = null!;
    private AtrWilder _atr = null!;

    public SmaBracketHybride()
    {
        Name = "Hybride H1 SMA Bracket (NQ)";
        Description = "Croisement SMA 9/21 (1 m) + bracket SL/TP attaché — jumeau LEAN : sma_bracket_nq.py";
    }

    protected override string Slug => "sma_bracket_nq";

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
        if (_cross.Croisement == 0 || !_atr.Pret) return;

        Side sens = _cross.Croisement > 0 ? Side.Buy : Side.Sell;
        var indicateurs = new[]
        {
            ("sma_rapide", _cross.Rapide), ("sma_lente", _cross.Lente), ("atr", _atr.Valeur),
        };
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
