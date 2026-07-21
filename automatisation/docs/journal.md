# Journal du projet — décisions datées & mesures clés

## 2026-07-20 — REFONTE des 3 stratégies : DÉCLENCHEUR COMMUN (fréquence des signaux)

**Constat utilisateur après le 1er test live de H1** : lancée à 14:22 ET (hors fenêtre
ORB 10:00-12:00), elle n'a rien journalisé — comportement correct, mais l'ORB (≤ 1 entrée/
jour dans une fenêtre de 2 h) donne **trop peu de signaux** pour observer la mécanique.
Décision : **repenser les 3 stratégies** autour d'un **déclencheur COMMUN, simple et
fréquent** ; elles ne diffèrent QUE par la gestion d'ordre (choix user parmi 3 options).
- **Déclencheur commun** : croisement SMA 9/21 sur closes **1 m**. Les 3 partagent
  `DeclencheurSmaCross` (Indicateurs.cs) — une seule implémentation.
- **Tueurs de fréquence retirés** : fenêtre ORB, une entrée/jour, filtres ; cooldown
  15 → **2 min** ; garde-fou **désactivé par défaut** (`PertesMax = 0` en test ; 0 = off,
  géré dans CadreSeance C# et py).
- **Les 3** (une mécanique chacune, conservée) : **H1 SMA Bracket** (bracket SL 1,5×ATR /
  TP 1R, attend SL/TP) · **H2 SMA Suiveur** (SL 2×ATR + suiveur, sort au croisement
  inverse) · **H3 SMA Annulation** (bracket SL 1,5×ATR / TP 2R, annulé au croisement
  inverse). Anciennes classes `OrbHybride`/`RsiBracketHybride` et jumeaux `orb_nq`/
  `rsi_bracket_nq` **supprimés** ; nouveaux : `Sma{Bracket,Annule}Hybride.cs` +
  `sma_{bracket,annule}_nq.py` ; `SmaSuiveurHybride`/`sma_suiveur_nq` **portés du 5 m au
  1 m**.
- **Runs LEAN de contrôle** (banc 06-01→07-10, ~28 séances) : H1 **442 entrées**
  (229 SL / 213 TP) · H2 **476 entrées**, stop modifié **3183 fois** · H3 **421 entrées**,
  **111 annulations** — chaque mécanique largement exercée, ~15-17 entrées/jour (contre 1
  pour l'ORB). **Parité indicateurs C#↔LEAN : 9034 comparaisons, 0 écart** (tout sur 1 m,
  plus d'écart d'amorçage). Compile 0/0, DLL + visuel H1 (`SmaBracketVisuel`, remplace
  `OrbNqVisuel`) redéployés. 3 modes (SHADOW/CONFIRMATION/AUTO) inchangés.
- **RESTE user** : redémarrer Quantower → graphe NQ 1 m + « Hybride H1 SMA Bracket
  (visuel) » + stratégie H1 en SHADOW pendant une séance → le journal devrait fourmiller
  de signaux et coller au visuel. Visuels H2/H3 sur demande.

## 2026-07-20 (nuit, suite) — VISUEL H1 : l'indicateur « Hybride H1 ORB (visuel) » [SUPERSÉDÉ par la refonte ci-dessus]

**Demande utilisateur** : tester UNE stratégie à la fois, H1 d'abord, et VOIR sur le
graphique — lignes, niveaux, marqueurs. Réponse : un INDICATEUR (les stratégies Quantower
ne dessinent pas), patron `SignauxNq` de la phase 3. `indicators/OrbNqVisuel/` — sur un
graphe **NQ 1 m** : bornes de la plage (bleu, 10:00→16:55 ET), flèche d'entrée à la
cassure (une/jour), SL rouge / TP vert pointillés pendant le trade, rond de sortie
(TP/SL/flat). **Une seule implémentation des formules** : le csproj inclut
`hybrides/Indicateurs.cs` (+ `CadreSeance.cs`) par Compile Include — le visuel ne peut
pas dériver de la stratégie ni du jumeau (parité déjà mesurée). Décisions aux clôtures
seulement (`UpdateReason` + garde de temps) ; fill pris au close du signal (la stratégie
se remplit au trade suivant — écart ~1 tick, assumé). N'émet rien. Compile 0/0 net10.0,
✅ déployé dans `Settings\Scripts\Indicators\OrbNqVisuel`. H2/H3 : visuels à venir SUR
DEMANDE, un à la fois (décision user : une stratégie testée à la fois).
**✅ VALIDÉ SUR GRAPHIQUE par l'utilisateur (capture du 2026-07-20 14:19)** : vendredi =
plage large (plongeon-rebond d'ouverture), entrée longue 10:35, TP touché 11:50 ; lundi =
bornes 29 192,50 / 28 979,75 (spike de 09:55), short sur cassure basse ~10:02, SL touché
~10:07 (rebond) — bornes, flèches, brackets et sorties tous lisibles et conformes.

## 2026-07-20 (nuit) — MODE CONFIRMATION : le semi-automatisé, l'humain dans la boucle

**Décision utilisateur** : sur Apex, remplacer les ordres automatiques par des
**propositions à accepter/refuser** — le logiciel signale, l'HUMAIN initie chaque
transaction (semi-automatisé ; l'utilisateur, source d'autorité sur ses règles, confirme
que c'est acceptable — c'est d'ailleurs la formulation d'origine de la phase 5, « ordres
réels semi-automatisés »). **Implémenté le soir même** : le booléen shadow devient un
**sélecteur à 3 modes** (`variants` d'InputParameter, mesuré) —
**SHADOW** (défaut, inchangé) · **CONFIRMATION** · **AUTO** (Simulator/phase 5).
Mode CONFIRMATION : chaque geste (entrée, modification du suiveur, sortie signal, flat)
est proposé par un **pop-up `Utils.Alert` avec `ActionOnConfirm`** (bouton mesuré dans le
dump) — rien ne part sans le clic ; ignorer = refus (expiration paramétrable, 120 s) ;
UNE proposition à la fois, la plus récente remplace ; re-validation du cadre À l'instant
du clic (le clic peut arriver 2 min après le signal). Prudences : le **suiveur n'est
jamais appliqué seul** (refusé = le stop reste, toujours protecteur) ; le **flat de fin
de séance = pop-up insistant** (rappel/minute), jamais d'ordre auto ; le bouton Stop (un
clic humain) garde son Flatten. La case « Autoriser un compte réel » reste un DEUXIÈME
consentement explicite. Journal : événement `proposition` (ids `prop-N`) ajouté au
vocabulaire (comme `demarrage`/`arret`). Compile 0/0, DLL redéployée.
**À VALIDER en 1er à l'usage** : le rendu du pop-up et le déclenchement du clic
(README §À sonder, point 0 — premier essai en MNQ ×1).

## 2026-07-20 (soir) — ⛔ Essai 7 j MORT → pivot MODE SHADOW (défaut) + sonde manuelle Apex

Constat utilisateur : l'essai 7 jours du Trading Simulator est « terminé » (déjà consommé)
— la voie d'accès retenue au volet B est morte. Contrainte utilisateur (source d'autorité
sur son compte) : **Apex interdit les transactions automatisées et les bots** → ni les
hybrides ni la sonde logicielle sur le compte Apex, peu importe l'objectif POC ; « respecter
les règles à la lettre » = zéro ordre logiciel sur ce compte. **Décisions** :
1. **MODE SHADOW ajouté aux 3 hybrides et COCHÉ PAR DÉFAUT** (= la phase 4 du plan,
   gratuite) : décisions sur flux réel + journal NDJSON identique (ids `shadow-N`), cycle
   de vie des ordres SIMULÉ AU TICK dans `HybrideStrategyBase` (fill au trade suivant,
   bracket vérifié à chaque trade, suiveur, annulations, flat/kill) — AUCUN appel à l'API
   d'ordres, compte ignoré. Compile 0 erreur/0 warning, DLL redéployée.
2. **Sonde MANUELLE sur Apex** ([sonde-manuelle-apex.md](sonde-manuelle-apex.md)) : la
   checklist mécanique du §3 se répond À LA MAIN en MNQ ×1 (trading manuel = permis) ;
   ⛔ ne JAMAIS fermer Quantower avec une position ouverte (survie du stop = question au
   support, recommandée MAINTENANT — elle était en stand-by).
3. La mécanique d'ordres PAR NOTRE CODE attendra : achat du Simulator (pack Advanced
   Features / All-in-One, garantie 10 j — décision de coût utilisateur) ou phase 5. La
   sonde « Ordres Probe (SIM) » et la garde anti-compte-réel restent prêtes, inchangées.

## 2026-07-20 — CODE LIVE FAIT : les 3 hybrides + la sonde « Ordres Probe (SIM) » (`hybrides/`)

**UNE DLL (`Hybrides.dll`, net10.0), 4 stratégies** : `OrbHybride` (H1, bracket),
`SmaSuiveurHybride` (H2, ModifyOrder), `RsiBracketHybride` (H3, annulation) +
`OrdresProbe` (la sonde §9 en machine à états : dump GetAlowedOrderTypes → bracket +
2 modifs + close (sort du bracket OBSERVÉ) → TP touché → SL touché → Flatten).
Méthode Phase 0 tenue : API re-dumpée par réflexion AVANT d'écrire (846 types) —
`Flatten(symbole, compte)` fait le « tout annuler + liquider » en UN appel ;
`GetAlowedOrderTypes` (coquille du vendor incluse) donne le type MARKET par connexion ;
compile **0 erreur / 0 warning du premier coup** contre la v1.146.14.
Architecture : compte en `[InputParameter]` (même code sim/réel) ; **garde anti-compte-réel**
(refus si connexion ≠ TradingSimulator, paramètre « phase 5 » pour lever — la sonde n'a PAS
ce paramètre, codé en dur) ; seed 48 h par `GetHistory` puis barres 1 m VIVANTES depuis
`NewLast` (patron NqFeed) ; brackets en `SlTpHolder` **Offset ticks** ; flat forcé sur
HORLOGE murale (timer 10 s — la réponse au trou « clôture avancée » du volet C : heure de
flat paramétrable) ; kill switch = Stop ; journal NDJSON même format que les jumeaux
(file bornée + thread, InvariantCulture, append + `demarrage`/`arret`).
**PARITÉ INDICATEURS MESURÉE** (banc `hybrides/parite/` : rejeu du CSV 1 m du banc dans
les classes C#) : **3 799 comparaisons vs les journaux LEAN des jumeaux, 10 écarts, tous
dans les 5 premières heures du 1er juin** (amorçage de Wilder, max 0,35 pt de RSI, éteint
en ~90 min) — au-delà : exact à l'arrondi près. ✅ DÉPLOYÉ dans
`C:\Quantower\Settings\Scripts\Strategies\Hybrides` (redémarrage Quantower requis).
🪤 Bug trouvé au passage : les ANCIENS deploy.ps1 (`..\..\..\Settings`) visent `C:\Settings`
(fantôme) au lieu de `C:\Quantower\Settings` — corrigé dans `hybrides/deploy.ps1`, les
anciens scripts restent à reprendre. **À SONDER** (la compile ne prouve pas tout) :
sémantique Offset=ticks, sort du bracket après `Close()`, ordre des événements
`PositionRemoved`/`TradeAdded` — c'est le rôle de la sonde, pendant l'essai 7 jours.
**RESTE : activer l'essai 7 jours (décision user, quand tout est prêt) → dérouler la sonde
→ semaine de POC des 3 hybrides** (critère de succès des specs, journal à l'appui).

## 2026-07-19 — Volet C FAIT : jumeaux backtest des 3 hybrides (pilier Backtesting)

3 jumeaux LEAN **à côté des 8** dans `backtesting/backtests/algorithms/` — `orb_nq.py`
(H1, bracket), `sma_suiveur_nq.py` (H2, modification), `rsi_bracket_nq.py` (H3,
annulation) — + `cadre_hybride.py` (cadre commun, même rôle que `nq_instrument.py` :
séance ET par zoneinfo, garde-fou 2 pertes pleines ré-armé à 09:30, cooldown 15 min,
journal NDJSON). SL/TP/suiveur **simulés dans la boucle 1 m** (patron
`risque_stops_nq.py`) ; le jumeau reproduit les DÉCISIONS, la mécanique d'ordres réelle
se prouvera en live. **3 runs LEAN menés** (fenêtre du banc 06-01 → 07-10, Launcher du
frère crypto) ; journaux de décisions dans `backtests/journaux/<strategie>/` (un fichier
NDJSON par jour ET, hors git — c'est le comparant de la phase 4). **Invariants
vérifiés** sur les 2 998 événements : fenêtres d'entrée ET respectées, suiveur jamais en
recul, aucune entrée sous garde-fou, flat forcé ≥ 16:55. Chiffres : H1 28 entrées
(1/jour max) ; H2 75 entrées, stop modifié **617 fois** ; H3 103 entrées, 17 annulations
sur RSI 50. Rentabilité = non-sujet (cadrage user).
🪤 **Trouvaille — clôture avancée** : le 3 juillet (CME 13:00 ET), pas de barre 16:55 →
H2 a porté sa position le week-end, flat à la réouverture dimanche 18:01 ET. L'angle
mort est dans la SPEC ; ajouté aux « décisions restantes » de strategies-hybrides.md (le
live devra flatter avant la clôture réelle du jour). **Reste** : code live des 3
stratégies + sonde « Ordres Probe (SIM) » → activer l'essai 7 j → semaine de POC.

## 2026-07-19 — Volet B FAIT : étude Simulator ([etude-simulator.md](etude-simulator.md))

Méthode Phase 0 (réflexion DLL + poste + doc officielle, **zéro déploiement, zéro ordre**).
Dump de **846 types publics** de `TradingPlatform.BusinessLayer.dll` v1.146.14 via la console
`poc/Phase0Poc` (modes `dump`/`type`). **Les 5 verdicts** :
1. **MÊME CODE sim/réel (MESURÉ)** — le compte est un paramètre de la requête
   (`OrderRequestParameters.Account`/`ConnectionId`, `AccountComplexIdentifier`) ; aucune API
   parallèle pour le papier. La question d'ouverture du chantier est réglée.
2. **Le Trading Simulator est PAYANT (DOC)** — absent de la version gratuite (Multi-Asset /
   All-in-One 70 $/mois dégressif / pack Advanced Features) ; **MAIS l'API de stratégies est
   GRATUITE** et un **essai 7 jours complet** existe → le POC tient dedans. ⚠️ La thèse du
   site (« panneaux verrouillés, donnée ouverte ») ne se transpose PAS : un moteur licencié
   n'est pas une donnée — contournement **exclu**.
3. **Toute la checklist des specs existe dans l'API (MESURÉ)** : brackets `SlTpHolder`
   (+`IsTrailing`), `ModifyOrder`/`AdjustStopLoss`, `CancelOrder`/`ClosePosition`, OCO
   (`PlaceOrders(GroupOrderType.OCO)`), flat par compte (`CancelOrders(account)` +
   `ClosePositions(account)`), événements complets (`TradeAdded`, `Order.Updated`,
   `PartiallyFilled`…). Réserve : la dispo PAR CONNEXION (`Vendor.GetAllowedOrderTypes`) →
   sonde « Ordres Probe (SIM) » spécifiée (§9, garde-fou : refuse tout compte non-Simulator).
4. **Le Simulator émule par-dessus les connexions branchées (DOC)** → flux réel Rithmic,
   même session, pas de conflit Apex. ⚠️ Chaque démarrage/arrêt du panneau = **compte NEUF**.
5. **Où vit le stop : NON TRANCHÉ** — indice `Core.LocalOrders` (moteur d'ordres côté
   plateforme). Sans gravité pour le POC ; **question de sécurité de la phase 5** → à poser
   au support Quantower (gratuit).
🪤 **Piège de build découvert** : la DLL v1.146.14 référence `System.Runtime 10.0.0.0` →
toute nouvelle compile doit cibler **net10.0** (CS1705 sinon ; les DLL net8 déployées
chargent encore). `Phase0Poc` compilé via `-p:TargetFramework=net10.0` (csproj inchangé).
**Décisions utilisateur (même soir)** : **essai 7 jours RETENU** — ⚠️ à n'activer que quand
la sonde et les stratégies seront prêtes à rouler (le compteur part au premier jour) ;
**question des stops au support : STAND-BY**, à poser avant la phase 5. Ordre des travaux :
volet C (jumeaux backtest) → code live + sonde → activer l'essai → semaine de POC.

## 2026-07-19 — PIVOT : stratégies hybrides (volet A fait — les specs sont écrites)

**Décision utilisateur : on n'automatise PAS les 8 stratégies du backtesting telles quelles.**
Ce sont des artefacts de backtest (signal sur clôture, position tout-ou-rien, aucun ordre
réellement placé) et le banc LEAN a rendu son verdict (pas d'edge démontré) : le pilier
automatisation prouve la **chaîne d'exécution**, pas un edge. À la place, **3 stratégies
HYBRIDES** = entrée simple empruntée au banc × vraie gestion d'ordres, **une par mécanique** :
- **H1 ORB** (cassure de plage d'ouverture 09:30-10:00 ET) — market + **bracket SL/TP attaché** ;
- **H2 SMA 9/21, 15 m** (tendance) — SL seul + **stop suiveur 2×ATR** = exerce la
  *modification* d'ordres ;
- **H3 RSI 9 (30/70), 3 m** (retour à la moyenne) — bracket complet + **annulation** sur RSI 50.

Cadre commun : **NQ mini ×1**, stop **k×ATR14** / cible en **R**, une position à la fois,
**règles Apex dès la conception** (entrées 09:30-15:30 ET, flat forcé 16:55 ET, garde-fou
**2 pertes pleines → journée finie**, kill switch — défauts à confirmer). « Mode simulation »
tranché par l'utilisateur = **compte papier (Simulator)**, pas le backtester. Specs complètes +
checklist des mécanismes d'ordres à vérifier : [strategies-hybrides.md](strategies-hybrides.md).
Cette checklist est l'ENTRÉE du volet B (étude Simulator — prompt
`Claude_Code/Prompt_Automatisation_Hybrides.md`) ; question la plus lourde : **où vit le stop
(serveur Rithmic vs plateforme)**. Les phases 4-5 du plan visent désormais ces 3 hybrides ;
les 8 du banc restent intouchées dans leur pilier.

**Précision utilisateur (même soir) — L'OBJECTIF DE TOUT LE PROJET = PREUVE DE CONCEPT ET DE
FONCTIONNALITÉS, PAS LA RENTABILITÉ** (l'historique est trop court pour des backtests
crédibles ; « l'automatisation n'a pas besoin d'être profitable, il faut avant tout créer de
quoi qui fonctionne »). Specs resserrées en conséquence : **filtres de régime RETIRÉS**
(SMA 48/SMA 50 — des features de performance, et les plus gros consommateurs d'historique),
H2 passé en **5 m** (plus d'événements), **cooldowns 15 min** (les 45-60 min du banc étaient
de l'anti-frais), **warm-up par seed `GetHistory`** (besoin réel ≈ 2 h de barres), et une
section **« critère de succès »** ajoutée : chaque mécanisme (bracket, suiveur modifié,
annulation, flat forcé, garde-fou, kill, redémarrage) observé au moins une fois dans le
journal NDJSON. Un jour perdant qui déclenche proprement le garde-fou = un succès de test.

**Ajout (même soir) — VOLET C : les 3 hybrides seront BACKTESTÉES dans le pilier
Backtesting** (décision utilisateur ; nécessité structurelle : la phase 4 « shadow » compare
le live au backtest — sans jumeau, rien à comparer). Rangement décidé : **à côté des 8** dans
`backtesting/backtests/algorithms/` (`orb_nq.py`, `sma_suiveur_nq.py`, `rsi_bracket_nq.py`),
patron `risque_stops_nq.py` (SL/TP/suiveur simulés dans la boucle 1 m), cadre de séance
modélisé (09:30-15:30, flat 16:55, garde-fou), journal de décisions au même format NDJSON que
le live (= la matière de la parité phase 4), fenêtre du banc 06-01→07-10 = **référence de
décisions, pas verdict de perf**. **Séquence décidée : B (étude Simulator) → C (jumeaux) →
code live.** Les 8 du banc ne bougent pas ; fiches site plus tard, question à poser avant.

## 2026-07-08 — Phase 3 démarrée : 1er indicateur (SMA Cross) + harnais de parité

Phase visuelle → construction collaborative. API réfléchie : `Indicator`
(OnInit/OnUpdate/OnPaintChart), `AddLineSeries`/`SetValue`, `LineSeries.SetMarker` +
`IndicatorLineMarker` (flèches), accès prix via `HistoricalData.Close(offset)`/`Time(offset)`,
natifs via `Core.Instance.Indicators.BuiltIn.SMA/RSI/MACD/ATR`, footprint via
`VolumeAnalysisData.PriceLevels`/`.Total.Delta`.

**`SMA Cross NQ (50/200)`** construit + déployé (`indicators/SmaCrossNq/` →
`Settings\Scripts\Indicators\`) : 2 lignes + flèches aux croisements, **exporte**
`time,sma_rapide,sma_lente,signal` → `F:\data\parity\NQ-sma-quantower.csv`. Choix : SMA d'abord
(moyenne simple = parité EXACTE attendue) pour valider la méthode avant RSI Wilder/EMA/VP.
Harnais `indicators/parity_sma.py` (référence Python OK, 337 barres ; attend l'export Quantower).
**Rendu validé** (capture user) après fix : bug « lignes plates » = `HistoricalData.Close(offset)`
absolu → corrigé en `GetPrice(PriceType.Close, offset)` (relatif, idiome confirmé sur
Quantower/Examples `SimpleMovingAverage.cs`). Marqueurs OK (flèche death cross 02/07).

**Parité SMA #1 mesurée & expliquée** (`docs/phase3-parity-sma.md`) : 97/107 barres aux closes
identiques → SMA identique. 10 barres diffèrent : 9 à ≤11 ticks (tick de frontière d'heure,
bornes ms vs agrégation Quantower) + 1 à +42,75 pt = dernière barre partielle au snapshot.
**Formule exacte, écarts = données de bord.** Méthode de parité validée. Suite : RSI Wilder / EMA
seed (vraies divergences de formule à mesurer), puis VP session. Outils : `GetPrice` relatif,
`this.Time(0)`, `VolumeAnalysisData` + `Core.Instance.VolumeAnalysis.CalculateProfile(hd)` (vu
sur l'exemple `AccessCustomVolumeAnalysisData.cs`) pour la VP.

## 2026-07-10 — RÉORIENTATION : scalping / orderflow temps réel

**Décision utilisateur** : l'objectif réel est le **scalping / orderflow** (ticks + carnet temps
réel), pas les 8 stratégies bar-based (trop lentes ; 1 h = swing). Ces stratégies restent valides
(Phases 1-3) mais deviennent un track secondaire. Nouveau track `orderflow/`. Réalités posées :
HFT littéral non (latence/colo/propfirm) mais scalping orderflow oui ; backtest orderflow limité
(ticks+aggressor OK, pas d'historique DOM) → validation surtout forward/shadow.

Choix (méthode « mesurer d'abord », comme Phase 0) : **sonde orderflow** `Orderflow Probe (NQ)`
déployée — Strategy qui souscrit au flux live (`Symbol.NewLast/NewQuote/NewLevel2`,
`DepthOfMarket.GetDepthOfMarketAggregatedCollections`) et mesure sur N s : débit ticks/s,
aggressor peuplé EN DIRECT, latence, et **disponibilité + profondeur du carnet DOM** (LA question).
API : souscription via events du symbole (déjà actif dans la stratégie ; `AddSubscription`/
`SubscribeSymbol` sont côté vendor, pas user). Détails : [orderflow/README.md](../orderflow/README.md).

**RÉSULTATS sonde (run 2026-07-10 03:52 UTC, overnight/thin) — `docs/orderflow-probe.md` :**
- **Tape** : aggressor **100 % peuplé EN DIRECT** (buy 7/sell 13). Débit **1 tick/s** — mais session
  overnight morte (03:52 UTC = ~23h ET) → **à re-mesurer en RTH NY** (13:30-20:00 UTC) pour le vrai débit.
- **Quote** : ~30 updates/s (meilleur bid/ask actif).
- **DOM / Level2 : DISPONIBLE et PROFOND** — **984 niveaux bid, 815 ask**, ~58 updates/s. ⭐ Le carnet
  temps réel EST accessible (≠ la « volume analysis » historique payante) → **signaux de carnet
  possibles** (imbalance, absorption, empilement). Débloquant.
- Latence estimée 262 ms (contaminée par l'écart d'horloge → borne haute ; sub-seconde = OK scalping).

**Conclusion** : les DEUX familles de signaux orderflow sont faisables — tape/delta (CVD, imbalance
d'agression, absorption ; backtestables sur l'historique ticks) ET carnet/DOM (live/forward).

**1er moteur orderflow déployé** : `CVD Orderflow (NQ)` (`orderflow/CvdOrderflow/`). Indicateur
fenêtre séparée, sans chandelles : **CVD** (delta cumulé des ticks, seed `GetTickHistory` + incrément
tick-par-tick live via `Symbol.NewLast`, reset session/jour, résolution 15 s→1 h) + **imbalance carnet**
(bid vs ask sur N niveaux via `Symbol.NewLevel2`/`DepthOfMarket`, affichée en OnPaintChart). Thread-safe
(`ConcurrentDictionary`, throttle 150 ms). **À faire** : run utilisateur en RTH. Prochaines briques :
absorption, vitesse du tape, puis signaux d'entrée scalping → stratégie shadow.

## 2026-07-10 — Phase 3 : indicateur signaux (strat. n°7) — dernière brique

`Signaux NQ (strat. avancée)` : reproduit sur le graphe la logique de `strategie_avancee_nq`
(régime SMA200, entrée croisement MACD confirmé RSI>50, sorties stop suiveur 2×ATR/take 4×ATR/
casse de régime), marqueurs entrée (flèche verte) / sortie (rouge=stop, verte=take, orange=régime).
Indicateurs natifs hébergés (SMA/RSI/ATR/EMA12/EMA26 ; MACD=EMA12−EMA26, signal=EMA9 maison).
Exporte `NQ-signaux-quantower.csv` (time,action,raison,prix,rsi,regime) → **concordance vs
backtest LEAN = objet de la Phase 4** (shadow mode). Sur graphe 1 h (résolution de la stratégie).
**Phase 3 : 5 indicateurs livrés (SMA/RSI/VP/EMA/signaux), 4 parités validées.** Prochaine : Phase 4.

## 2026-07-10 — Phase 3 : EMA (résolution fixe) + parité #4

`EMA NQ (native)` : mode Graphe héberge `BuiltIn.EMA` (parité native) ; modes fixes (défaut 1 h)
calculent l'EMA sur `Symbol.GetHistory(<résolution>)` (seed SMA, 2/(N+1)) et la projettent →
indépendante de l'affichage (comme la SMA). Testée par l'utilisateur en **30 min**.
`parity_ema.py --resolution-min 30` (clôtures 30 min reconstruites depuis la base de ticks) :
**seed = SMA** (3,92 vs 76,7 first-price), zone convergée max 3,92 / moyen 0,22 — résiduel = 45
clôtures 30 min de bord différentes, pas la formule. Rapport : [phase3-parity-ema.md](phase3-parity-ema.md).
**Reste Phase 3** : indicateur signaux (marqueurs entrée/sortie + régime) → pont Phase 4.

## 2026-07-09 — Phase 3 : VP par session (footprint maison, sans fonction payante)

**Blocage mesuré** : le volume analysis natif de Quantower (`IVolumeAnalysisIndicator` /
`VolumeAnalysisData.PriceLevels`) exige un **abonnement payant**. Décision utilisateur : **calculer
le footprint nous-mêmes** depuis les ticks Rithmic **gratuits** (accès tick déjà validé par
l'extracteur). `VpSessionNq` réécrit sans la fonction payante : demande les ticks (`GetTickHistory`,
par jour, en tâche de fond → pas de gel UI), reconstruit sous-briques 30 min / niveaux 5 pts /
sessions NY / value area 70 % — portage direct de `volume_profile_features.py`. Pose POC/VAH/VAL
par barre + exporte `time,session,barres,delta,poc,vah,val`. Parité vs `features_vp.csv` via
`parity_vp.py`. La version « native » (paywall) reste dans l'historique git si abonnement un jour.
Rendu itéré avec l'utilisateur : histogramme horizontal (pas 3 lignes), **par session** (encadré +
histo aligné à gauche chevauchant la session + 2 lignes VAH/VAL), menu **« Affichage »** (Volume
buy/sell · Volume total · **Delta** défaut), **granularités superposables** (cases Session/30 min/1 h
en même temps, couleur d'encadré par granularité — leçon 09 : la session se construit de sous-briques
30 min), sous-briques en cache. GDI+ via `OnPaintChart` + `CoordinatesConverter`, `System.Drawing.Common`.

**SMA résolution-indépendante** : paramètre « Résolution de calcul » (défaut 1 h) via
`Symbol.GetHistory` sur un historique fixe, projeté en marches → afficher en 1 min ne change pas
les SMA 50/200 horaires (vérifié à l'écran par l'utilisateur, valeurs identiques 1 h vs 15 min).

**PARITÉ #3 VP mesurée** ([phase3-parity-vp.md](phase3-parity-vp.md)) : **session 100 %, delta
exact (0.0)**, POC/VAH/VAL moyen ~2 pt. 6 barres aberrantes = une vieille session (25/06) au **mur
de profondeur tick** (~2 sem.) : Quantower re-télécharge moins de ticks anciens que notre base n'en
avait gardé → écart au démarrage, converge. Argument concret de la collecte incrémentale. SMA en
résol. fixe : même parité qu'avant. **Reste Phase 3** : EMA/MACD, marqueurs de signaux.

## 2026-07-09 — Phase 3 : parité #2 RSI (Wilder + seed) mesurée

`RSI NQ (14, natif)` déployé : héberge le RSI natif (`BuiltIn.RSI`, RSIMode.Exponential +
MaMode.SMMA = Wilder) en fenêtre séparée + niveaux 30/70, exporte `time,close,rsi`.
`parity_rsi.py` compare à Wilder + Cutler. **Résultat** : Quantower = **Wilder** (écart 7,25 vs
Wilder contre 39,4 vs Cutler). L'écart de 7,25 est **au warmup** (18/06) et **converge à 0**
(zone convergée : max 1,62 = dernière barre partielle, moyen 0,089). **Formule identique ;
différence = seed de warmup**, mesurée. Découverte annexe : historique **horaire** Rithmic remonte
au 13/04 (bien plus profond que le tick ~2 sem.). Implication : warmup suffisant requis en live
(Phase 4). Rapport : [phase3-parity-rsi.md](phase3-parity-rsi.md).

## 2026-07-08 — Automatisation de la collecte (démon dans Quantower)

`NQ Tick Extractor` passe en **démon** : paramètre « Collecte auto toutes les N heures »
(défaut 6 ; 0 = one-shot). `System.Threading.Timer` dans `OnRun`, re-collecte idempotente,
protégée contre le recouvrement (`_busy`+lock) ; `OnStop` libère le timer. Contrainte assumée :
collecte uniquement quand Quantower est ouvert (connexion Rithmic live) → usage quotidien du
challenge + tampon 2 sem. suffisent ; la 1re passe rattrape les jours manqués. Reste : Phase 3
(indicateurs C#). Détails : [extractor/README.md](../extractor/README.md).

## 2026-07-08 — Phase 2 : backtests NQ des 8 stratégies (LEAN) — TERMINÉE

Code dans `../../backtesting/backtests/algorithms/` (module partagé `nq_instrument.py` + 8 algos).
Données : `normalize_ohlcv.py` (candles→format canonique LEAN) + `features_vp.csv` NQ (tick 5 pts)
via `volume_profile_features.py --db/--out` (args ajoutés, rétrocompatibles).

- **Sol de vérité `buyhold_nq`** : équité LEAN = `cash + qté×close×20` au dollar près →
  multiplicateur 20 correctement appliqué par LEAN sur données custom. FeeModel 2 $/contrat/side.
  Sizing en **contrats entiers** + `set_leverage(20)` (1 contrat ≈ 600 k$ notionnel = ~6× sur 100 k$).
- **Tableau NQ** (20 j) : B&H −21,4 %, SMA −13,3 %, MACD −21,9 %, RSI −1,7 %, Bollinger +0,0 %,
  RSI+stop −1,7 %, avancée −6,9 % (dd 8,2 %), **VP +2,9 % (dd 0,9 %) = meilleure**.
- **Écarts vs BTC** : ① levier = vrai risque (NQ −3,5 % prix → −21 % compte) → argument MNQ ;
  ② stops % ne bindent pas (RSI = RSI+stop à l'identique) → recalibrer en ATR/points ;
  ③ frais fixe/contrat ≪ % notionnel ; ④ fenêtre 20 j = indicatif (SMA200 à peine de runway).
  Détails : [phase2-backtests-nq.md](phase2-backtests-nq.md).

## 2026-07-08 — Phase 1 : extracteur ticks NQ → SQLite (déployé)

**Décision** : NQ seul en Phase 1 (ES ajoutable plus tard, schéma le permet ; MNQ inutile en
données — mêmes ticks, ne sert qu'aux ordres Phase 5).

**Schéma frère relu (mesuré sur `F:\data\BTCUSDT-um.db`)** : `trades(trade_id INTEGER PK, ts
INTEGER ms, price REAL, size REAL, side TEXT)` + `_ingested(name,rows,at)` + `_meta(k,v)` +
`idx_trades_ts`. `candles.py` fait `SELECT ts,price,size,side='buy' FROM trades ORDER BY trade_id`
et **suppose l'ordre trade_id = chronologique** (insertion append) → contrainte reprise à l'identique.

**`NQ Tick Extractor` construit et déployé** (`extractor/NqExtractor/`) : stratégie Quantower
(net8.0) référençant `System.Data.SQLite` fourni par Quantower (`Private=false` ; natif
`SQLite.Interop.dll` déjà dans le bin). Télécharge les ticks `Last` via `GetTickHistory`, mappe
`AggressorFlag`→`side`, `Volume`→`size`, `TimeLeft`(UTC)→`ts` ms, insère trié par ts (rowid
chrono). Incrémental idempotent : jours complets marqués dans `_ingested`, jour courant purgé/
réinséré ; backfill borné si base vide. `_meta` : symbol=NQ, exchange=CME, tick_size=0.25,
multiplier=20 (=GetTickCost(1)/TickSize), source=rithmic. Détails : [extractor/README.md](../extractor/README.md).

**RÉSULTAT Phase 1 (1er run 2026-07-08 19:07) — PHASE 1 TERMINÉE :**
- `F:\data\NQ-2026-09.db` (304 Mo) : **6 929 676 ticks**, du **2026-06-18** au 2026-07-08 (jour
  courant partiel non marqué). 20 jours dans `_ingested`. Profondeur Rithmic réelle **≥ 20 j**
  (la sonde Phase 0 avait sous-estimé à ~2 sem.).
- **Vérifs** : schéma + `_meta` conformes ; **0 inversion chronologique** (trade_id↔ts) → hypothèse
  `candles.py` OK ; buy 3,46 M / sell 3,47 M ; **0 ligne invalide** ; prix aux pas de 0,25.
- **Chaîne Python aval OK sans modif** : `candles.py --db NQ-2026-09.db` → OHLCV 337×1H / 91×4H /
  18×D dans `F:\data\ohlcv\NQ-2026-09\` + chandelier `docs/nq-control.html`. L'heure **21:00 UTC
  absente** = pause CME Globex 17:00–18:00 ET → calendrier réel confirmé.
- **Reste** : automatiser la collecte quotidienne (tâche planifiée) — puis Phase 2 (backtests NQ/LEAN).

## 2026-07-08 — Initialisation & lancement Phase 0

**État vérifié :**
- Dossier projet créé de zéro (seul `le cahier des charges` présent).
- DLL `TradingPlatform.BusinessLayer.dll` trouvée : `C:\Quantower\TradingPlatform\v1.145.17\bin\`,
  FileVersion 1.145.17.0, cible **net8.0** (mesuré via `SQLiteUtilities.runtimeconfig.json` du
  même dossier bin — attendu du cahier des charges confirmé).
- Machine : SDK .NET 10.0.301 (compile du net8.0), runtimes 8.0.11/8.0.25/8.0.28 présents.
- `F:\data\` existe (convention de stockage du frère).

**Décisions :**
- POC Phase 0 = console `poc/Phase0Poc` ciblant net8.0, référence BusinessLayer avec
  `Private=false` + résolution runtime des dépendances vers le bin Quantower (dossier `v*`
  le plus récent résolu dynamiquement, à la compilation via `build.ps1` et à l'exécution en C#).
- Démarche mesurée : **dump réflexif de l'API publique d'abord** (types Core/Connection/History/
  ticks), puis code typé écrit d'après le dump — on ne suppose pas l'API, on la lit.

**Mesures Phase 0 (Q1) :**
- Dump réflexif : **840 types publics**. `HistoryItemLast.AggressorFlag` existe
  (`None/Buy/Sell/NotSet`) ; `HistoryType = Bid/Ask/Midpoint/Last/BidAsk/Mark` ;
  `Symbol.GetHistory(HistoryRequestParameters)` + `Connection.HistoryMetaData`
  (`DownloadingStep_Tick/Minute`, `AllowedHistoryTypes…`).
- **`Core.Initialize()` fonctionne hors process Quantower** (console net8.0), à condition de :
  ① resolver d'assemblies vers `bin`, `bin\System`, `bin\runtimes\win\lib\net8.0`
  (sinon `FileNotFoundException` Pkcs 4.0.3.1) ; ② `CurrentDirectory = bin` (vendors sous
  `bin\Vendors\*`). Exceptions first-chance bénignes pendant l'init (Drawing.Primitives/
  Runtime v10) rattrapées en interne.
- **80 connexions** chargées, dont Rithmic sauvegardée (`user=le compte`,
  `server=le serveur Rithmic`, History+MarketData=true).
- **Verrou** : mot de passe Quantower chiffré **non déchiffrable hors process**
  (`FailedToRestorePassword=True`) → `Connect()` = `Fail "Password is empty."`.

**PIVOT d'architecture (retour utilisateur) : on tourne DANS Quantower.** Sur les projets
précédents, lancer Quantower suffisait — la connexion Rithmic y est déjà authentifiée. Décision
verrouillée : le code Phase 0 (mesure) et Phase 1 (extraction) est une `Strategy`/`Indicator`
chargée depuis `C:\Quantower\Settings\Scripts\`. La console externe devient un simple **outil
de diagnostic d'API** (modes `dump`/`type`/`connect`) ; la branche `credentials.local.json`
est conservée pour mémoire mais **abandonnée**.
- Contrat de `Strategy` mesuré par réflexion : surcharges `OnCreated/OnRun/OnStop/OnRemove`,
  logs via `LogInfo/LogError`, paramètres via attribut `[InputParameter]` (supporte `Symbol`).
  Scripts custom = DLL **net8.0**, `Private=false` (plateforme non copiée), déposées dans
  `Settings\Scripts\Strategies\<nom>\` (structure calquée sur les exemples Bootcamp/Orderflow).
- **`Phase0 Measure (NQ)` construite et déployée** (`poc/Phase0Strategy/`) : sonde l'historique
  tick jour par jour (Q2 profondeur), compte les aggressor flags (Q3), moyenne ticks/jour (Q4),
  loggue les specs du contrat, écrit `docs/phase0-measures.txt`. Détails : [phase0-poc.md](phase0-poc.md).

**RÉSULTATS Phase 0 (run 2026-07-08 18:47, Rithmic live) — PHASE 0 TERMINÉE :**
- **Q1** connexion in-Quantower : OK (la stratégie tourne, connexion live).
- **Q2 profondeur** : plus ancien tick `Last` = **2026-06-23** → **~2 semaines**. `tickHistoryTypes=[Last]`
  uniquement (pas de BidAsk historique) ; `stepTick=1h`. ⇒ collecte incrémentale quotidienne CRITIQUE.
- **Q3 aggressor** : **100 % peuplé** (5 359 050 ticks, `none=11` = 0,0002 %). Schéma `side buy/sell`
  du frère transfère. Nuance : `ServerSideTickDirectionAvailable=False` → direction calculée côté
  Quantower (pas flag natif exchange).
- **Q4 volumétrie** : **~412 k ticks/séance** → ~16,5 Mo/jour SQLite (~350 Mo/mois). 1 collecte/jour suffit.
- **Contrat** : `NQ@CME`, front = échéance **2026-09-18** (U/sept), tick 0,25 = **5 $** (`GetTickCost(1)=5`),
  contrats entiers (`MinLot=1/LotStep=1`).
- **Points Phase 1** : `HistoryItemLast` n'expose PAS de `TradeId` → `trade_id` synthétisé, dédoublonnage
  sur `(ts,price,size,side)` + reprise au dernier `ts`. Footprint historique = trade-based (bid/ask par
  niveau seulement en temps réel Level2/DOM). Prêt à attaquer la **Phase 1** (extracteur → SQLite).
