using System.Globalization;
using TradingPlatform.BusinessLayer;

namespace Hybrides;

/// <summary>
/// Base COMMUNE des 3 stratégies hybrides (H1 ORB / H2 SMA suiveur / H3 RSI bracket) —
/// specs : automatisation/docs/strategies-hybrides.md ; jumeaux backtest : volet C
/// (backtesting/backtests/algorithms/). MÊME code sim/réel : le compte est un PARAMÈTRE
/// (verdict n°1 de l'étude Simulator) ; par défaut, seul un compte du Trading Simulator
/// est accepté (« Autoriser un compte réel » = la porte de la phase 5, fermée d'origine).
///
/// Répartition des rôles avec le jumeau LEAN : le jumeau simule les brackets dans sa
/// boucle 1 m ; ICI les SL/TP sont de VRAIS ordres attachés (SlTpHolder) — la plateforme
/// (ou le serveur : question stand-by au support) exécute, nous, on décide et on journalise.
/// Le journal NDJSON (même format que le jumeau) est la matière de la parité phase 4.
///
/// Flux de décision : seed d'indicateurs par GetHistory (barres 1 m), puis barres 1 m
/// VIVANTES reconstruites depuis Symbol.NewLast (le flux trades prouvé par le pont NqFeed)
/// — même règle d'agrégation 3/5 m que le jumeau. Le flat forcé tourne sur une HORLOGE
/// murale (timer 10 s), pas sur les barres : un marché muet à 16:55 n'empêche pas le flat
/// (trouvaille du volet C : les jours de clôture avancée, avancer le paramètre d'heure).
///
/// Pièges honorés (REPRISE) : InvariantCulture partout ; aucune I/O sur les threads de
/// marché (journal = file bornée + thread) ; symbole vivant vérifié (State/TickSize) ;
/// InstanceName et non Name pour étiqueter l'instance.
/// </summary>
public abstract class HybrideStrategyBase : Strategy
{
    protected static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // --- Paramètres communs (indices 0-9 ; les stratégies filles continuent à 20+) ------
    [InputParameter("Symbole (NQ front)", 0)]
    public Symbol? Instrument { get; set; }

    [InputParameter("Compte (inutile en shadow ; Trading Simulator sinon)", 1)]
    public Account? Compte { get; set; }

    // Les TROIS modes d'exécution (décisions user des 2026-07-19/20) :
    //   SHADOW (défaut)  — phase 4 : décisions + journal, ordres SIMULÉS au tick, zéro API.
    //   CONFIRMATION     — semi-automatisé, l'HUMAIN dans la boucle : l'humain initie chaque
    //     POSITION. Les ENTRÉES et les SORTIES SUR SIGNAL sont PROPOSÉES par un pop-up Alert
    //     (Utils.Alert.ActionOnConfirm, mesuré) — rien ne s'ouvre/se ferme discrétionnairement
    //     sans son clic. En revanche, la GESTION PROTECTRICE d'une position déjà confirmée
    //     s'applique AUTOMATIQUEMENT : stop suiveur (resserrement) et flat de fin de séance
    //     (obligatoire) — ça ne fait que réduire le risque, et confirmer le suiveur à chaque
    //     barre serait impraticable (H2). Compatible avec l'interdit Apex des bots (l'humain
    //     initie ; le reste est de la protection) ; la case « compte réel » = 2e consentement.
    //   AUTO             — ordres directs sans confirmation : Trading Simulator (ou phase 5).
    [InputParameter("Mode d'exécution", 2, variants: new object[] {
        "SHADOW — journal seulement, ZÉRO ordre", ModeShadowV,
        "CONFIRMATION — pop-up, l'utilisateur accepte chaque ordre", ModeConfirmationV,
        "AUTO — ordres directs (Simulator / phase 5)", ModeAutoV })]
    public int Mode = ModeShadowV;

    public const int ModeShadowV = 0, ModeConfirmationV = 1, ModeAutoV = 2;
    protected bool EnShadow => Mode == ModeShadowV;
    protected bool EnConfirmation => Mode == ModeConfirmationV;

    [InputParameter("Autoriser un compte réel (CONFIRMATION recommandé ; AUTO = phase 5)", 11)]
    public bool AutoriserCompteReel = false;

    [InputParameter("Validité d'une proposition (secondes, mode CONFIRMATION)", 12, 10, 600, 5, 0)]
    public int PropositionValiditeS = 120;

    // Coché : au démarrage, fait apparaître UN pop-up de confirmation dont le « OK » ne fait
    // que journaliser — AUCUN ordre, aucun compte requis. Sert à valider le mécanisme
    // (affichage + clic) sans risque, en mode SHADOW. À décocher ensuite.
    [InputParameter("Test : pop-up de confirmation au démarrage (aucun ordre)", 14)]
    public bool TestPopup = false;

    [InputParameter("Contrats", 3, 1, 10, 1, 0)]
    public int Contrats = 1;

    [InputParameter("Entrées à partir de (HH:mm ET)", 4)]
    public string EntreesDebutEt = "09:30";

    [InputParameter("Entrées jusqu'à (HH:mm ET)", 5)]
    public string EntreesFinEt = "15:30";

    [InputParameter("Flat forcé à (HH:mm ET — AVANCER les jours de clôture avancée)", 6)]
    public string HeureFlatEt = "16:55";

    // Décoché (défaut) = 24 h : entrées permises quand le marché est ouvert (donc aussi le
    // soir, l'« Asia » CME ouvre 18:00 ET), et PAS de flat forcé de séance. C'est le mode
    // pour tester/observer librement. Le RE-COCHER pour la réalité prop firm (séance NY +
    // flat 16:55) — surtout en CONFIRMATION/AUTO sur compte réel.
    [InputParameter("Restreindre à la séance NY (décoché = 24 h)", 13)]
    public bool SeanceNY = false;

    [InputParameter("Garde-fou : pertes pleines/jour (0 = désactivé)", 7, 0, 10, 1, 0)]
    public int PertesMax = 0;      // 0 en phase de test : ne pas brider les signaux

    [InputParameter("Cooldown après sortie (minutes)", 8, 0, 120, 1, 0)]
    public int CooldownMin = 0;    // recadrage 07-23 : 0 = ré-entrée dès la barre suivante (fréquence max)

    [InputParameter("Seed d'historique (heures de barres 1 m)", 9, 2, 168, 1, 0)]
    public int SeedHeures = 48;

    [InputParameter("Dossier des journaux NDJSON", 10)]
    public string JournalDossier = @"H:\IndicesBoursiers\automatisation\journaux";

    // --- Identité de la stratégie fille -------------------------------------------------
    /// <summary>Slug du journal (= nom du dossier), aligné sur le jumeau LEAN.</summary>
    protected abstract string Slug { get; }

    /// <summary>Une barre 1 m vient de fermer (seed PUIS live, dans l'ordre). Les filles y
    /// font tout : agrégation TF, indicateurs, décisions (via les helpers de la base).</summary>
    protected abstract void SurBarre1m(in Barre1m barre);

    /// <summary>La position vient d'ouvrir (fill confirmé — réel ou simulé en shadow).</summary>
    protected virtual void SurPositionOuverte(double prixEntree) { }

    /// <summary>La position vient de fermer (raison : SL | TP | SIGNAL | FLAT | KILL | AUTRE).</summary>
    protected virtual void SurPositionFermee(string raison) { }

    /// <summary>Une sortie sur stop est-elle une PERTE PLEINE (garde-fou) ? Défaut : tout
    /// SL touché. H2 (stop suiveur) affine : seulement si le fill est du côté perdant.</summary>
    protected virtual bool EstPertePleine(string raison, double prixSortie) => raison == "SL";

    // --- État interne -------------------------------------------------------------------
    protected readonly object Verrou = new();
    protected CadreSeance Cadre = null!;
    private JournalNdjson _journal = null!;
    private System.Threading.Timer? _horloge;
    private bool _demarre;
    private bool _enSeed;
    private string _idTypeMarket = "";

    private Barre1m? _barreEnCours;
    private DateTime _derniereOuvertureTraitee = DateTime.MinValue;
    private DateTime _dernierTradeVuUtc = DateTime.MinValue;
    protected double DernierClose { get; private set; } = double.NaN;

    protected Position? PositionCourante { get; private set; }
    protected double PrixEntree { get; private set; } = double.NaN;
    protected Side SensPosition { get; private set; }
    private bool _entreeEnCours;
    private bool _sortieEnCours;
    private string _raisonSortie = "";
    private string? _idOrdreEntree, _idOrdreSl, _idOrdreTp;
    private string? _dernierOrdreFille;
    private int _slTicksAttente, _tpTicksAttente;
    private (string cle, double valeur)[] _indicateursEntree = Array.Empty<(string, double)>();

    // --- Position SIMULÉE du mode shadow (le moteur des jumeaux, porté au tick) ---------
    private int _shadowSens;                      // 0 = flat, ±1 = sens de la position simulée
    private double _shadowStop = double.NaN;      // niveaux du bracket simulé
    private double _shadowTake = double.NaN;      // NaN = pas de TP (patron H2)
    private bool _shadowEntreeAttendue;           // market « envoyé », fill au prochain trade
    private Side _shadowSensAttendu;
    private bool _shadowSortieAttendue;           // sortie signal « envoyée », fill au prochain trade
    private string _shadowRaisonSortie = "";
    private int _shadowNumero;                    // ids shadow-N / shadow-N-bracket

    // --- Propositions du mode CONFIRMATION (pop-up Alert + ActionOnConfirm) -------------
    private int _propNumero;                      // ids prop-N (journal)
    private int _propActive;                      // proposition en cours (0 = aucune)
    private DateTime _propExpireUtc;
    private Action? _propGeste;                   // le geste RÉEL, exécuté au clic seulement

    protected bool EnPosition => PositionCourante is not null || _shadowSens != 0;
    protected bool SortieEnCours => _sortieEnCours || _shadowSortieAttendue;

    // ======================================================================== DÉMARRAGE =
    protected override void OnRun()
    {
        try { Demarrer(); }
        catch (Exception ex)
        {
            this.LogError($"Démarrage impossible : {ex}");
            this.Stop();
        }
    }

    private void Demarrer()
    {
        var s = Instrument;
        if (s is null) { this.LogError("Aucun symbole sélectionné (choisir NQ front)."); this.Stop(); return; }

        // Symbole vivant (piège du pont NqFeed : un symbole résolu n'est pas un symbole vivant).
        if (s.State == BusinessObjectState.Fake || double.IsNaN(s.TickSize) || s.TickSize <= 0)
        {
            this.LogError($"{s.Name} : symbole SANS FLUX (State={s.State}, TickSize={s.TickSize.ToString(Inv)}). "
                        + "Connecter Rithmic dans Quantower, PUIS relancer.");
            this.Stop();
            return;
        }

        string etiquetteCompte;
        if (EnShadow)
        {
            // SHADOW : zéro ordre, donc zéro compte — le paramètre Compte est IGNORÉ même
            // s'il est rempli (aucun chemin de code ne le touche).
            etiquetteCompte = "SHADOW (zéro ordre)";
        }
        else
        {
            if (Compte is null) { this.LogError("Aucun compte sélectionné (requis hors shadow)."); this.Stop(); return; }

            var typeConn = Compte.Connection?.Type;

            // GARDE DUR : AUTO (ordres directs, SANS humain) est INTERDIT hors Trading
            // Simulator. Sur un compte réel = un bot, du mauvais côté des règles prop firm
            // (supervision humaine active exigée — mémoire apex-regles-automatisation). AUCUNE
            // case ne lève ce refus : seul CONFIRMATION (l'humain initie) peut aller sur le réel.
            if (Mode == ModeAutoV && typeConn != ConnectionType.TradingSimulator)
            {
                this.LogError($"Mode AUTO REFUSÉ sur « {Compte.Name} » ({typeConn?.ToString() ?? "?"}) : "
                            + "les ordres automatiques (sans confirmation humaine) ne sont permis que "
                            + "sur le Trading Simulator. Sur un compte réel, utiliser CONFIRMATION.");
                this.Stop();
                return;
            }

            // GARDE ANTI-COMPTE-RÉEL : jamais d'ordre hors Simulator tant que la porte n'est
            // pas explicitement ouverte (paramètre). Le type de connexion fait foi.
            if (typeConn != ConnectionType.TradingSimulator && !AutoriserCompteReel)
            {
                this.LogError($"Compte « {Compte.Name} » sur une connexion {typeConn?.ToString() ?? "?"} : "
                            + "REFUSÉ. Ces stratégies ne tradent que le Trading Simulator tant que "
                            + "« Autoriser un compte réel » n'est pas coché.");
                this.Stop();
                return;
            }

            // Type d'ordre MARKET servi par la connexion du symbole (jamais un id deviné).
            var market = s.GetAlowedOrderTypes(OrderTypeUsage.Order)?
                .FirstOrDefault(t => t.Behavior == OrderTypeBehavior.Market);
            if (market is null)
            {
                this.LogError("La connexion ne sert pas d'ordre MARKET pour ce symbole (GetAlowedOrderTypes).");
                this.Stop();
                return;
            }
            _idTypeMarket = market.Id;
            etiquetteCompte = (EnConfirmation ? "CONFIRMATION @ " : "AUTO @ ") + $"{Compte.Name} ({typeConn})";
        }

        Cadre = new CadreSeance(EntreesDebutEt, EntreesFinEt, HeureFlatEt, PertesMax, CooldownMin);
        _journal = new JournalNdjson(JournalDossier, Slug, s.Root ?? s.Name, m => this.LogError(m));
        InstanceName = $"{Name} — {s.Name} @ {etiquetteCompte}";

        string seance = SeanceNY
            ? $"séance NY (entrées {EntreesDebutEt}-{EntreesFinEt} ET, flat {HeureFlatEt} ET)"
            : "24 h (pas de fenêtre ni de flat de séance)";
        this.LogInfo($"{Name} | {s.Name} (tick {s.TickSize.ToString(Inv)}) | {etiquetteCompte} | "
                   + $"{seance} | garde-fou {PertesMax} | cooldown {CooldownMin} m | journal : {_journal.Dossier}");
        Journal("demarrage", raison: $"{etiquetteCompte} — {seance}");

        // SEED : les indicateurs s'amorcent sur l'historique 1 m (jamais « attendre que ça
        // chauffe » en séance — spec). Le même chemin SurBarre1m que le live, ordres coupés.
        _enSeed = true;
        int barres = 0;
        HistoricalData? hd = null;
        try
        {
            var de = DateTime.UtcNow.AddHours(-SeedHeures);
            hd = s.GetHistory(new Period(BasePeriod.Minute, 1), HistoryType.Last, de, DateTime.UtcNow);
            if (hd is not null)
                foreach (var brut in hd)
                {
                    if (brut is not HistoryItemBar b) continue;
                    var ouverture = ToUtc(b.TimeLeft);
                    SurBarre1m(new Barre1m(ouverture, b.Open, b.High, b.Low, b.Close));
                    _derniereOuvertureTraitee = ouverture;
                    barres++;
                }
        }
        finally { hd?.Dispose(); _enSeed = false; }
        this.LogInfo($"Seed : {barres} barres 1 m relues ({SeedHeures} h demandées).");

        // Événements de la plateforme : fills, positions, et le flux trades (barres vivantes).
        Core.Instance.TradeAdded += SurTrade;
        Core.Instance.PositionAdded += SurPositionAjoutee;
        Core.Instance.PositionRemoved += SurPositionRetiree;
        s.NewLast += SurNouveauTrade;

        // L'horloge murale : flat forcé + ré-armement du garde-fou + détection de flux gelé.
        _horloge = new System.Threading.Timer(_ => TicHorloge(), null,
                                              TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        _demarre = true;

        // Test du pop-up (aucun ordre) : valide affichage + clic sans risque, même en shadow.
        if (TestPopup)
            lock (Verrou)
                Proposer("TEST — pop-up de confirmation", "clic sur OK = AUCUN ordre, juste un test",
                         () => this.LogInfo("✅ TEST : pop-up CONFIRMÉ par clic — le mécanisme fonctionne."));
    }

    // ============================================================================ ARRÊT =
    protected override void OnStop()
    {
        if (!_demarre) return;
        _demarre = false;
        _horloge?.Dispose(); _horloge = null;

        if (Instrument is { } s) s.NewLast -= SurNouveauTrade;
        Core.Instance.TradeAdded -= SurTrade;
        Core.Instance.PositionAdded -= SurPositionAjoutee;
        Core.Instance.PositionRemoved -= SurPositionRetiree;

        // KILL SWITCH (spec) : l'arrêt de la stratégie = tout annuler + liquider.
        lock (Verrou)
        {
            if (EnShadow)
            {
                if (_shadowSens != 0 || _shadowEntreeAttendue)
                {
                    _shadowEntreeAttendue = false;
                    _shadowSortieAttendue = false;
                    Journal("kill", prix: DernierClose, raison: "arrêt de la stratégie (simulé : position shadow soldée)");
                    if (_shadowSens != 0) FermerShadow(DernierClose, "KILL", pertePleine: false);
                }
            }
            else if (PositionCourante is not null || DesOrdresVivants())
            {
                _sortieEnCours = true;
                _raisonSortie = "KILL";
                Journal("kill", prix: DernierClose, raison: "arrêt de la stratégie : tout annuler + liquider");
                var r = Core.Instance.AdvancedTradingOperations.Flatten(Instrument, Compte, null);
                this.LogInfo($"Kill switch : Flatten → {r?.ToString() ?? "?"}");
            }
        }
        Journal("arret", raison: $"lignes jetées journal : {_journal.LignesJetees.ToString(Inv)}");
        _journal.Dispose();
        this.LogInfo("Stratégie arrêtée proprement.");
    }

    // ================================================================== BARRES VIVANTES =
    /// <summary>Reconstruit les barres 1 m depuis le flux trades — même matière première que
    /// l'extracteur/le CSV des jumeaux (une minute sans trade = pas de barre, comme en base).</summary>
    private void SurNouveauTrade(Symbol symbole, Last trade)
    {
        if (trade.Size <= 0) return;
        var t = ToUtc(trade.Time);
        var ouverture = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc);
        lock (Verrou)
        {
            _dernierTradeVuUtc = DateTime.UtcNow;
            if (EnShadow) MoteurShadow(t, trade.Price);
            if (_barreEnCours is { } b)
            {
                if (ouverture > b.OuvertureUtc)
                {
                    // La barre précédente vient de fermer (si le seed ne l'a pas déjà couverte).
                    if (b.OuvertureUtc > _derniereOuvertureTraitee)
                    {
                        _derniereOuvertureTraitee = b.OuvertureUtc;
                        DernierClose = b.Close;
                        SurBarre1m(b);
                    }
                    _barreEnCours = new Barre1m(ouverture, trade.Price, trade.Price, trade.Price, trade.Price);
                }
                else
                {
                    _barreEnCours = b with
                    {
                        High = Math.Max(b.High, trade.Price),
                        Low = Math.Min(b.Low, trade.Price),
                        Close = trade.Price,
                    };
                }
            }
            else if (ouverture > _derniereOuvertureTraitee)
            {
                _barreEnCours = new Barre1m(ouverture, trade.Price, trade.Price, trade.Price, trade.Price);
            }
        }
    }

    // ================================================================== MOTEUR SHADOW ==
    /// <summary>Le cycle de vie SIMULÉ des ordres, au tick : fill d'entrée/sortie au trade
    /// suivant l'envoi, bracket vérifié à chaque trade (SL prioritaire). Même vocabulaire
    /// de journal que le réel ; ids « shadow-N ». AUCUN appel à l'API d'ordres ici.</summary>
    private void MoteurShadow(DateTime tUtc, double prix)
    {
        // 1) Fill d'entrée simulé : le « market » se remplit au premier trade qui suit.
        if (_shadowEntreeAttendue)
        {
            _shadowEntreeAttendue = false;
            _shadowSens = _shadowSensAttendu == Side.Buy ? 1 : -1;
            PrixEntree = prix;
            SensPosition = _shadowSensAttendu;
            double tick = Instrument!.TickSize;
            _shadowStop = _shadowSens > 0 ? prix - _slTicksAttente * tick : prix + _slTicksAttente * tick;
            _shadowTake = _tpTicksAttente > 0
                ? (_shadowSens > 0 ? prix + _tpTicksAttente * tick : prix - _tpTicksAttente * tick)
                : double.NaN;
            StopCourantPlateforme = _shadowStop;
            string id = $"shadow-{_shadowNumero.ToString(Inv)}";
            Journal("fill", prix: prix, qte: _shadowSens * Contrats, idOrdre: id, raison: "fill d'entrée (simulé)");
            var indic = new List<(string, double)>(_indicateursEntree) { ("stop", _shadowStop) };
            if (!double.IsNaN(_shadowTake)) indic.Add(("take", _shadowTake));
            Journal("bracket_pose", prix: prix, idOrdre: $"{id}-bracket",
                    raison: $"bracket SIMULÉ au fill (SL {_slTicksAttente.ToString(Inv)} ticks"
                          + (_tpTicksAttente > 0 ? $", TP {_tpTicksAttente.ToString(Inv)} ticks)" : ", pas de TP)"),
                    indicateurs: indic.ToArray());
            this.LogInfo($"SHADOW : position {SensPosition} @ {prix.ToString(Inv)} | stop {_shadowStop.ToString(Inv)}"
                       + (double.IsNaN(_shadowTake) ? "" : $" | take {_shadowTake.ToString(Inv)}"));
            SurPositionOuverte(prix);
            return;
        }

        // 2) Fill de sortie signal/flat simulé.
        if (_shadowSortieAttendue)
        {
            _shadowSortieAttendue = false;
            FermerShadow(prix, _shadowRaisonSortie, pertePleine: false);
            return;
        }

        // 3) Bracket simulé : SL prioritaire, puis TP — à CHAQUE trade (plus fin que le 1 m
        // des jumeaux ; mêmes niveaux, donc mêmes décisions).
        if (_shadowSens == 0) return;
        if (!double.IsNaN(_shadowStop)
            && ((_shadowSens > 0 && prix <= _shadowStop) || (_shadowSens < 0 && prix >= _shadowStop)))
        {
            bool perte = EstPertePleine("SL", _shadowStop);
            Journal("fill", prix: _shadowStop, qte: -_shadowSens * Contrats,
                    idOrdre: $"shadow-{_shadowNumero.ToString(Inv)}-sl", raison: "fill de sortie [SL] (simulé)");
            FermerShadow(_shadowStop, "SL", perte);
        }
        else if (!double.IsNaN(_shadowTake)
                 && ((_shadowSens > 0 && prix >= _shadowTake) || (_shadowSens < 0 && prix <= _shadowTake)))
        {
            Journal("fill", prix: _shadowTake, qte: -_shadowSens * Contrats,
                    idOrdre: $"shadow-{_shadowNumero.ToString(Inv)}-tp", raison: "fill de sortie [TP] (simulé)");
            FermerShadow(_shadowTake, "TP", pertePleine: false);
        }
    }

    /// <summary>Clôture de la position simulée + garde-fou + hook fille.</summary>
    private void FermerShadow(double prixSortie, string raison, bool pertePleine)
    {
        if (raison is "SIGNAL" or "FLAT" or "KILL")
            Journal("fill", prix: prixSortie, qte: -_shadowSens * Contrats,
                    idOrdre: $"shadow-{_shadowNumero.ToString(Inv)}",
                    raison: $"fill de sortie [{raison}] (simulé)");
        _shadowSens = 0;
        _shadowStop = _shadowTake = double.NaN;
        StopCourantPlateforme = double.NaN;
        PrixEntree = double.NaN;
        if (Cadre.Sortie(DateTime.UtcNow, pertePleine))
            Journal("garde_fou",
                    raison: $"{PertesMax.ToString(Inv)} pertes pleines — arrêt jusqu'à {EntreesDebutEt} ET");
        this.LogInfo($"SHADOW : position fermée [{raison}]{(pertePleine ? " (perte pleine)" : "")} @ {prixSortie.ToString(Inv)}");
        SurPositionFermee(raison);
    }

    // =========================================================== MOTEUR DE CONFIRMATION =
    /// <summary>Mode CONFIRMATION : PROPOSE un geste par pop-up (Utils.Alert, bouton de
    /// confirmation). Le geste réel ne part QUE si l'utilisateur clique — c'est LUI qui
    /// initie chaque transaction. UNE proposition à la fois : la plus récente remplace
    /// l'ancienne ; ignorer = refus (expiration). Tout est journalisé (ids prop-N).</summary>
    private void Proposer(string titre, string texte, Action gesteReel)
    {
        if (_propActive != 0)
            Journal("annulation", idOrdre: $"prop-{_propActive.ToString(Inv)}",
                    raison: "proposition remplacée par une plus récente");
        _propNumero++;
        int id = _propNumero;
        _propActive = id;
        _propExpireUtc = DateTime.UtcNow.AddSeconds(PropositionValiditeS);
        _propGeste = gesteReel;
        Journal("proposition", prix: DernierClose, idOrdre: $"prop-{id.ToString(Inv)}",
                raison: $"{titre} — {texte} (valide {PropositionValiditeS.ToString(Inv)} s)");
        Core.Instance.Alert(new TradingPlatform.BusinessLayer.Utils.Alert
        {
            Name = titre,
            Text = $"{texte} — CONFIRMER = accepter ; ignorer = refuser "
                 + $"(expire dans {PropositionValiditeS.ToString(Inv)} s).",
            SymbolName = Instrument?.Name ?? "",
            ConnectionName = Compte?.Connection?.Name ?? "",
            AutoOpenAlertsLog = true,
            ActionOnConfirm = () => Confirmer(id),
        });
        this.LogInfo($"PROPOSITION #{id.ToString(Inv)} : {titre} — {texte}");
    }

    private void Confirmer(int id)
    {
        try
        {
            lock (Verrou)
            {
                if (id != _propActive)
                {
                    Journal("annulation", idOrdre: $"prop-{id.ToString(Inv)}",
                            raison: "confirmation reçue APRÈS expiration/remplacement — ignorée");
                    return;
                }
                var geste = _propGeste;
                _propActive = 0;
                _propGeste = null;
                Journal("proposition", idOrdre: $"prop-{id.ToString(Inv)}",
                        raison: "ACCEPTÉE par l'utilisateur (clic)");
                geste?.Invoke();
            }
        }
        catch (Exception ex) { this.LogError($"Confirmation : {ex}"); }
    }

    private void ExpirerProposition()
    {
        if (_propActive == 0 || DateTime.UtcNow < _propExpireUtc) return;
        Journal("annulation", idOrdre: $"prop-{_propActive.ToString(Inv)}",
                raison: $"proposition expirée sans réponse ({PropositionValiditeS.ToString(Inv)} s) — refus implicite");
        _propActive = 0;
        _propGeste = null;
    }

    // ======================================================================== L'HORLOGE =
    private void TicHorloge()
    {
        try
        {
            lock (Verrou)
            {
                var (jourEt, minutes) = CadreSeance.HeureEt(DateTime.UtcNow);
                Cadre.MajJour(jourEt, minutes);

                // Flux gelé pendant la séance (piège 17) : aucune nouvelle entrée.
                if (_dernierTradeVuUtc != DateTime.MinValue
                    && minutes >= Cadre.EntreeDebut && minutes < Cadre.FlatForce
                    && (DateTime.UtcNow - _dernierTradeVuUtc).TotalMinutes > 5)
                    this.LogInfo("⚠️ Aucun trade reçu depuis > 5 min en pleine séance : flux suspect, "
                               + "les entrées sont bloquées de fait (pas de barre = pas de signal).");

                ExpirerProposition();

                // 24 h (séance NY décochée) : pas de flat forcé de séance — on tient la
                // position jusqu'à un SL/TP/signal ou le kill switch manuel.
                if (!SeanceNY) return;

                // FLAT FORCÉ à l'horloge murale — indépendant des barres.
                if (EnShadow)
                {
                    if (minutes >= Cadre.FlatForce && (_shadowSens != 0 || _shadowEntreeAttendue))
                    {
                        _shadowEntreeAttendue = false;
                        _shadowSortieAttendue = false;
                        string id = $"shadow-{_shadowNumero.ToString(Inv)}";
                        Journal("annulation", idOrdre: $"{id}-bracket", raison: "flat forcé : bracket annulé (simulé)");
                        Journal("flat_force", prix: DernierClose,
                                raison: $"flat forcé {HeureFlatEt} ET (simulé, au dernier prix connu)");
                        if (_shadowSens != 0) FermerShadow(DernierClose, "FLAT", pertePleine: false);
                    }
                    return;
                }
                if (minutes < Cadre.FlatForce || _sortieEnCours
                    || (PositionCourante is null && !DesOrdresVivants()))
                    return;
                // Flat de fin de séance : AUTOMATIQUE même en CONFIRMATION. Être flat à la fin
                // est OBLIGATOIRE côté prop firm, et ça ne fait que fermer/réduire le risque —
                // on ne peut pas dépendre d'un clic humain à temps.
                FlatReel();
            }
        }
        catch (Exception ex) { this.LogError($"Horloge : {ex.Message}"); }
    }

    /// <summary>Le geste RÉEL du flat forcé : tout annuler + liquider (mécanisme n°4).</summary>
    private void FlatReel()
    {
        if (_sortieEnCours || (PositionCourante is null && !DesOrdresVivants())) return;
        Journal("annulation", idOrdre: IdBracket(), raison: "flat forcé : bracket annulé");
        _sortieEnCours = true;
        _raisonSortie = "FLAT";
        Journal("flat_force", prix: DernierClose,
                raison: $"flat forcé {HeureFlatEt} ET : tout annuler + liquider");
        var r = Core.Instance.AdvancedTradingOperations.Flatten(Instrument, Compte, null);
        if (r is not null) this.LogInfo($"Flat forcé : Flatten → {r}");
    }

    // ============================================================= HELPERS DE DÉCISION =
    /// <summary>null si une entrée est permise à la clôture de cette barre ; sinon la raison
    /// du refus (journalisée par la fille — même vocabulaire que le jumeau).</summary>
    protected string? RaisonRefus(DateTime finBarreUtc)
    {
        // "seed" et "en_position" = codes SILENCIEUX (les stratégies les ignorent sans
        // journaliser) : le premier chauffe les indicateurs, le second est l'état normal
        // pendant qu'un trade est ouvert. Les autres refus (fenêtre/garde-fou/cooldown) sont
        // journalisés — ils expliquent pourquoi un croisement n'a PAS été pris.
        if (_enSeed) return "seed";
        if (EnPosition || _entreeEnCours || _shadowEntreeAttendue || SortieEnCours) return "en_position";
        if (SeanceNY)      // 24 h si décoché : pas de fenêtre d'entrée
        {
            var (_, m) = CadreSeance.HeureEt(finBarreUtc);
            if (m <= Cadre.EntreeDebut || m > Cadre.EntreeFin)
                return $"hors fenêtre d'entrée ({EntreesDebutEt}-{EntreesFinEt} ET)";
        }
        if (Cadre.GardeFou) return "garde-fou journalier actif";
        if (!Cadre.CooldownOk(finBarreUtc)) return $"cooldown {CooldownMin.ToString(Inv)} min";
        return null;
    }

    /// <summary>Entrée market ×Contrats avec bracket ATTACHÉ (offsets en ticks depuis le
    /// fill — la sémantique mesurée de SlTpHolder ; tpTicks = 0 → pas de TP, patron H2).</summary>
    protected void EnvoyerEntree(DateTime tsUtc, Side sens, double prixDecision,
                                 int slTicks, int tpTicks,
                                 (string cle, double valeur)[] indicateurs)
    {
        if (_enSeed || Instrument is null) return;
        _slTicksAttente = slTicks;
        _tpTicksAttente = tpTicks;
        _indicateursEntree = indicateurs;

        if (EnShadow)
        {
            _shadowNumero++;
            _shadowEntreeAttendue = true;
            _shadowSensAttendu = sens;
            SensPosition = sens;
            Journal("entree_envoyee", prix: prixDecision, qte: sens == Side.Buy ? Contrats : -Contrats,
                    idOrdre: $"shadow-{_shadowNumero.ToString(Inv)}",
                    raison: $"market ×{Contrats.ToString(Inv)} SIMULÉ + SL {slTicks.ToString(Inv)} ticks"
                          + (tpTicks > 0 ? $" / TP {tpTicks.ToString(Inv)} ticks" : " (pas de TP)"));
            return;
        }
        if (Compte is null) return;
        if (EnConfirmation)
        {
            string sensTxt = sens == Side.Buy ? "LONG" : "SHORT";
            Proposer($"{Name} : ENTRÉE {sensTxt} ?",
                     $"market ×{Contrats.ToString(Inv)} @ ~{prixDecision.ToString(Inv)} + SL {slTicks.ToString(Inv)} ticks"
                   + (tpTicks > 0 ? $" / TP {tpTicks.ToString(Inv)} ticks" : " (pas de TP)"),
                     () => EntreeReelle(sens, prixDecision, slTicks, tpTicks));
            return;
        }
        EntreeReelle(sens, prixDecision, slTicks, tpTicks);
    }

    /// <summary>Le geste RÉEL d'entrée (AUTO : direct ; CONFIRMATION : au clic). Re-valide
    /// le cadre à l'instant de l'exécution — le clic peut arriver 2 minutes après le signal.</summary>
    private void EntreeReelle(Side sens, double prixDecision, int slTicks, int tpTicks)
    {
        var refus = RaisonRefus(DateTime.UtcNow);
        if (refus is not null)
        {
            Journal("annulation", raison: $"entrée non exécutée : {refus}");
            this.LogInfo($"Entrée refusée à l'exécution : {refus}");
            return;
        }

        var requete = new PlaceOrderRequestParameters
        {
            Account = Compte,
            Symbol = Instrument,
            Side = sens,
            OrderTypeId = _idTypeMarket,
            Quantity = Contrats,
            TimeInForce = TimeInForce.Day,
            StopLoss = SlTpHolder.CreateSL(slTicks, PriceMeasurement.Offset),
        };
        if (tpTicks > 0)
            requete.TakeProfit = SlTpHolder.CreateTP(tpTicks, PriceMeasurement.Offset);

        _entreeEnCours = true;
        SensPosition = sens;
        Journal("entree_envoyee", prix: prixDecision, qte: sens == Side.Buy ? Contrats : -Contrats,
                raison: $"market ×{Contrats.ToString(Inv)} + SL {slTicks.ToString(Inv)} ticks"
                      + (tpTicks > 0 ? $" / TP {tpTicks.ToString(Inv)} ticks" : " (pas de TP)"));
        var r = Core.Instance.PlaceOrder(requete);
        if (r.Status == TradingOperationResultStatus.Failure)
        {
            _entreeEnCours = false;
            Journal("annulation", raison: $"entrée REFUSÉE par la plateforme : {r.Message}");
            this.LogError($"PlaceOrder REFUSÉ : {r.Message}");
            return;
        }
        _idOrdreEntree = r.OrderId;
        this.LogInfo($"ENTRÉE {sens} envoyée (ordre {r.OrderId}) @ ~{prixDecision.ToString(Inv)}");
    }

    /// <summary>Sortie sur SIGNAL : annule le bracket (mécanisme n°3 des specs, ordre par
    /// ordre) puis ferme la position au market. Le fill arrivera par TradeAdded.</summary>
    protected void EnvoyerSortieSignal(DateTime tsUtc, string raisonTexte)
    {
        if (EnShadow)
        {
            if (_shadowSens == 0 || _shadowSortieAttendue) return;
            string id = $"shadow-{_shadowNumero.ToString(Inv)}";
            Journal("annulation", idOrdre: $"{id}-bracket", raison: $"sortie signal : bracket annulé ({raisonTexte}) (simulé)");
            _shadowStop = _shadowTake = double.NaN;          // le bracket simulé meurt ICI
            StopCourantPlateforme = double.NaN;
            Journal("sortie_envoyee", prix: DernierClose, qte: -_shadowSens * Contrats,
                    idOrdre: id, raison: $"{raisonTexte} (simulé)");
            _shadowSortieAttendue = true;
            _shadowRaisonSortie = "SIGNAL";
            return;
        }
        if (EnConfirmation)
        {
            if (PositionCourante is null || _sortieEnCours) return;
            Proposer($"{Name} : SORTIE ?",
                     $"{raisonTexte} — annuler le bracket + fermer au market",
                     () => SortieSignalReelle(raisonTexte));
            return;
        }
        SortieSignalReelle(raisonTexte);
    }

    /// <summary>Le geste RÉEL de sortie signal (annulation du bracket + close).</summary>
    private void SortieSignalReelle(string raisonTexte)
    {
        var p = PositionCourante;
        if (p is null || _sortieEnCours)
        {
            Journal("annulation", raison: $"sortie non exécutée ({raisonTexte}) : position déjà fermée");
            return;
        }
        _sortieEnCours = true;
        _raisonSortie = "SIGNAL";
        Journal("annulation", idOrdre: IdBracket(), raison: $"sortie signal : bracket annulé ({raisonTexte})");
        ResoudreOrdresLies();
        AnnulerSiVivant(_idOrdreSl);
        AnnulerSiVivant(_idOrdreTp);
        Journal("sortie_envoyee", prix: DernierClose,
                qte: p.Side == Side.Buy ? -p.Quantity : p.Quantity, idOrdre: p.Id, raison: raisonTexte);
        var r = p.Close();
        if (r?.Status == TradingOperationResultStatus.Failure)
            this.LogError($"ClosePosition REFUSÉ : {r.Message} (le flat de {HeureFlatEt} ET rattrapera)");
    }

    /// <summary>H2 : MODIFICATION du prix de déclenchement du stop existant (mécanisme n°2).
    /// Renvoie true si la plateforme a accepté.</summary>
    protected bool ModifierStop(DateTime tsUtc, double nouveauPrix,
                                (string cle, double valeur)[] indicateurs)
    {
        if (EnShadow)
        {
            if (_shadowSens == 0) return false;
            _shadowStop = nouveauPrix;
            StopCourantPlateforme = nouveauPrix;
            Journal("stop_modifie", prix: nouveauPrix,
                    idOrdre: $"shadow-{_shadowNumero.ToString(Inv)}-sl",
                    raison: "suiveur (simulé)", indicateurs: indicateurs);
            return true;
        }
        // CONFIRMATION : le suiveur s'applique AUTOMATIQUEMENT (comme en AUTO). Il ne fait que
        // RESSERRER un stop protecteur sur une position DÉJÀ confirmée par l'humain — jamais une
        // nouvelle prise de risque — donc pas de pop-up (sinon un par barre = impraticable pour
        // H2). Seules les ENTRÉES demandent un clic. (Décision 2026-07-22 ; lecture des règles
        // Apex : l'humain initie chaque POSITION, la gestion protectrice est automatique.)
        return ModifStopReelle(nouveauPrix, indicateurs);
    }

    /// <summary>Le geste RÉEL de modification du stop (mécanisme n°2 des specs).</summary>
    private bool ModifStopReelle(double nouveauPrix, (string cle, double valeur)[] indicateurs)
    {
        if (PositionCourante is null)
        {
            Journal("annulation", raison: "modification de stop non exécutée : position déjà fermée");
            return false;
        }
        ResoudreOrdresLies();
        var ordre = TrouverOrdre(_idOrdreSl);
        if (ordre is null)
        {
            this.LogInfo("Stop introuvable pour modification (bracket pas encore visible ?) — retenté à la prochaine barre.");
            return false;
        }
        var requete = new ModifyOrderRequestParameters(ordre) { TriggerPrice = nouveauPrix };
        var r = Core.Instance.ModifyOrder(requete);
        if (r.Status == TradingOperationResultStatus.Failure)
        {
            this.LogError($"ModifyOrder REFUSÉ : {r.Message}");
            return false;
        }
        Journal("stop_modifie", prix: nouveauPrix, idOrdre: ordre.Id, raison: "suiveur", indicateurs: indicateurs);
        return true;
    }

    /// <summary>Journalise un événement (enfilage non bloquant, format jumeau).</summary>
    protected void Journal(string evenement, double? prix = null, double? qte = null,
                           string? idOrdre = null, string raison = "",
                           params (string cle, double valeur)[] indicateurs)
        => _journal.Ecrire(DateTime.UtcNow, evenement, prix, qte, idOrdre, raison, indicateurs);

    // ================================================== ÉVÉNEMENTS DE LA PLATEFORME ====
    private void SurPositionAjoutee(Position p)
    {
        if (!EstANous(p.Account) || !EstNotreSymbole(p.Symbol)) return;
        lock (Verrou)
        {
            PositionCourante = p;
            PrixEntree = p.OpenPrice;
            SensPosition = p.Side;
            _entreeEnCours = false;
            ResoudreOrdresLies();

            double tick = Instrument!.TickSize;
            double sl = p.Side == Side.Buy ? p.OpenPrice - _slTicksAttente * tick
                                           : p.OpenPrice + _slTicksAttente * tick;
            var indic = new List<(string, double)>(_indicateursEntree) { ("stop", sl) };
            if (_tpTicksAttente > 0)
                indic.Add(("take", p.Side == Side.Buy ? p.OpenPrice + _tpTicksAttente * tick
                                                      : p.OpenPrice - _tpTicksAttente * tick));
            Journal("bracket_pose", prix: p.OpenPrice, idOrdre: IdBracket(),
                    raison: $"bracket attaché au fill (SL {_slTicksAttente.ToString(Inv)} ticks"
                          + (_tpTicksAttente > 0 ? $", TP {_tpTicksAttente.ToString(Inv)} ticks)" : ", pas de TP)"),
                    indicateurs: indic.ToArray());
            this.LogInfo($"POSITION ouverte {p.Side} @ {p.OpenPrice.ToString(Inv)} (id {p.Id})");
            SurPositionOuverte(p.OpenPrice);
        }
    }

    private void SurPositionRetiree(Position p)
    {
        if (!EstANous(p.Account) || !EstNotreSymbole(p.Symbol)) return;
        lock (Verrou)
        {
            if (PositionCourante is null) return;
            string raison = _sortieEnCours ? _raisonSortie
                : _dernierOrdreFille == _idOrdreSl && _idOrdreSl is not null ? "SL"
                : _dernierOrdreFille == _idOrdreTp && _idOrdreTp is not null ? "TP"
                : "AUTRE";
            double prixSortie = p.CurrentPrice;
            bool pertePleine = (raison is "SL" or "AUTRE") && EstPertePleine("SL", prixSortie);
            if (raison == "AUTRE")
                this.LogInfo("Position fermée sans intention connue (SL/TP non identifié) — "
                           + "comptée prudemment via EstPertePleine.");

            var maintenant = DateTime.UtcNow;
            if (Cadre.Sortie(maintenant, pertePleine))
                Journal("garde_fou",
                        raison: $"{PertesMax.ToString(Inv)} pertes pleines — arrêt jusqu'à {EntreesDebutEt} ET");
            this.LogInfo($"POSITION fermée [{raison}]{(pertePleine ? " (perte pleine)" : "")}");

            PositionCourante = null;
            PrixEntree = double.NaN;
            _idOrdreEntree = _idOrdreSl = _idOrdreTp = null;
            _sortieEnCours = false;
            SurPositionFermee(raison);
        }
    }

    private void SurTrade(Trade t)
    {
        if (!EstANous(t.Account) || !EstNotreSymbole(t.Symbol)) return;
        lock (Verrou)
        {
            _dernierOrdreFille = t.OrderId;
            string raison = t.OrderId == _idOrdreEntree ? "fill d'entrée"
                : t.OrderId == _idOrdreSl ? "fill de sortie [SL]"
                : t.OrderId == _idOrdreTp ? "fill de sortie [TP]"
                : _sortieEnCours ? $"fill de sortie [{_raisonSortie}]"
                : "fill";
            Journal("fill", prix: t.Price,
                    qte: t.Side == Side.Buy ? t.Quantity : -t.Quantity,
                    idOrdre: t.OrderId, raison: raison);
        }
    }

    // ============================================================== PETITE PLOMBERIE ===
    private bool EstANous(Account? a) =>
        a is not null && Compte is not null && a.Id == Compte.Id && a.ConnectionId == Compte.ConnectionId;

    private bool EstNotreSymbole(Symbol? s) => s is not null && Instrument is not null && s.Id == Instrument.Id;

    /// <summary>Les SL/TP attachés apparaissent comme des ordres liés à la position — on les
    /// retrouve par PositionId (ils ne sont pas forcément visibles à l'instant du fill).</summary>
    private void ResoudreOrdresLies()
    {
        var p = PositionCourante;
        if (p is null || (_idOrdreSl is not null && _idOrdreTp is not null)) return;
        foreach (var o in Core.Instance.Orders)
        {
            if (o.PositionId != p.Id || !EstANous(o.Account)) continue;
            var comportement = o.OrderType?.Behavior;
            if (comportement is OrderTypeBehavior.Stop or OrderTypeBehavior.StopLimit or OrderTypeBehavior.TrailingStop)
                _idOrdreSl ??= o.Id;
            else if (comportement is OrderTypeBehavior.Limit)
                _idOrdreTp ??= o.Id;
        }
        var sl = TrouverOrdre(_idOrdreSl);
        if (sl is not null && PositionCourante is not null && double.IsNaN(StopCourantPlateforme))
            StopCourantPlateforme = sl.TriggerPrice;
    }

    /// <summary>Dernier prix de déclenchement CONNU du stop attaché (NaN si pas encore vu).</summary>
    protected double StopCourantPlateforme { get; set; } = double.NaN;

    protected Order? TrouverOrdre(string? id) =>
        id is null ? null : Core.Instance.Orders.FirstOrDefault(o => o.Id == id);

    private void AnnulerSiVivant(string? id)
    {
        var o = TrouverOrdre(id);
        if (o is null || o.Status is OrderStatus.Cancelled or OrderStatus.Filled or OrderStatus.Refused) return;
        var r = o.Cancel(null);
        if (r?.Status == TradingOperationResultStatus.Failure)
            this.LogError($"CancelOrder {id} REFUSÉ : {r.Message}");
    }

    private bool DesOrdresVivants() =>
        Core.Instance.Orders.Any(o => EstANous(o.Account) && EstNotreSymbole(o.Symbol)
                                      && o.Status is OrderStatus.Opened or OrderStatus.PartiallyFilled);

    private string IdBracket() =>
        _idOrdreSl is null && _idOrdreTp is null ? (_idOrdreEntree ?? "?") + "-bracket"
            : $"{_idOrdreSl ?? "-"}/{_idOrdreTp ?? "-"}";

    protected static DateTime ToUtc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();

    /// <summary>Points → ticks entiers (au moins 1), pour les offsets de bracket.</summary>
    protected int EnTicks(double points)
    {
        double tick = Instrument?.TickSize is { } ts && ts > 0 ? ts : 0.25;
        return Math.Max(1, (int)Math.Round(points / tick));
    }
}
