# Journal du projet — décisions datées & mesures clés

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

Code dans `../Backtesting/backtests/algorithms/nq/` (module partagé `nq_instrument.py` + 8 algos).
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
