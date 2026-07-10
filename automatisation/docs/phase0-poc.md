# Phase 0 — POC & mesures de faisabilité

> Statut : **TERMINÉE** (run du 2026-07-08, 18:47 heure locale). Les 4 questions sont
> répondues par mesure via la stratégie `Phase0 Measure (NQ)` tournant dans Quantower
> (connexion Rithmic live). Résultats bruts : [phase0-measures.txt](phase0-measures.txt).
> Tout ce qui suit est **mesuré**, pas supposé.

## Environnement mesuré (2026-07-08)

| Élément | Valeur mesurée |
|---|---|
| DLL BusinessLayer | `C:\Quantower\TradingPlatform\v1.145.17\bin\TradingPlatform.BusinessLayer.dll` |
| Version fichier | 1.145.17.0 · `Core.CurrentVersion` = 1.145.17 |
| Cible .NET | **net8.0** (mesuré via `*.runtimeconfig.json` du bin ; pas de deps.json pour la DLL) |
| SDK/runtime machine | SDK .NET 10.0.301 ; runtimes NETCore 8.0.11/8.0.25/8.0.28 présents |
| Résolution du bin | dynamique : dossier `v*` au plus haut numéro de version (`build.ps1` + `QuantowerLocator`) |

**Types publics de l'API** : 840 (dump réflexif → `poc/Phase0Poc` mode `dump`). Types clés
repérés pour la suite : `Core`, `ConnectionsManager`/`Connection`/`ConnectionInfo`,
`Symbol.GetHistory(HistoryRequestParameters)`, `HistoricalData`/`IHistoryItem`,
`HistoryItemLast` (porte **`AggressorFlag`** + `Price`/`Size`/`TradeId`), `HistoryType`
(`Bid, Ask, Midpoint, Last, BidAsk, Mark`), `AggressorFlag` (`None, Buy, Sell, NotSet`),
`Integration.HistoryMetadata` (`DownloadingStep_Tick/Minute/…`, `AllowedHistoryTypes…`).

## Q1 — Le BusinessLayer se connecte-t-il à Rithmic hors du process Quantower ?

**Réponse tranchée : NON en pratique → on tourne DANS Quantower.** `Core.Initialize()`
démarre bien en console, mais le mot de passe Rithmic stocké par Quantower est chiffré et
**non déchiffrable hors de son process**. Or l'utilisateur lance Quantower et se connecte
normalement à Rithmic (voie éprouvée sur les projets précédents) : la connexion est alors déjà
vivante et authentifiée dans ce process. **Décision** : le code de mesure (Phase 0) et
d'extraction (Phase 1) tourne **dans Quantower** sous forme de `Strategy`/`Indicator` chargée
depuis `C:\Quantower\Settings\Scripts\` — c'est exactement le « fallback » du cahier des charges, retenu
comme voie principale. La console externe reste un **outil de diagnostic** (réflexion d'API,
liste des connexions) — modes `dump` / `type` / `connect`.

Détail de ce qui a été mesuré côté standalone (preuve du verrou) :

1. `Core.Instance` + `Core.Initialize()` **fonctionnent** dans une console standalone, à
   condition de :
   - poser un resolver d'assemblies vers `bin\`, `bin\System\`, `bin\runtimes\win\lib\net8.0\`
     (sinon `FileNotFoundException` sur `System.Security.Cryptography.Pkcs 4.0.3.1`) ;
   - fixer `Environment.CurrentDirectory` sur le bin Quantower (les vendors sont sous `bin\Vendors\*`).
   - Exceptions *first-chance* bénignes pendant `Initialize` (`System.Drawing.Primitives`/
     `System.Runtime` v10 introuvables) : rattrapées en interne, l'init aboutit.
2. **80 connexions** chargées depuis `settings.xml`, dont la connexion **Rithmic** sauvegardée
   (`ConnectionId = Rithmic-Rithmic-Default-Rithmic`, `IsFavourite=true`). `RithmicVendor.dll`
   présent sous `bin\Vendors\RithmicVendor\`.
3. `CreateConnection(info)` construit l'objet `Connection` ; `user=le compte`,
   `server=le serveur Rithmic`, `History=true`, `Market data=true` sont lus automatiquement.
4. **Verrou mesuré** : le mot de passe stocké (`settings.xml`, base64 chiffré) a
   `FailedToRestorePassword = True` hors process → `Connect()` renvoie
   `State=Fail, Message="Password is empty."`. Le chiffrement Quantower n'est pas réversible
   dans une console minimale.

La branche « config locale gitignorée » (`credentials.local.json` + `SettingItemPassword`)
existe dans la console pour mémoire, mais **n'est plus la voie retenue** : inutile puisque la
connexion vit déjà dans Quantower.

## Contrat NQ (mesuré)

`Id=NQ@CME`, `Type=Futures`, `Root=NQ`, front = **échéance 2026-09-18** (contrat U/septembre).
`TickSize=0.25` · **`GetTickCost(1)=5` → 5 $/tick confirmé** (multiplicateur 20 $/pt) ·
`LotSize=1 / MinLot=1 / LotStep=1` → **contrats entiers** confirmés. `NotionalValueStep=1`.

## Q2 — Profondeur d'historique tick : **~2 semaines** ✅

Plus ancien tick `Last` réellement servi = **2026-06-23** (13 séances de données sur la fenêtre
sondée). Au-delà, Rithmic ne renvoie rien → **profondeur ≈ 2 semaines**, conforme à l'attendu.

> **Correction (Phase 1)** : la collecte réelle a récupéré depuis le **2026-06-18** (20 jours,
> limite de backfill choisie) — la sonde Phase 0 s'était arrêtée trop tôt à 06-23. La profondeur
> Rithmic est donc **≥ 20 jours** (probablement plus ; à re-sonder avec un backfill plus large si
> besoin). Conclusion inchangée : collecter quotidiennement.
`HistoryMetadata` : `tickHistoryTypes=[Last]` **uniquement** (pas de ticks BidAsk historiques),
`stepTick=1h` (taille de chunk de téléchargement). Le calendrier CME est cohérent (04/07 = 0 tick
= Independence Day ; dimanches 14–19 k ticks = ouverture Globex 18:00 ET ; 03/07 demi-séance 75 k).

⇒ **Conséquence stratégique majeure** : chaque jour non collecté est **irrécupérable**. La
collecte incrémentale quotidienne (Phase 1) doit démarrer **au plus tôt** et être automatisée.

## Q3 — Aggressor flag : **100 % peuplé** ✅ (excellent)

Sur **5 359 050** ticks : `buy=2 677 301`, `sell=2 681 738`, `none=11` (**0,0002 %**).
Le côté agresseur (buy/sell) est **quasi intégralement renseigné** → delta/footprint directement
constructibles, et le **schéma SQLite du frère** (`side TEXT buy/sell`) transfère tel quel.
Nuance mesurée : `ServerSideTickDirectionAvailable=False` → la direction est **calculée côté
Quantower** (pas un flag natif exchange). À garder en tête pour la parité (notre `side` = la
classification Quantower, pas forcément celle d'une autre source).

## Q4 — Volumétrie : **~412 k ticks/jour** ✅

Moyenne **412 234 ticks/séance** (13 séances). Estimation SQLite **~16,5 Mo/jour** (40 o/tick)
→ ~350 Mo/mois : **très gérable**. Fréquence de collecte : 1×/jour suffit largement (marge
confortable sous les 2 semaines de profondeur).

## Points ouverts créés par ces mesures (pour Phase 1)

- **Footprint** (« à trancher » n°1 du cahier des charges) : l'historique ne fournit que des ticks `Last`
  (pas de bid/ask par niveau). Un footprint historique sera donc **trade-based** (volume au prix
  classé par agresseur), exactement comme le pipeline crypto → `features_vp` transfère. Le
  bid/ask par niveau n'existe qu'en **temps réel** (Level2/DOM), à capturer en live si un jour requis.
- **Clé de trade** : `HistoryItemLast` **n'expose pas** de `TradeId` (contrairement au tick temps
  réel `Last`). Le `trade_id` du schéma sera **synthétisé** (index d'insertion) et le
  dédoublonnage incrémental se fera sur `(ts, price, size, side)` + reprise au dernier `ts`.

## Verdict Phase 0

Faisabilité **validée sur toute la chaîne** : connexion (in-Quantower), profondeur (~2 sem.),
aggressor (100 %), volumétrie (~412 k/j), specs contrat (tick 5 $, contrats entiers). L'ordre
d'architecture est confirmé. **On peut attaquer la Phase 1** (extracteur incrémental NQ → SQLite).
