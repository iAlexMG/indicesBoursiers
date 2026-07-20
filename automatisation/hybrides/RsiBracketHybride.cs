using TradingPlatform.BusinessLayer;

namespace Hybrides;

/// <summary>
/// H3 — RSI 9 retour à la moyenne + bracket · mécanique : contre-tendance · PROUVE :
/// l'ANNULATION du bracket sur sortie signal (RSI revenu à ~50).
/// Jumeau LEAN : backtesting/backtests/algorithms/rsi_bracket_nq.py.
///   - Signal : RSI 9 (Wilder) aux bornes de 3 m, SANS filtre de régime — franchissement
///     sous 30 → long, au-dessus de 70 → short. Market ×1 + bracket SL/TP complet.
///   - SL = 1,5 × ATR14 (barres 3 m) | TP = 1R (défaut ; 1,5R à confirmer, spec).
///   - Sortie anticipée : RSI revenu à ~50 → ANNULATION du bracket + market de sortie.
///   - Sorties : TP, SL (plateforme), RSI 50, ou flat forcé (base).
/// </summary>
public sealed class RsiBracketHybride : HybrideStrategyBase
{
    [InputParameter("Période RSI (barres 3 m)", 20, 2, 100, 1, 0)]
    public int RsiPeriode = 9;

    [InputParameter("Seuil de survente (long sous ce niveau)", 21, 5, 50, 1, 0)]
    public int Survente = 30;

    [InputParameter("Seuil de surachat (short au-dessus)", 22, 50, 95, 1, 0)]
    public int Surachat = 70;

    [InputParameter("RSI de sortie anticipée (~moyenne)", 23, 30, 70, 1, 0)]
    public int Moyenne = 50;

    [InputParameter("Période ATR (barres 3 m)", 24, 2, 100, 1, 0)]
    public int AtrPeriode = 14;

    [InputParameter("Stop (× ATR)", 25, 0.5, 10, 0.5, 1)]
    public double StopMult = 1.5;

    [InputParameter("Take profit (× R)", 26, 0.5, 10, 0.5, 1)]
    public double TpR = 1.0;

    private const int TfSignal = 3;

    private AggregateurBarres _agg3 = null!;
    private RsiWilder _rsi = null!;
    private AtrWilder _atr = null!;
    private double _rsiPrec = double.NaN;

    public RsiBracketHybride()
    {
        Name = "Hybride H3 RSI Bracket (NQ)";
        Description = "RSI 9 en 3 m, bracket complet + annulation sur RSI 50 — jumeau LEAN : rsi_bracket_nq.py";
    }

    protected override string Slug => "rsi_bracket_nq";

    protected override void OnRun()
    {
        _agg3 = new AggregateurBarres(TfSignal);
        _rsi = new RsiWilder(RsiPeriode);
        _atr = new AtrWilder(AtrPeriode);
        base.OnRun();
    }

    protected override void SurBarre1m(in Barre1m b)
    {
        // SIGNAL aux bornes de 3 min (même règle d'agrégation que le jumeau).
        if (_agg3.Ajouter(b) is not { } b3) return;
        _atr.Ajouter(b3.H, b3.L, b3.C);
        _rsi.Ajouter(b3.C);
        if (!(_rsi.Pret && _atr.Pret)) return;
        double rsi = _rsi.Valeur;
        var indicateurs = new[] { ("rsi", rsi), ("atr", _atr.Valeur) };

        if (EnPosition && !SortieEnCours)
        {
            // Sortie anticipée : RSI revenu à ~50 → annulation du bracket + market.
            if ((SensPosition == Side.Buy && rsi >= Moyenne) || (SensPosition == Side.Sell && rsi <= Moyenne))
            {
                Journal("signal", prix: b3.C, raison: $"RSI revenu à {Moyenne} -> sortie anticipée",
                        indicateurs: indicateurs);
                EnvoyerSortieSignal(b3.FinUtc, $"RSI revenu à {Moyenne}");
            }
        }
        else if (!double.IsNaN(_rsiPrec))
        {
            bool entreSurvente = _rsiPrec >= Survente && rsi < Survente;
            bool entreSurachat = _rsiPrec <= Surachat && rsi > Surachat;
            if (entreSurvente || entreSurachat)
            {
                var refus = RaisonRefus(b3.FinUtc);
                string sensTxt = entreSurvente
                    ? $"franchissement sous {Survente} -> long"
                    : $"franchissement au-dessus de {Surachat} -> short";
                if (refus == "seed") { _rsiPrec = rsi; return; }
                if (refus is not null)
                {
                    Journal("signal", prix: b3.C, raison: $"{sensTxt} — REFUSÉ : {refus}",
                            indicateurs: indicateurs);
                }
                else
                {
                    Journal("signal", prix: b3.C, raison: sensTxt, indicateurs: indicateurs);
                    double slPoints = StopMult * _atr.Valeur;
                    EnvoyerEntree(b3.FinUtc, entreSurvente ? Side.Buy : Side.Sell, b3.C,
                                  EnTicks(slPoints), EnTicks(TpR * slPoints), indicateurs);
                }
            }
        }
        _rsiPrec = rsi;
    }
}
