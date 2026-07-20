using TradingPlatform.BusinessLayer;

namespace Hybrides;

/// <summary>
/// H1 — Cassure de plage d'ouverture (ORB) · mécanique : cassure · PROUVE : le bracket.
/// Jumeau LEAN : backtesting/backtests/algorithms/orb_nq.py (même logique, mêmes défauts).
///   - Plage : plus-haut/plus-bas de 09:30 → 10:00 ET (barres 1 m).
///   - Entrée : première clôture 1 m au-delà d'une borne, 10:00 → 12:00 ET, AU PLUS UNE
///     entrée par jour → market ×1 + bracket SL/TP ATTACHÉ (offsets en ticks du fill).
///   - SL = 1,5 × ATR14 (barres 5 m, valeur au signal) | TP = 2R. Pas de stop suiveur.
///   - Sorties : TP, SL (exécutés par la plateforme/serveur) ou flat forcé (base).
/// ⚠ Démarrée en cours de fenêtre (10:00-12:00), la stratégie peut prendre une cassure
/// TARDIVE (la « première » du seed est passée sans ordre) — journalisé, la parité le verra.
/// </summary>
public sealed class OrbHybride : HybrideStrategyBase
{
    [InputParameter("Plage d'ouverture (minutes)", 20, 15, 60, 15, 0)]
    public int PlageMin = 30;

    [InputParameter("Fin de la fenêtre d'entrée (HH:mm ET)", 21)]
    public string FenetreFinEt = "12:00";

    [InputParameter("Période ATR (barres 5 m)", 22, 2, 100, 1, 0)]
    public int AtrPeriode = 14;

    [InputParameter("Stop (× ATR)", 23, 0.5, 10, 0.5, 1)]
    public double StopMult = 1.5;

    [InputParameter("Take profit (× R)", 24, 0.5, 10, 0.5, 1)]
    public double TpR = 2.0;

    private const int TfAtr = 5;

    private AggregateurBarres _agg5 = null!;
    private AtrWilder _atr = null!;
    private DateTime _jourEt = DateTime.MinValue;
    private double _borneHaute = double.NaN, _borneBasse = double.NaN;
    private bool _entreeFaite;
    private int _fenetreFin;

    public OrbHybride()
    {
        Name = "Hybride H1 ORB (NQ)";
        Description = "Cassure de plage d'ouverture, bracket SL/TP attaché — jumeau LEAN : orb_nq.py";
    }

    protected override string Slug => "orb_nq";

    protected override void OnRun()
    {
        _agg5 = new AggregateurBarres(TfAtr);
        _atr = new AtrWilder(AtrPeriode);
        _fenetreFin = CadreSeance.ParseHeure(FenetreFinEt);
        base.OnRun();
    }

    protected override void SurBarre1m(in Barre1m b)
    {
        // ATR sur barres 5 m agrégées (même règle que le jumeau).
        if (_agg5.Ajouter(b) is { } b5)
            _atr.Ajouter(b5.H, b5.L, b5.C);

        var (jourEt, m) = CadreSeance.HeureEt(b.FinUtc);
        if (jourEt != _jourEt)
        {
            _jourEt = jourEt;
            _borneHaute = _borneBasse = double.NaN;
            _entreeFaite = false;
        }

        // Construction de la plage : barres 1 m closes entre l'ouverture et ouverture+30.
        if (m > Cadre.EntreeDebut && m <= Cadre.EntreeDebut + PlageMin)
        {
            _borneHaute = double.IsNaN(_borneHaute) ? b.High : Math.Max(_borneHaute, b.High);
            _borneBasse = double.IsNaN(_borneBasse) ? b.Low : Math.Min(_borneBasse, b.Low);
            return;
        }

        // ENTRÉE : première clôture 1 m au-delà d'une borne, dans la fenêtre 10:00 → 12:00.
        if (_entreeFaite || double.IsNaN(_borneHaute) || !_atr.Pret
            || m <= Cadre.EntreeDebut + PlageMin || m > _fenetreFin)
            return;
        Side sens;
        if (b.Close > _borneHaute) sens = Side.Buy;
        else if (b.Close < _borneBasse) sens = Side.Sell;
        else return;

        var refus = RaisonRefus(b.FinUtc);
        if (refus == "seed") return;             // seed : on chauffe les indicateurs, rien d'autre
        var indicateurs = new[]
        {
            ("borne_haute", _borneHaute), ("borne_basse", _borneBasse), ("atr", _atr.Valeur),
        };
        if (refus is not null)
        {
            Journal("signal", prix: b.Close, raison: $"cassure REFUSÉE : {refus}", indicateurs: indicateurs);
            return;
        }

        _entreeFaite = true;                     // au plus UNE entrée par jour, gagnée ou perdue
        double slPoints = StopMult * _atr.Valeur;
        Journal("signal", prix: b.Close,
                raison: $"première clôture 1 m au-delà de la borne -> {(sens == Side.Buy ? "long" : "short")}",
                indicateurs: indicateurs);
        EnvoyerEntree(b.FinUtc, sens, b.Close, EnTicks(slPoints), EnTicks(TpR * slPoints), indicateurs);
    }
}
