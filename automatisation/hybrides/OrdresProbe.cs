using System.Globalization;
using System.Text;
using TradingPlatform.BusinessLayer;

namespace Hybrides;

/// <summary>
/// « Ordres Probe (SIM) » — la sonde du volet B (etude-simulator.md §9), à lancer PENDANT
/// l'essai 7 jours, en séance, sur le compte du Trading Simulator. Elle déroule, seule :
///   0. dump des types d'ordres servis (GetAlowedOrderTypes) + inventaire des connexions
///      (le côté Rithmic est LU, jamais tradé) ;
///   A. market ×1 + bracket ATTACHÉ (SL 20 ticks / TP 40) → le SL est MODIFIÉ deux fois
///      (suiveur simulé) → ClosePosition → le sort du bracket est OBSERVÉ (annulé ou pas ?) ;
///   B. market ×1 avec TP à 2 ticks (SL loin) → attendre le TP TOUCHÉ ;
///   C. market ×1 avec SL à 2 ticks (TP loin) → attendre le SL TOUCHÉ ;
///   D. filet : Flatten(compte) — tout annuler + liquider — puis arrêt.
/// Chaque étape est journalisée en NDJSON (même format que les hybrides/jumeaux).
///
/// GARDE-FOU CODÉ EN DUR : refuse de démarrer si la connexion du compte n'est pas
/// ConnectionType.TradingSimulator — IMPOSSIBLE de la pointer par accident sur l'Apex
/// (aucun paramètre ne l'y autorise, contrairement aux stratégies de la phase 5).
///
/// Question « où vit le stop » (stand-by support) : la sonde apporte UN élément à la main —
/// pendant l'étape A, fermer Quantower AVANT la 2e modification et regarder si le stop a
/// survécu au redémarrage (procédure dans le README ; rien d'automatisable proprement).
/// </summary>
public sealed class OrdresProbe : Strategy
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [InputParameter("Symbole (NQ front)", 0)]
    public Symbol? Instrument { get; set; }

    [InputParameter("Compte (Trading Simulator OBLIGATOIRE)", 1)]
    public Account? Compte { get; set; }

    [InputParameter("Offset SL initial (ticks)", 2, 1, 200, 1, 0)]
    public int SlTicks = 20;

    [InputParameter("Offset TP initial (ticks)", 3, 1, 400, 1, 0)]
    public int TpTicks = 40;

    [InputParameter("Pas de modification du SL (ticks)", 4, 1, 50, 1, 0)]
    public int PasModifTicks = 5;

    [InputParameter("Timeout par étape (secondes)", 5, 30, 600, 10, 0)]
    public int TimeoutS = 180;

    [InputParameter("Dossier des journaux NDJSON", 6)]
    public string JournalDossier = @"H:\IndicesBoursiers\automatisation\journaux";

    private enum Etape
    {
        Dump, PoserA, AttendrePositionA, Modif1, Modif2, FermerA, ObserverBracketA,
        PoserB, AttendreTpB, PoserC, AttendreSlC, Bilan, Fini,
    }

    private readonly object _verrou = new();
    private JournalNdjson _journal = null!;
    private System.Threading.Timer? _tic;
    private Etape _etape = Etape.Dump;
    private DateTime _etapeDepuis = DateTime.UtcNow;
    private DateTime _pasAvant = DateTime.MinValue;   // temporisation entre deux gestes
    private bool _demarre;
    private string _idTypeMarket = "";
    private Position? _position;
    private string? _idSl, _idTp;
    private int _modifsAcceptees;
    private readonly StringBuilder _verdicts = new();

    public OrdresProbe()
    {
        Name = "Ordres Probe (SIM)";
        Description = "Sonde du cycle de vie des ordres attachés sur le Trading Simulator (volet B §9)";
    }

    protected override void OnRun()
    {
        var s = Instrument;
        if (s is null) { this.LogError("Aucun symbole sélectionné."); this.Stop(); return; }
        if (Compte is null) { this.LogError("Aucun compte sélectionné."); this.Stop(); return; }
        if (s.State == BusinessObjectState.Fake || double.IsNaN(s.TickSize) || s.TickSize <= 0)
        {
            this.LogError($"{s.Name} : symbole SANS FLUX — connecter Rithmic puis relancer.");
            this.Stop(); return;
        }

        // LE garde-fou : Trading Simulator ou rien. Codé en dur, pas de paramètre d'évasion.
        if (Compte.Connection?.Type != ConnectionType.TradingSimulator)
        {
            this.LogError($"Compte « {Compte.Name} » : connexion {Compte.Connection?.Type.ToString() ?? "?"} "
                        + "≠ TradingSimulator → REFUS. La sonde ne trade QUE le compte papier.");
            this.Stop(); return;
        }

        var market = s.GetAlowedOrderTypes(OrderTypeUsage.Order)?
            .FirstOrDefault(t => t.Behavior == OrderTypeBehavior.Market);
        if (market is null) { this.LogError("Pas de type MARKET servi pour ce symbole."); this.Stop(); return; }
        _idTypeMarket = market.Id;

        _journal = new JournalNdjson(JournalDossier, "ordres_probe", s.Root ?? s.Name, m => this.LogError(m));
        InstanceName = $"{Name} — {s.Name} @ {Compte.Name}";
        _journal.Ecrire(DateTime.UtcNow, "demarrage",
                        raison: $"sonde sur {Compte.Name} (TradingSimulator), SL {SlTicks.ToString(Inv)} / "
                              + $"TP {TpTicks.ToString(Inv)} ticks, pas {PasModifTicks.ToString(Inv)}");

        Core.Instance.TradeAdded += SurTrade;
        Core.Instance.PositionAdded += SurPositionAjoutee;
        Core.Instance.PositionRemoved += SurPositionRetiree;
        _tic = new System.Threading.Timer(_ => Tic(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _demarre = true;
        this.LogInfo("Sonde démarrée — déroulé automatique (dump → bracket+modifs+close → TP → SL → flat).");
    }

    protected override void OnStop()
    {
        if (!_demarre) return;
        _demarre = false;
        _tic?.Dispose(); _tic = null;
        Core.Instance.TradeAdded -= SurTrade;
        Core.Instance.PositionAdded -= SurPositionAjoutee;
        Core.Instance.PositionRemoved -= SurPositionRetiree;
        if (_etape != Etape.Fini && Instrument is not null && Compte is not null)
        {
            _journal.Ecrire(DateTime.UtcNow, "kill", raison: "arrêt manuel : Flatten de sécurité");
            Core.Instance.AdvancedTradingOperations.Flatten(Instrument, Compte, null);
        }
        _journal.Ecrire(DateTime.UtcNow, "arret", raison: "sonde arrêtée");
        _journal.Dispose();
    }

    // ------------------------------------------------------------------ machine à états -
    private void Tic()
    {
        try { lock (_verrou) { Derouler(); } }
        catch (Exception ex) { this.LogError($"Sonde : {ex}"); }
    }

    private void Derouler()
    {
        if (DateTime.UtcNow < _pasAvant) return;

        // Timeout d'étape : on note, on sécurise, on arrête — jamais de sonde zombie.
        if (_etape is not (Etape.Dump or Etape.Bilan or Etape.Fini)
            && (DateTime.UtcNow - _etapeDepuis).TotalSeconds > TimeoutS)
        {
            Verdict($"TIMEOUT à l'étape {_etape} après {TimeoutS.ToString(Inv)} s");
            Aller(Etape.Bilan);
            return;
        }

        switch (_etape)
        {
            case Etape.Dump: FaireDump(); Aller(Etape.PoserA); break;

            case Etape.PoserA: Poser(Side.Buy, SlTicks, TpTicks, "A : bracket + modifs + close"); Aller(Etape.AttendrePositionA); break;
            case Etape.AttendrePositionA:
                if (_position is null) return;
                ResoudreOrdresLies();
                if (_idSl is null) return;              // le bracket doit être visible avant de modifier
                Verdict($"A1 bracket visible : SL={_idSl ?? "-"} TP={_idTp ?? "-"}");
                Temporiser(3); Aller(Etape.Modif1); break;

            case Etape.Modif1:
            case Etape.Modif2:
                if (ModifierSl()) { Temporiser(3); Aller(_etape == Etape.Modif1 ? Etape.Modif2 : Etape.FermerA); }
                break;

            case Etape.FermerA:
                Verdict($"A2 modifications de SL acceptées : {_modifsAcceptees.ToString(Inv)}/2");
                _journal.Ecrire(DateTime.UtcNow, "sortie_envoyee", idOrdre: _position?.Id,
                                raison: "A : ClosePosition — le sort du bracket est LA mesure");
                _position?.Close();
                Temporiser(5); Aller(Etape.ObserverBracketA); break;

            case Etape.ObserverBracketA:
                if (_position is not null) return;      // la fermeture n'est pas encore passée
                bool slVivant = EstVivant(_idSl);
                bool tpVivant = EstVivant(_idTp);
                Verdict($"A3 après ClosePosition : SL {(slVivant ? "ENCORE VIVANT ⚠️" : "annulé ✓")}, "
                      + $"TP {(tpVivant ? "ENCORE VIVANT ⚠️" : "annulé ✓")}");
                _journal.Ecrire(DateTime.UtcNow, "annulation",
                                raison: $"A : brackets après close — SL vivant={slVivant.ToString(Inv)}, "
                                      + $"TP vivant={tpVivant.ToString(Inv)}");
                if (slVivant || tpVivant)
                    Core.Instance.AdvancedTradingOperations.CancelOrders(Compte, null);
                RaZPosition();
                Aller(Etape.PoserB); break;

            case Etape.PoserB: Poser(Side.Buy, 200, 2, "B : TP à 2 ticks — attendre le TP touché"); Aller(Etape.AttendreTpB); break;
            case Etape.AttendreTpB:
                if (_position is not null) { ResoudreOrdresLies(); return; }
                if (_idSl is null && _idTp is null) return;   // pas encore ouverte
                Verdict("B TP touché : position fermée par le bracket ✓");
                RaZPosition();
                Aller(Etape.PoserC); break;

            case Etape.PoserC: Poser(Side.Buy, 2, 200, "C : SL à 2 ticks — attendre le SL touché"); Aller(Etape.AttendreSlC); break;
            case Etape.AttendreSlC:
                if (_position is not null) { ResoudreOrdresLies(); return; }
                if (_idSl is null && _idTp is null) return;
                Verdict("C SL touché : position fermée par le bracket ✓");
                RaZPosition();
                Aller(Etape.Bilan); break;

            case Etape.Bilan:
                var r = Core.Instance.AdvancedTradingOperations.Flatten(Instrument, Compte, null);
                _journal.Ecrire(DateTime.UtcNow, "flat_force",
                                raison: $"D : Flatten final de sécurité ({r?.ToString() ?? "?"})");
                this.LogInfo("=== VERDICTS DE LA SONDE ===\n" + _verdicts);
                Aller(Etape.Fini);
                this.Stop(); break;
        }
    }

    private void FaireDump()
    {
        var s = Instrument!;
        var types = s.GetAlowedOrderTypes(OrderTypeUsage.All);
        var liste = types is null ? "aucun"
            : string.Join(" | ", types.Select(t => $"{t.Name} ({t.Behavior}, id={t.Id})"));
        Verdict($"0 GetAlowedOrderTypes({s.Name}) : {liste}");
        foreach (var c in Core.Instance.Connections.All)
            Verdict($"0 connexion « {c.Name} » : type={c.Type}, état={c.State}");
        _journal.Ecrire(DateTime.UtcNow, "signal", raison: $"dump types d'ordres : {liste}");
    }

    private void Poser(Side sens, int sl, int tp, string etiquette)
    {
        var requete = new PlaceOrderRequestParameters
        {
            Account = Compte!,
            Symbol = Instrument!,
            Side = sens,
            OrderTypeId = _idTypeMarket,
            Quantity = 1,
            TimeInForce = TimeInForce.Day,
            StopLoss = SlTpHolder.CreateSL(sl, PriceMeasurement.Offset),
            TakeProfit = SlTpHolder.CreateTP(tp, PriceMeasurement.Offset),
        };
        _journal.Ecrire(DateTime.UtcNow, "entree_envoyee", qte: sens == Side.Buy ? 1 : -1,
                        raison: $"{etiquette} (SL {sl.ToString(Inv)} / TP {tp.ToString(Inv)} ticks)");
        var r = Core.Instance.PlaceOrder(requete);
        if (r.Status == TradingOperationResultStatus.Failure)
        {
            Verdict($"PlaceOrder REFUSÉ ({etiquette}) : {r.Message}");
            Aller(Etape.Bilan);
        }
        else this.LogInfo($"{etiquette} : ordre {r.OrderId} envoyé.");
    }

    private bool ModifierSl()
    {
        ResoudreOrdresLies();
        var ordre = _idSl is null ? null : Core.Instance.Orders.FirstOrDefault(o => o.Id == _idSl);
        if (ordre is null) return false;
        double tick = Instrument!.TickSize;
        double nouveau = ordre.TriggerPrice + PasModifTicks * tick;   // long : le SL remonte
        var r = Core.Instance.ModifyOrder(new ModifyOrderRequestParameters(ordre) { TriggerPrice = nouveau });
        if (r.Status == TradingOperationResultStatus.Failure)
        {
            Verdict($"ModifyOrder REFUSÉ : {r.Message}");
            Aller(Etape.Bilan);
            return false;
        }
        _modifsAcceptees++;
        _journal.Ecrire(DateTime.UtcNow, "stop_modifie", prix: nouveau, idOrdre: ordre.Id,
                        raison: $"modification {_modifsAcceptees.ToString(Inv)}/2 (suiveur simulé)");
        return true;
    }

    // ------------------------------------------------------------------ événements ------
    private void SurPositionAjoutee(Position p)
    {
        if (!EstANous(p.Account) || p.Symbol?.Id != Instrument?.Id) return;
        lock (_verrou)
        {
            _position = p;
            _journal.Ecrire(DateTime.UtcNow, "bracket_pose", prix: p.OpenPrice, idOrdre: p.Id,
                            raison: $"position {p.Side} ouverte @ {p.OpenPrice.ToString(Inv)}");
        }
    }

    private void SurPositionRetiree(Position p)
    {
        if (!EstANous(p.Account) || p.Symbol?.Id != Instrument?.Id) return;
        lock (_verrou) { _position = null; }
    }

    private void SurTrade(Trade t)
    {
        if (!EstANous(t.Account) || t.Symbol?.Id != Instrument?.Id) return;
        _journal.Ecrire(DateTime.UtcNow, "fill", prix: t.Price,
                        qte: t.Side == Side.Buy ? t.Quantity : -t.Quantity,
                        idOrdre: t.OrderId, raison: $"fill (étape {_etape})");
    }

    // ------------------------------------------------------------------ plomberie -------
    private void ResoudreOrdresLies()
    {
        var p = _position;
        if (p is null) return;
        foreach (var o in Core.Instance.Orders)
        {
            if (o.PositionId != p.Id || !EstANous(o.Account)) continue;
            if (o.OrderType?.Behavior is OrderTypeBehavior.Stop or OrderTypeBehavior.StopLimit) _idSl ??= o.Id;
            else if (o.OrderType?.Behavior is OrderTypeBehavior.Limit) _idTp ??= o.Id;
        }
    }

    private bool EstVivant(string? id) =>
        id is not null && Core.Instance.Orders.Any(o => o.Id == id
            && o.Status is OrderStatus.Opened or OrderStatus.PartiallyFilled);

    private bool EstANous(Account? a) =>
        a is not null && Compte is not null && a.Id == Compte.Id && a.ConnectionId == Compte.ConnectionId;

    private void RaZPosition() { _position = null; _idSl = _idTp = null; }
    private void Aller(Etape e) { _etape = e; _etapeDepuis = DateTime.UtcNow; }
    private void Temporiser(int secondes) => _pasAvant = DateTime.UtcNow.AddSeconds(secondes);

    private void Verdict(string texte)
    {
        _verdicts.AppendLine(texte);
        this.LogInfo(texte);
    }
}
