# Reprise — état au 2026-07-16 (fin de 3e session) : TOUT FONCTIONNE SUR FLUX RÉEL — NQ + ES par Quantower/Rithmic, IBKR en témoin dégradé avec bandeau « aucun abonnement » ; décision : PROJET 100 % QUANTOWER (abonnement CME REFUSÉ) ; **COMMIT + PUSH FAIT le 2026-07-16** (2 dépôts, voir la fin) ; textes IBKR RECADRÉS (limites présentées comme temporaires, l'utilisateur signale qu'il s'abonnera bientôt) ; reste les CAPTURES DÉMO à remplacer (utilisateur)

> **Session du 2026-07-16 en une ligne** : la panne « IBKR n'affiche rien » = handler weakref
> (piège 13) ; IBKR est bien en DIFFÉRÉ (retard MESURÉ 11,5 min, piège de la mesure circulaire) ;
> décision « tout Quantower » ; zoom, cadrage sans carnet, DOM aligné au pixel, bandeaux,
> détection de gel, horaire CME — pièges 13 à 23 ; pont C# durci et DÉPLOYÉ (validé : les 2
> instances servent `tick 0.25`) ; ES branché (2e instance, port 5556) ; Apex Legacy 250k sans
> add-on → la thèse du site est PROUVÉE (pop-ups DOM-Surface = la preuve en image).

> Ce fichier reprend la convention de `crypto/REPRISE.md`. Il est écrit pour qu'une nouvelle
> conversation reparte **exactement** d'ici, sans redécouvrir ce qui a déjà été mesuré.
> Mémoire associée : `indices-affichage-refonte-quantower`, `indices-piliers-a-faire`,
> `ialexmg-restructure-crypto-nq`, `powershell-ps1-bom-cp1252`.

---

## Ce qu'on fait et pourquoi

Demande de l'utilisateur (2026-07-15, ouverture de session) :

> « on va travailler sur indicesBoursiers, mais on va garder le même style que crypto. Crypto
> est bien avancé et **on ne va pas lui toucher**. […] historiques : je ne suis pas abonné à
> ibkr donc je n'ai pas accès aux historiques. Par contre, on peut avec Rithmic-Qt. Pour les
> Trades/orderflow des visualisations, Qt les fournit avec un abonnement payant mais je crois
> que **nous avons accès aux données gratuitement via .dll**. […] on va donc devoir retravailler
> le projet IndicesB. pour **reconstruire les 3 vues dans l'interface comme dans crypto**. »

Les « 3 vues » = les 3 sections du pilier affichage, des deux côtés : **DOM & Heatmap**,
**Footprint**, **Trades / orderflow**.

**Son intuition sur la .dll était juste** — vérifié par réflexion sur
`TradingPlatform.BusinessLayer.dll` v1.146.14, **puis par le compilateur**. L'abonnement payant
Quantower ne verrouille que **ses propres panneaux** ; les événements bruts du BusinessLayer
sont ouverts. C'est la brèche par laquelle passe déjà `historique/NqExtractor`.

---

## LES FAITS MESURÉS (ne pas les redécouvrir — ils ont coûté du temps)

### L'API Quantower (réflexion sur la DLL, v1.146.14)

| Ce dont on a besoin | Ce que l'API donne |
|---|---|
| Trades + côté agresseur | `Symbol.NewLast` → `Last { Price, Size, AggressorFlag, TradeId, Time }` |
| Carnet L2 live | `Symbol.NewLevel2` → `Level2Quote { Price, Size, Id, Closed, PriceType }` + `DOMQuote` |
| Carnet agrégé (ce que veut `FlowStore.add_book`) | `Symbol.DepthOfMarket.GetDepthOfMarketAggregatedCollections(GetLevel2ItemsParameters{AggregateMethod.ByPriceLVL, LevelsCount})` → `Bids/Asks: Level2Item[]` |
| Top of book indépendant | `Symbol.Bid/Ask/BidSize/AskSize` (flux de cotation) |
| Historique | `Symbol.GetHistory` / `GetTickHistory`, `HistoryType = Bid\|Ask\|Midpoint\|Last\|BidAsk\|Mark` |

- ⚠️ **L'abonnement se déclenche en ATTACHANT le handler.** `SubscribeAction(SubscribeQuoteType)`
  est **interne** à la plateforme ; seuls `add_NewLast` / `add_NewLevel2` / `add_NewQuote` sont
  publics. Sans handler `NewLevel2` attaché, `DepthOfMarket` **se vide**. Sans `NewQuote`,
  `Symbol.Bid/Ask` restent à **zéro** (piège rencontré : on conclut n'importe quoi).
- ⚠️ `Symbol.TickSize` est une **propriété**, pas une méthode. `Exchange.ExchangeName`, pas `.Name`.
- ⚠️ Horodatage = `MessageQuote.Time` (hérité par `Last`/`Quote`/`Level2Quote`/`DOMQuote`).
- ⚠️ **Aucun historique L2** (`HistoryType` ne le connaît pas) → la heatmap ne peut grandir
  qu'en enregistrant le live. Exactement la contrainte de crypto (`books.db` live-only) : la
  symétrie tient toute seule.
- ⚠️ **Le pont : « obligatoire » est TROP FORT — la vraie raison est le SECRET.** Le
  commentaire de `NqFeedStrategy.cs` disait « aucun pythonnet ne peut se connecter tout
  seul » : **faux**, et démenti par le dépôt lui-même. `automatisation/poc/Phase0Poc`
  (docs/phase0-poc.md, Q1) a MESURÉ que `Core.Instance` + `Core.Initialize()` **marchent**
  hors process (resolver d'assemblies vers `bin\`, `bin\System\`, `bin\runtimes\win\lib\net8.0\`
  + `CurrentDirectory` sur le bin), que **80 connexions** de `settings.xml` se chargent dont
  Rithmic, et que user/serveur se restaurent seuls. **Seul verrou** : le mot de passe stocké
  est chiffré, `FailedToRestorePassword=True` hors process → `Connect()` rend
  `State=Fail, "Password is empty."`.
  → La sortie EXISTE et est **déjà codée** : `Phase0Poc` mode `rithmic` lit un
  `credentials.local.json` (gitignoré) et injecte le mot de passe **en clair** via
  `SettingItemPassword`. **JAMAIS validée de bout en bout** (abandonnée avant, « inutile
  puisque la connexion vit déjà dans Quantower »).
  ⚠️ **2 coûts à peser avant d'y retourner** : (1) un secret **en clair sur le disque** ;
  (2) **risque NON MESURÉ de conflit de session Rithmic** — en général une seule session par
  compte, donc se connecter en standalone pendant que Quantower tourne pourrait éjecter l'un
  des deux. **C'est LA question à trancher en premier**, et elle est gratuite à poser.
  ⚠️ **Le mot de passe appartient à l'utilisateur : ne jamais le saisir/lire à sa place.**

### La sonde Phase 0 (NQ@CME, 2 runs de 60 s, 2026-07-15 14:40 et 14:53 ET)

- **`LEVEL 2 SERVI PAR RITHMIC : OUI`** — 239 snapshots, **0 vide**. Aucun abonnement en plus.
- **Agresseur : couverture 100 %** (`None` 0, `NotSet` 0) → **le footprint NQ est EXACT**. Ça
  enterre la dette « règle du tick » que `affichage/docs/conception.md` traîne depuis IBKR.
- **`retard de cotation = 0 s`** → temps réel. Décalage d'horloge +352 ms (latence, pas de piège
  de fuseau type UTC+8).
- **Pas de `TradeId`** en live → dédup par `(ts, prix, taille)` impossible/interdite (voir plus
  bas). Cohérent avec l'historique Rithmic (README de `NqExtractor` : « pas de TradeId → rowid »).
- **Flux L2 = MBP (par niveau de prix), PAS MBO.** ⚠️ La 1re version de la sonde disait « MBO » :
  **heuristique fausse, corrigée**. La preuve : **2912 fermetures pour 422 Id distincts** — en
  MBO chaque fermeture retire un ordre unique, donc les Id seraient ≥ aux fermetures. Moins d'Id
  que de fermetures = Id réutilisés = niveaux de prix. **Trancher MBO/MBP par les FERMETURES,
  jamais par le volume d'Id** (les niveaux dérivent avec le prix).
- **Cadences : ~9 trades/s, 361 à 472 updates L2/s.** → le pont **ne relaie pas** le L2 brut.
- **Profondeur du carnet ≈ 210 niveaux/côté.** Demander 30 → on obtient 30 ; demander 200 → on
  obtient 200 (toujours plafonné). Mais **le nombre d'Id distincts ne dépend PAS de la demande**
  (il vient du flux, pas de l'agrégation) et n'a pas bougé : **434** à 30 niveaux, **422** à 200.
  C'est *lui* la vraie mesure du fond. Inutile de resonder à 500.
- **Ordre des niveaux VÉRIFIÉ** : `GetDepthOfMarketAggregatedCollections` rend **meilleur en
  premier** (bids décroissants 49/49, asks croissants 49/49). C'est l'hypothèse de
  `FlowStore`/`FlowPanel` : elle tient.

### L'anomalie du spread — RÉSOLUE, aucun bug

Le carnet montrait un spread de 3 ticks et 1-4 contrats en haut du livre : suspect pour du NQ
front. Piste écartée : `ImplicitOrderBookType` vaut déjà `Combined` par défaut (les ordres
impliqués **sont** inclus). Contre-contrôle décisif : `qb`/`qa` (`Symbol.Bid/Ask`, flux de
cotation, chemin **indépendant** de l'agrégation) → **strictement identiques** au 1er niveau du
carnet (29701,25×2 / 29702,50×4). L'agrégation ne perd rien ; **le spread large est réel** :
0,75 à 15:05 ET (séance régulière) contre 1,25 à 16:14 ET, après la clôture du cash de 16:00.
⚠️ **Ne jamais juger la qualité du flux hors 09:30-16:00 ET** — le carnet post-clôture est
légitimement mince. Séance CME : dim. 18:00 ET → ven. 17:00 ET, coupure quotidienne 17:00-18:00.

---

## 🔥 2026-07-16 — LE RITHMIC VIENT D'APEX, ET ÇA CASSE L'ARGUMENT DU SITE

L'utilisateur : **« Rithmic vient de mon compte Apex »** (Apex Trader Funding, prop firm). Fait
capital, jamais su jusqu'ici — il change la lecture de TOUT le pilier. Source : la page d'aide
d'Apex « Rithmic Trading Tools/Add-ons » (403 en WebFetch → lue via le navigateur intégré).

- **L'add-on « Market Depth » d'Apex existe : 10 $/exchange/mois** (30 $ les 4). Sa doc dit que
  sans lui on n'a que **le haut du carnet** (« rather than just the Best Bid/Best Ask for the
  top-of-book »).
- ❌ **J'EN AI DÉDUIT « L'UTILISATEUR LE PAIE, DONC LE SITE MENT » — RETIRÉ, C'ÉTAIT FAUX DE
  MÉTHODE.** Il a répondu : **« je n'ai JAMAIS payé pour Rithmic add-ons »**. Il paie (payait)
  **100 $/mois à QUANTOWER** pour le forfait *Volume Analysis*. J'avais déduit d'une doc
  publique un fait sur SON compte, puis écrit « le site est faux » comme établi. **Il est la
  source d'autorité sur son compte, pas la page d'aide d'Apex.** Même faute que le « temps
  réel 1,8 s » : conclure sans mesurer.
- ⚠️ **NE PAS CONFONDRE LES DEUX PAIEMENTS — c'est le cœur du sujet** :
  - **Quantower ~100 $/mois (*Volume Analysis*)** = les **PANNEAUX** (footprint, DOM,
    indicateurs d'orderflow) = du **LOGICIEL**. C'est CE verrou que la .dll contourne, et
    c'est la trouvaille fondatrice du projet.
  - **Apex 10 $/mois (*Market Depth*)** = le **DROIT À LA DONNÉE** (carnet L2 CME).
  Les deux n'ont **rien à voir**. L'add-on à 10 $ n'« offre pas la même chose » que le forfait
  à 100 $.
- ✅ **TRANCHÉ PAR L'UTILISATEUR (2026-07-16) — LE SITE A RAISON, ET C'EST MAINTENANT PROUVÉ.**
  Son état de compte, confirmé par lui : **AUCUN abonnement Quantower**, **AUCUN add-on Rithmic
  Apex**, forfait **« Legacy 250k Rithmic »** (promo). Et le pont sert malgré tout **100×100
  niveaux + agresseur 100 %**. La thèse du projet n'est donc plus une déduction tirée de la
  réflexion sur la DLL : elle est **vérifiée sur un compte réel, sans le moindre abonnement**.
  🎯 **LA PREUVE EN IMAGE, à mettre sur le site** : Quantower lui affiche des **pop-ups
  réclamant une licence pour le DOM-Surface** — pendant que le pont lit **le même carnet L2**
  sans rien payer. La plateforme verrouille SON panneau, pas la donnée dessous. C'est
  l'illustration littérale de l'argument, et elle vient de la plateforme elle-même.
  ⚠️ **NUANCE À ÉCRIRE (une phrase, pas une réécriture)** : c'est vrai **pour son forfait
  Legacy en promo**. La doc d'Apex vend un add-on « Market Depth » à 10 $/mois et annonce « sans
  lui → haut du carnet seulement » : **un lecteur qui ouvre un compte Apex aujourd'hui pourrait
  devoir le payer.** Ne pas laisser croire que le L2 est gratuit pour tout le monde — dire d'où
  vient LE SIEN. (Piste probable, non confirmée : les comptes **Legacy** incluent la donnée
  non-professionnelle dans le prix du forfait — la doc d'Apex mentionne un traitement à part
  pour eux, mais sur le statut pro/non-pro, pas explicitement sur la profondeur.)
- ✅ **LA QUESTION DES 2 SESSIONS EST RÉGLÉE** (ouverte depuis 3 sessions) : Apex **VEND** une
  **2e session de login à 30 $/mois**. Donc **1 seule par défaut**, et le multi-session est
  possible mais **payant**. Le souvenir de l'utilisateur (Quantower + NinjaTrader ensemble) est
  cohérent : c'est exactement l'usage décrit par Apex (« Bookmap pendant que vous tradez sur
  NinjaTrader8 »). **Conséquence pour la voie « Rithmic direct » : un client Python tournant à
  CÔTÉ de Quantower consomme une 2e session → 30 $/mois, ou il entre en conflit.**
- ⏰ **PIÈGE DE CALENDRIER, à connaître** : les deux add-ons **expirent le DERNIER JOUR du mois
  à 17:00 ET** et doivent être **rachetés** début de mois. Donc **le 1er de chaque mois, la
  heatmap peut mourir** sans que rien ne soit cassé dans le code. ⚠️ **Ce n'est PAS la cause du
  gel du 2026-07-16** (on est le 16, et le L2 marchait la nuit précédente) — mais c'est la
  1re chose à vérifier devant un carnet absent en début de mois.
- ❓ **API Rithmic + Apex : NON TRANCHÉ.** La doc d'Apex ne dit rien du dev kit R|API+. Rithmic
  exige « request a dev kit and pass conformance review » avant des identifiants de production.
  Pour un compte de prop firm, c'est **Apex** qui contrôle. **Question à leur poser avant
  d'investir une heure dans la voie B.**

## DÉCISION DE L'UTILISATEUR (2026-07-16) — L'ABONNEMENT CME EST REFUSÉ, PAS REPORTÉ

> « on va rester avec Quantower et continuer tout le projet juste avec quantower »

**Le dossier « abonnement CME » est CLOS.** Il traînait depuis 3 sessions comme « reporté », faute
de chiffre. Le chiffre est là, la réponse est non — **ne plus reposer la question**.

- **Chiffrage (2026-07-16)** : chez le CME, **deux produits SÉPARÉS** — L1 ≈ **1-3 $/mois**
  (haut du carnet **+ les transactions** → réglerait le retard de 11,5 min ET la couverture
  de 4 %, via `reqTickByTickData`) et L2/`/DEEP` ≈ **15 $/mois** (la profondeur → heatmap +
  DOM). Total **~16-18 $ US/mois** non-professionnel ; **140 $/exchange** en professionnel.
  ES et NQ sont tous deux au CME → **un seul** exchange, pas le forfait à 4.
  ⚠️ **Chiffres NON confirmés à la source** : la table d'IBKR est fermée (403 + widget JS ;
  ne pas accepter leurs cookies à la place de l'utilisateur). 3 sources secondaires divergent
  (3 $ vs 1 $ pour le L1) et on ignore si le L2 d'IBKR **inclut** le L1. **La seule source qui
  fasse foi : le Portail Client de l'utilisateur** (Paramètres → Abonnements aux données de
  marché), gratuit à consulter. N'y aller que s'il redemande.
- 🔑 **Nos 3 erreurs pointent 2 permissions DISTINCTES** (utile si le sujet revient) :
  354 « …/**DEEP** » = le carnet manque ; 10189 « No market data permissions for **CME FUT** »
  (tick-by-tick, refus sec) et 10167 (repli sur le différé) = la permission **temps réel de
  base** manque. ⚠️ Cohérent avec 2 produits séparés, mais **ne le PROUVE pas** : sans aucun
  abonnement, les 3 erreurs tombent de toute façon.
- 🔥 **MÊME ABONNÉ, IBKR NE RATTRAPERAIT PAS QUANTOWER** (doc TWS API vérifiée) :
  `reqTickByTickData("AllLast")` rend prix/taille/heure/exchange — **PAS le côté agresseur**.
  Il resterait INFÉRÉ (règle du tick) là où Rithmic le FOURNIT sur 100 % des trades. L'angle
  « 1 marché, 2 accès » survit donc à l'abonnement : il se déplace du « gratuit vs payant »
  vers « ce que même la donnée payante ne dit pas ».

**Ce que la décision change — et ce qu'elle NE change PAS :**
- ✅ **Quantower/Rithmic = LA source du projet**, pour tout le travail à venir.
- ✅ **L'accès IBKR RESTE dans le menu** (choix explicite du même jour : « le garder comme
  témoin »), et il **FONCTIONNE** depuis la correction du weakref. Il ne coûte rien : il tourne
  en parallèle, dégrade seul, et c'est **la démonstration** du pilier. **Ne pas le retirer sans
  un ordre explicite** — ce serait détruire du code qui marche ET l'angle éditorial.
- ⛔ **On n'investit plus une heure dans IBKR.** `reqTickByTickData` est définitivement enterré
  (il exige l'abonnement refusé). Le mode dégradé est l'état FINAL, pas une étape.
- 📄 **Le pilier historique** : IBKR y était le « candidat » pour la contre-épreuve — il est
  mort aussi (même facture). La 2e source d'historique reste **à retrouver ailleurs**
  (Databento, CME DataMine…) ou l'écart « source unique » reste assumé. Voir
  [[indices-piliers-a-faire]].

## DÉCISIONS DE L'UTILISATEUR (2026-07-15) — ne pas re-litiger

1. **IBKR gardé en mode DÉGRADÉ** (pas retiré) : footprint + trades en différé, heatmap/DOM
   vides. L'angle éditorial « 1 marché, 2 accès » survit — mais ⚠️ **les rôles s'inversent** :
   Quantower devient le flux de l'écran, IBKR le secondaire.
2. **PyQt5 → PySide6**, comme crypto. Le conflit apparent avec `ib_insync` (`util.useQt()`
   ignore PySide6) **se dissout** : le flux IB tournera dans **son propre thread + sa propre
   boucle asyncio** et n'écrira que dans le `FlowStore` — le binding Qt ne le concerne plus.
   C'est déjà l'architecture de crypto (connecteurs en threads → hub → GUI sur QTimer).
3. **Persistance disque** comme crypto (fait, phase 4).
4. **Résolution** : indices **GARDE** son échelle 1s→5m. C'est **CRYPTO** qui doit descendre
   sous la minute → **tâche séparée** (chip `task_e01d97bb`), voir « Exception » plus bas.
5. **Menu `Accès`** (Quantower / IBKR / Démo) = analogue du menu `Exchange` de crypto →
   **les 3 sources tournent EN PARALLÈLE**, le menu ne change que la clé lue. Implique des
   stores par `(source, symbol)` et **absorbe la phase 3**.
6. **Ordre : phase 4 (disque) AVANT l'interface**, pour que le bouton « Aller au » ait un sens.

---

## FAIT — phases 0, 1, 2, 2b, 4

### Phase 0 ✅ — la sonde
`affichage/NqFeed/NqFeedProbeStrategy.cs` (stratégie **« NQ Feed Probe »**). Ne produit rien :
mesure. Défauts : 60 s, 200 niveaux, snapshots 250 ms. Rapport = une ligne par message.

### Phase 1 ✅ — le pont (validé sur flux réel)
- **C#** : `affichage/NqFeed/NqFeedStrategy.cs` (stratégie **« NQ Feed »**). Serveur TCP
  **127.0.0.1:5555** (jamais `IPAddress.Any` — c'est un flux de marché), **NDJSON**.
  Pousse les **trades** au fil de l'eau + des **snapshots agrégés** à `SnapshotMs` (250 ms).
  **Ne relaie PAS** les 472 updates L2/s : `FlowStore` throttle de toute façon → ~100× de débit
  pour rien. Handler `NewLevel2` attaché **vide** (il tient l'abonnement), `NewQuote` idem.
  **Une file bornée + un thread d'écriture PAR client** : les handlers ne font qu'enfiler, on
  **JETTE** si ça déborde plutôt que de bloquer un thread du moteur de marché.
- **Python** : `affichage/quantower_feed.py` — thread client, reconnexion à backoff plafonné
  (forme d'un connecteur crypto). **stdlib seule.**
- Messages : `hello` (symbol, exchange, tick, lot_size, levels, snapshot_ms) · `trade`
  (ts, p, s, side) · `book` (ts, **qb/qa/qbs/qas** = top of book du flux de cotation, b, a).
- **Mesuré en vrai** : 8,8 trades/s · 4,0 carnets/s · **80 reçus / 80 stockés** · 100×100 niveaux.

### Phase 2 ✅ — PySide6 (le port a été QUASI GRATUIT)
`flow_view.py` (623 l.), `ui.py`, `demo_feed.py` importaient déjà tout depuis **`pyqtgraph.Qt`**,
qui résout le binding lui-même : aucun `pyqtSignal`/`pyqtSlot`/`sip`. Seuls vrais changements :
`app.exec_()` → `app.exec()` + docstrings. **Leçon : passer par `pyqtgraph.Qt` rend le code
transférable entre les 2 dépôts.** Validé `QT_QPA_PLATFORM=offscreen` sous `crypto-agg` avec le
flux Rithmic réel : 5 `refresh()` complets (heatmap+scatter+bbo+footprint+DOM), 0 exception.
- **`config.DEMO_MODE` (booléen) → `config.SOURCE`** = `"quantower"` (défaut) | `"ibkr"` |
  `"demo"`, dispatché par `RUNNERS` dans `main.py`.
- `config.QT_FEED_PORTS = {"NQ": 5555, "ES": 5556}` — **une instance de « NQ Feed » PAR symbole**,
  chacune sur son port. Symbole sans port = onglet vide, sans gêner les autres.
- `environment.yml` (env **`indices-flow`**) calqué sur le frère ; `ib_insync` **sorti** des
  dépendances de base (la source quantower n'utilise que la stdlib).

### Phase 2b ✅ — L'INTERFACE COMME CRYPTO (validée en headless, 62 assertions)
Toute la barre du frère est portée : **boutons** de symbole (plus d'onglets, **un seul**
`FlowPanel`), **`Affichage ⚙`**, **`Aller au`**, **`● Live`** (bascule qui reflète
`view.follow`), **`✕ Quitter`**, menu **`Accès`**, thème `#0d1117`, `showMaximized`.
`Dislocation ⚙` et `Marché` Futures/Spot restent **SANS OBJET**. Résolution : **1s→5m gardée**.
- ⚠️ **Le REPRISE se trompait** : `FootprintItem` d'indices avait **DÉJÀ** `show_bars` /
  `show_poc` / `show_numbers` / `show_header`, câblés dans `_generate` et `paint`. Rien à
  ajouter. Ce qui manquait vraiment : l'API de couches, et les réglages Trades.
- `flow_view.py` : `_layers`/`_layer_order`/`_apply_z_order`, `set_layer_visible|opacity`,
  `move_layer`, `set_dom_visible`, `set_footprint_option`, `go_live`/`goto`/`set_resolution`,
  `set_symbol(symbol, store)`. `_build_options` supprimé (la barre vit dans `ui.py`).
- **`min_size` / `auto_filter` / `dot_scale` étaient ABSENTS** (`DOT_SCALE` n'était qu'une
  constante de module) → portés de crypto avec l'hystérésis du seuil. **Mesuré** : à 20 min
  de plage, 28 944 trades → **459 points** ; seuil stable à 17,0 sur 5 images ; `HARD_CAP`
  plafonne à 6 000 quand on coupe Auto. ⚠️ **À 180 s (le défaut) le filtre Auto ne mord
  PAS** (cap 1200 > ~740 points) : un test à la plage par défaut passe au vert sans rien
  prouver — il faut DÉZOOMER pour l'exercer.
- ⚠️ **`follow` ne repassait JAMAIS à False** : rien ne connectait `sigRangeChangedManually`.
  Sans ça, ni `Aller au` ni la bascule `● Live` n'ont de sens. Branché dans `FlowPanel`.
- `controls.py` (neuf) : port de `crypto/gui/controls.py`, **une seule** substitution
  (`view.fp.item` → `view.fp`), via `pyqtgraph.Qt` pour rester transférable.
- **Menu `Accès` + phase 3 absorbée** : `main.py` MONTE les 3 accès (`BUILDERS`, plus
  d'aiguillage `RUNNERS`), stores par **`(accès, symbole)`**, tous alimentés en continu.
  `config.ACCESS` = table du menu ; `config.SOURCE` = accès sélectionné au démarrage.
  IBKR tourne dans **son thread + sa boucle asyncio**, `util.useQt()` jamais appelé.
  Chaque accès dégrade SEUL (pont fermé / TWS éteint / `ib_insync` absent) : **vérifié**,
  l'app monte et rend malgré les deux services absents, fermeture en 2,5 s.
- ⚠️ Un accès non monté fabrique un **store VIDE** plutôt que de laisser la vue sur le flux
  précédent : afficher les chiffres d'un accès sous l'étiquette d'un autre serait un
  mensonge — or c'est précisément ce que ce tableau de bord démontre (d'où vient la donnée).
- 🐛 **BUG CORRIGÉ (signalé par l'utilisateur, 2026-07-15)** : le store vide ne suffisait
  PAS. `refresh` sortait en avance sur un store muet et **laissait les pixels du flux
  précédent** : Démo → Quantower (pont fermé) affichait **1226 points de la démo** sous
  l'étiquette « Quantower ». → `FlowPanel._clear()`, appelé par `refresh`.
  ⚠️ **Le test le prétendait couvert** : il vérifiait `store.source`/`len(trades)` —
  **le store, jamais la VUE**. 3e faux vert de la session (après « 739 → 739 » et le tick
  « == 0 or > 0 »). **Assertion renforcée : compter les points DESSINÉS.**
- 🐛 **Manque corrigé dans la foulée** : l'app ne disait RIEN. Un pont fermé = écran noir
  muet, indiscernable d'une panne (c'est ce qui a fait croire à un bug). → **ligne d'état**
  sous la barre (`MainWindow.status` / `_status_text`), qui lit `last_error` du flux :
  « ○ Quantower · NQ — aucune donnée. Le pont ne répond pas sur 127.0.0.1:5555 — la
  stratégie « NQ Feed » tourne-t-elle dans Quantower, en Working ? ». Les flux portent une
  clé `.key = (accès, symbole)` pour ça ; `IbkrFeed.key = ("ibkr", None)` = tous symboles.

### Phase 4 ✅ — le disque (`affichage/backend/`)
`trade_archive.py` · `book_archive.py` · `recorder.py` (thread d'écriture unique, file bornée,
commits groupés, purge de rétention) · `book_reader.py` (lecture bornée + hors thread Qt).
- **Le disque entre dans la vue en UN SEUL point** : `FlowStore.visible_books` complète la
  mémoire par `BookReader`. **`FlowPanel` n'a pas une ligne de changée** et ignore que l'archive
  existe. Vérifié : mémoire forcée à 6 s → le disque rend **34 colonnes au-delà**, chronologie
  croissante, 100×100 intacts, 1er appel non bloquant.
- Ordre d'arrêt impératif (`Storage.close`, appelé **en dernier** dans `ui.closeEvent`) : couper
  les sources → arrêter reader/recorder → fermer les archives.

---

## ⚠️ LES PIÈGES (tous mesurés — les réintroduire coûterait cher)

1. **Trades JAMAIS dédupliqués ; carnets TOUJOURS dédupliqués.** Crypto a
   `PRIMARY KEY(market,symbol,trade_id)` car son backfill REST recouvre le live. Ici : pas de
   `TradeId`, pas de backfill, et surtout **deux trades de 1 lot au même prix/côté/ms sont RÉELS
   et DISTINCTS** — une PK les fusionnerait et **fausserait le volume**, ce que le footprint est
   censé mesurer. `trades` est donc en ajout pur (rowid). `snapshots` a bien
   `PRIMARY KEY(source,symbol,ts)` (deux photos du même état au même instant = la même chose).
   Testé : 3 trades identiques → 3 lignes ; 2 snapshots au même ts → 1 ligne.
2. **Double-throttle → 29 % de snapshots perdus EN SILENCE.** Le pont échantillonne à 250 ms et
   `FlowStore.add_book` re-throttlait à `config.SNAPSHOT_MS`=250 : la gigue réseau en jetait 7
   sur 24. Corrigé par **`add_book(..., throttle=False)`** — c'est l'APPELANT qui sait si sa
   source est déjà échantillonnée. IBKR garde `throttle=True` (il pousse à chaque tick).
3. **Coût disque : 1,54 Go/JOUR** en enregistrant au rythme de l'affichage (snapshot 100×100 =
   **~4,4 ko**). D'où **`config.RECORD_SNAPSHOT_MS = 1000`, découplé de `SNAPSHOT_MS`** →
   ~385 Mo/jour, **2,7 Go sur 7 j** (comparable aux 2 Go de crypto, mais toujours conséquent :
   envisager une rétention plus courte ou la jonction vers `H:` comme le frère).
   🪤 **Ne pas se fier à la taille de books.db sur un petit échantillon** : à 18 snapshots elle
   affiche 4096 o (page SQLite minimale) et suggère 20 Mo/jour — **faux d'un facteur 20**.
4. **`visible_trades` ne lit PAS le disque, volontairement.** À 9 trades/s une journée pèse
   ~750 k trades ; les relire à chaque image coûterait O(trades) — le mur que crypto a contourné
   par un **rollup**, pas par une lecture brute. Le footprint reste borné à la mémoire tant
   qu'aucun rollup n'existe ici. **À MESURER avant de porter celui du frère.**
5. **BOUCLE DE DÉV COÛTEUSE** : Quantower charge les DLL de stratégie **au démarrage** et garde
   l'ancienne en mémoire. Le deploy réussit sans verrou ni message, **mais la plateforme continue
   de servir l'ANCIENNE** (vérifié : un champ nouvellement ajouté était absent du flux). Donc
   **toute modif du C# = fermer et rouvrir Quantower**, et une nouvelle stratégie n'apparaît dans
   le panneau `+` qu'après redémarrage. → **grouper les changements du pont, pousser le maximum
   de logique côté Python** (rechargeable à volonté).
6. **Locale FRANÇAISE du poste** (la sonde journalise « tick=0,**25** ») : toute sérialisation
   JSON en C# **doit** passer par `InvariantCulture`, sinon `0.25` sort `0,25` et casse le
   parseur Python.
7. **`.ps1` sans BOM** : PowerShell 5.1 les lit en CP1252 → un **tiret cadratin** dans une chaîne
   se décode en guillemet courbe et **casse le parse** (`TerminatorExpectedAtEndOfString`).
   Écrire les `.ps1` avec **BOM UTF-8**. Voir mémoire `powershell-ps1-bom-cp1252`.
8. **Logs de stratégie Quantower** : la grille de la plateforme n'affiche que la **1re ligne**
   d'un message (un rapport multi-ligne y est tronqué). Texte intégral sur disque :
   `C:\Quantower\Settings\Scripts\ScriptsData\<nom> (<guid>)\logs\<AAAAMMJJ>.slog` (JSON par
   ligne, clés `@t`/`lvl`/`ev`). `C:\Quantower\Logs\Serilog\` ne contient que le cycle de vie.

9. **`self._stop` sur une sous-classe de `threading.Thread` ÉCRASE `Thread._stop()`** (une
   méthode interne). `is_alive()` lève alors `TypeError: 'Event' object is not callable` et
   le thread ne se nettoie plus. Mesuré sur `IbkrFeed` → renommé `_stopping`. Le frère
   `QuantowerFeed` y échappe parce qu'il utilise la **composition** (attribut `_thread`),
   pas l'héritage.
10. **`eventkit` (dépendance d'ib_insync) appelle `get_event_loop()` DÈS L'IMPORT**, pas à
    la connexion. Dans un thread, il faut donc `asyncio.set_event_loop(new_event_loop())`
    **AVANT** d'importer `ib_connection`/`market_data`, sinon : `RuntimeError: There is no
    current event loop in thread 'ibkr-feed'`. Et ce n'est **pas** une `ImportError` : un
    garde `except ImportError` ne l'attrape pas et le thread meurt.
11. **`conda env create -f environment.yml` NE MARCHE PAS sur ce poste** (conda 26.5.3) :
    `CondaToSNonInteractiveError` sur `repo.anaconda.com/pkgs/{main,r,msys2}`. Faute de
    `.condarc`, conda ajoute `defaults` tout seul et exige d'en **accepter les CGU**
    (décision de l'utilisateur, pas la nôtre). MESURÉ : le contrôle tourne AVANT la lecture
    du yml → ni `- nodefaults`, ni `CONDA_CHANNELS=conda-forge` n'y changent rien, et
    `conda env create` **n'accepte pas** `--override-channels`. → **créer en 2 temps** :
    `conda create -n <env> --override-channels -c conda-forge python=3.12 pip -y` puis
    `pip install -r requirements.txt`. On n'a aucun besoin de ces canaux : vérifié, le frère
    `crypto-agg` ne contient QUE des paquets conda-forge.

12. **`pythonw.exe` met `sys.stdout` ET `sys.stderr` à `None`.** `print()` ne lève pas : il
    est **SILENCIEUSEMENT jeté**, tracebacks compris. C'est ce qui a imposé de porter
    `logsetup.py` (un `FileHandler` écrit quoi qu'il arrive) — et le garde-fou maison que le
    frère crypto n'a pas : **n'ajouter le `StreamHandler` que si `sys.stderr` existe**,
    sinon chaque émission échoue sous le lanceur silencieux.
    - ⚠️ Corollaire : **ne PAS rajouter `> logs\… 2>&1`** dans `Lancer (sans console).bat`.
      Ce serait deux écrivains sur le même fichier. (Et pour mémoire, mesuré au passage : la
      redirection d'un `start` **n'atteint pas** le fils sans `/B`, et `print()` vers un
      FICHIER est bufferisé par blocs — il aurait fallu `-u`. Les deux sont sans objet
      depuis que `logging` possède le fichier.)
    - ⚠️ Texte des `.bat` **sans accents** : la console Windows est en CP850. Même famille
      que le piège 7 (BOM des `.ps1`). Le journal, lui, est en **UTF-8**.
    - 🪤 **Ne pas juger l'encodage d'une console sur une capture par PIPE** : PowerShell
      décode la sortie native en CP850 et affiche du charabia (`d├®marrage`). Dans une VRAIE
      console, Python passe en `_WindowsConsoleIO`/UTF-8 et les accents sortent bien —
      **mesuré**. Comme le « 20 Mo/jour » et le « MBO », la fausse alerte venait de l'outil
      de mesure, pas du code.

13. **Les événements d'ib_insync/eventkit tiennent les handlers en WEAKREF** (`connect()` a
    `keep_ref=False` par défaut : il pose un `weakref.ref(obj)` et met `obj = None`). Donc
    `MarketDataManager(ib, contracts, stores).subscribe()` — un objet TEMPORAIRE — est collecté
    dès la fin de l'instruction, son slot est retiré, et **plus aucun tick n'arrive**. Panne
    **totale et SILENCIEUSE** : connexion établie, contrats résolus, pas une exception, un
    journal parfaitement sain (c'est ce qui l'a rendue introuvable côté connexion). **Mesuré**
    (`Event()` + objet non retenu → 0 slot, 0 tick reçu ; référence gardée → 1 tick).
    Régression de la refonte : l'ancien `run_live` gardait `market_data = …` en variable locale
    vivante pendant `ib.run()`. → Corrigé **à la source** : `market_data.subscribe()` connecte
    désormais avec **`keep_ref=True`** (l'abonnement maintient en vie ce qui en dépend — invariant
    du composant, pas une consigne à répéter à chaque appelant) ; `IbkrFeed` garde en plus
    `self._md` (propriété explicite, comme `self._ib`).
    🪤 **Un test qui garde la référence passe au vert sans rien prouver** — c'est très
    probablement ce qui a produit le « ✅ IBKR fonctionne : trades reçus » de la phase 0bis,
    alors que l'app, elle, n'a jamais rien affiché. **4e faux vert de la série.**

14. **La molette était INERTE en mode libre** (signalé par l'utilisateur : « parfois je ne peux
    même pas dézoomer »). `FlowViewBox.wheelEvent` ne modifiait que `o.x_span` — or `x_span`
    n'est lu par `refresh` **que sous `if self.follow:`** ; en libre, `refresh` lit
    `viewRange()` et ignore `x_span`. Donc **un simple glissement souris (→ `follow=False`)
    tuait le zoom du temps**, et `ev.accept()` empêchait même le zoom par défaut de pyqtgraph
    de prendre le relais. **Mesuré** : en libre, `x_span` 273,8 → 416,4 pendant que la plage
    affichée restait à **273,8 s** (0 mouvement) ; en live, 180 → 273,8 (OK). → Corrigé : en
    libre, la molette applique elle-même `setXRange`, en partant de la plage **AFFICHÉE** et
    non de `x_span` (un pan ou un zoom-boîte peut l'avoir désynchronisée — zoomer depuis une
    durée fantôme ferait sauter la vue), et resynchronise `x_span` pour que « ● Live » conserve
    la durée que l'œil voit.
15. **IBKR HORODATE À LA RÉCEPTION — les 2 accès ne montrent donc PAS les mêmes minutes.**
    Attente de l'utilisateur (2026-07-16) : « les affichages IBKR devraient être presque
    identiques à ceux de Quantower ». **Impossible sans abonnement**, et ce n'est pas un bug
    d'affichage : (1) le flux est **différé** → un trade reçu maintenant a eu lieu ~15 min plus
    tôt, mais `market_data.add_trade` l'horodate à **l'arrivée** (aucun horodatage de marché
    disponible : `ticker.rtTime` n'existe que via **RTVolume**, tickType 48/77, `genericTickList`
    "233", **réservé aux abonnés** — vérifié dans le source d'ib_insync). La vue IBKR est donc un
    morceau du **passé collé au présent** : les courbes ne peuvent pas se superposer ; (2)
    couverture ~4 % (**mesuré à 90 s sur NQ : 106 trades Quantower contre 33 IBKR**) → footprint
    troué ; (3) **0 carnet** → ni heatmap ni DOM. → La ligne d'état affiche désormais
    « **· flux DIFFÉRÉ** », lu sur `ticker.marketDataType` (**demandé à TWS**, pas déduit de
    `config.MARKET_DATA_TYPE` qui ne dit que « du différé si je n'ai pas mieux ») : elle disait
    « dernier tick il y a 3,0 s », ce qui se lit comme de la fraîcheur et invite précisément à
    la comparaison fausse. Vérifié des deux côtés : mention sur IBKR, **absente** sur Quantower.

16. **`_auto_y` EXIGEAIT un carnet → tout accès sans L2 était AVEUGLE** (c'est le « l'affichage
    est erroné, ça ne fonctionne plus du tout » de l'utilisateur, 2026-07-16). `refresh` faisait
    `if not self.y_manual and book is not None:` et `_auto_y` renonçait sans carnet. Or **IBKR
    non abonné n'a JAMAIS de carnet** : l'axe des prix restait figé sur le dernier cadrage
    (celui de Quantower) et les trades d'IBKR se dessinaient **hors écran**. **Mesuré** : axe à
    **29642-29660** pendant que les trades tombaient à **29284-29291** — 355 points plus bas,
    écran vide, aucune erreur. → `_auto_y` se rabat désormais sur `_vis_price` (les trades
    visibles, déjà recalculés à chaque image par `_draw_scatter`) ; le carnet reste l'ancre
    préférée, plus la seule. Vérifié après correction : axe 29303,84-29320,40 pour des trades
    à 29305,75-29318,50 → **11 posés / 11 VISIBLES**.
    🪤 **MON FAUX VERT — le 5e de la série, et le pire.** J'avais annoncé « IBKR fonctionne :
    17 trades → 17 points DESSINÉS » en comptant `scatter.getData()` : ça mesure les données
    **POSÉES sur l'objet**, pas ce qui est **dans le cadre**. Les points étaient là depuis le
    début — 355 points sous la vue. Le REPRISE avertissait déjà « compter les points DESSINÉS »
    après le bug des pixels fantômes ; **ça ne suffit pas**. La bonne assertion : compter les
    points dont (x, y) tombe DANS `vb.viewRange()`. Le cadrage fait partie du rendu.
17. **UN PONT VIVANT NE PROUVE PAS UNE DONNÉE VIVANTE — le piège du « ● reçu il y a 0,2 s ».**
    Quand Rithmic cesse d'alimenter Quantower, la stratégie continue de photographier le carnet
    toutes les 250 ms : les photos arrivent **fraîches, à 4/s, et RIGOUREUSEMENT IDENTIQUES**.
    `t_last` ne mesurant que l'ARRIVÉE, l'app affichait **« ● Quantower · NQ — reçu il y a
    0,2 s »** sur un carnet mort depuis ~20 min, en pleine séance. **Mesuré (2026-07-16, 13:23
    ET)** : 80 photos → **1 seule valeur distincte** (bid 29650,75×1 / ask 29652,75×4), **0
    trade**, spread de **2,00** (8 ticks, aberrant sur du NQ front en séance). `qb/qa` (flux de
    cotation, chemin **indépendant** de l'agrégation) gelé **aux mêmes valeurs** → ce n'est pas
    le pont, c'est la source. → `FlowStore.t_book_change` (instant du dernier changement de
    CONTENU) + `frozen_for()` + `config.STALE_BOOK_SECONDS = 30` → ligne d'état « ⚠ FLUX GELÉ »
    et bandeau sur le graphe.
    ⚠️ **Diagnostiquer un pont muet dans CET ordre** : (1) le contenu change-t-il ? (2) `qb/qa`
    bougent-ils ? (3) la stratégie journalise-t-elle ? (`…/ScriptsData/NQ Feed (<guid>)/logs/`
    — elle n'y consigne QUE les clients TCP, pas la santé du flux). 4 carnets/s **ne prouvent
    que le minuteur**, jamais la donnée.
18. **BANDEAU sur les graphes** (`FlowPanel.set_notice`, demandé par l'utilisateur pour IBKR :
    « juste afficher un message […] ex : "aucun abonnement" »). QLabel enfant, pas un
    `TextItem` : il ne doit ni bouger ni grossir avec le zoom, ni vivre dans le repère des
    données — ce qu'il dit, c'est justement qu'elles manquent. **En HAUT, pas au centre** (sur
    IBKR les trades s'affichent : seul le carnet manque), **`setWordWrap` + largeur bornée**
    (sans repli, `adjustSize` étale la phrase sur toute la largeur : mesuré, une barre qui
    écrase la vue). `MainWindow._notice_for` décide — la vue ne voit qu'un store, seule la
    fenêtre sait de quel accès il vient. Ne s'affiche PAS si TWS est déconnecté : ce serait
    accuser l'abonnement d'une panne de connexion.
    🪤 **`isVisible()` est faux tant que la fenêtre n'est pas `show()`** — un test headless qui
    l'omet croit le bandeau caché. Artefact de test, pas bug.

19. **`setYLink` NE SUFFIT PAS À ALIGNER LE DOM — et le « titre vide » du DOM est LOAD-BEARING.**
    Signalé par l'utilisateur (2026-07-16) : le carnet n'était pas en face des bons niveaux.
    `setYLink` synchronise la **plage**, jamais la **géométrie**. Le graphe principal porte
    `setTitle(symbol)` (30 px en haut), le DOM n'avait pas de titre → sa zone de tracé
    commençait 30 px plus haut. Pire : pyqtgraph **tente de compenser** en décalant la plage du
    linked view, **et se trompe de sens** → **désalignement MESURÉ de 60 px (= 2 × 30)**,
    constant, avec des plages Y qui **divergeaient** (21,30 vs 22,13 pts) alors qu'un `setYLink`
    est censé les rendre égales. → `self.dom.setTitle(" ", size="12pt")` : même hauteur, donc
    géométries identiques, donc compensation neutralisée. **Vérifié : plages identiques,
    haut=31,00 / hauteur=763,50 des deux côtés, écart 0,0 px sur 3 prix.**
    ⚠️ **NE JAMAIS “nettoyer” ce titre vide**, et si le titre du graphe change de taille, celui
    du DOM DOIT suivre.
    🪤 **Ne pas juger l'alignement sur les plages Y** : elles peuvent être égales et l'écran
    faux (géométries différentes), ou différentes et l'écran juste (compensation correcte). La
    seule mesure qui vaut : **mapper un même prix en pixels GLOBAUX dans les 2 widgets et
    comparer** (chaque `PlotWidget` a sa PROPRE scène — les coordonnées de scène ne sont PAS
    comparables entre eux ; passer par `mapToGlobal`).
20. **Graduations de temps : jamais plus fines que la résolution** (demandé par l'utilisateur,
    aligné sur crypto). `TzDateAxis` ne visait que « ~7 graduations » et posait une grille de
    **30 s sous des bougies de 1 min**. → `TzDateAxis.min_step`, calé par
    `FlowPanel.set_resolution`. `showGrid` dessinant SUR les graduations, la grille suit.
    Vérifié aux 6 résolutions. ⚠️ Effet de bord assumé : à **5 m sur la fenêtre par défaut de
    180 s**, il ne reste **qu'UNE graduation** (moins d'une bougie tient à l'écran).
21. **Ligne d'état muette quand tout va bien** (« reçu il y a 0,2 s · 27 trades » retiré à la
    demande de l'utilisateur) : elle ne disait rien que le graphe ne montre, et un bandeau
    permanent finit par ne plus se lire — précisément le jour où il annonce une panne. Elle ne
    parle plus que dans l'anomalie (FLUX GELÉ, pont fermé, TWS éteint). **Horloge** ajoutée
    dans la barre (fuseau `config.TIMEZONE`, celui de l'axe) : sur un tableau de bord de marché
    elle situe la séance, ce n'est pas une décoration.

22. **PONT C# — 2 modifs, DÉPLOYÉES le 2026-07-16 (Quantower fermé par l'utilisateur).**
    - **Refus de servir un symbole MORT** : `OnRun` sortait dès que `Instrument` était non-null.
      Or **un symbole RÉSOLU n'est pas un symbole VIVANT** : démarré avant que Rithmic ne soit
      rattaché, il sort du catalogue local (NQ@CME, l'air correct) sans flux → `hello` avec
      `tick NaN`, 4 carnets/s identiques, 0 trade. → garde
      `s.State == BusinessObjectState.Fake || double.IsNaN(s.TickSize) || s.TickSize <= 0`
      → `LogError` actionnable + `Stop()`. **L'OR est délibéré** : seul `TickSize=NaN` a été
      OBSERVÉ ; `State==Fake` est le test sémantique mais **non mesuré en panne** — l'OR ne
      suppose rien. (⚠️ `double.IsNaN`, jamais `== NaN` : NaN n'égale rien, pas même lui-même.)
    - **`InstanceName`, PAS `Name`** pour étiqueter l'instance (`"NQ Feed — NQ :5555"`) : deux
      instances étaient indiscernables dans le panneau. **Tranché par RÉFLEXION, et ça a évité
      une bourde** : `Name` a un setter **protected** et sert à dériver **`DataFolderName`**
      (= le dossier `ScriptsData/<nom> (<guid>)/logs`) → le changer aurait **éparpillé les
      journaux** ; `InstanceName` a un setter **PUBLIC**, c'est la propriété prévue.
    - Libellé `« Symbole (NQ front) »` → `« Symbole (NQ, ES, …) »` sur le PONT : c'est ce nom
      qui a fait croire à l'utilisateur qu'il fallait une stratégie « ES Feed » séparée. **La
      SONDE garde « NQ front »** (elle est bien spécifique au NQ).
    ⚠️ **`deploy.ps1` est INEXÉCUTABLE ici** : `powershell -File` → `PSSecurityException`
    (politique d'exécution). **Ne pas changer la politique du système** — c'est un réglage de
    sécurité qui appartient à l'utilisateur. Le script ne fait que *build + copier 3 fichiers*
    (`NqFeed.dll/.deps.json/.pdb` → `C:\Quantower\Settings\Scripts\Strategies\NqFeed\`) : le
    refaire à la main coûte 2 commandes. Build : `dotnet build -c Release -p:QuantowerBin=<le
    v* le plus récent>\bin`.
    🪤 **VÉRIFIER UNE DLL DÉPLOYÉE — 3 pièges enchaînés, tous rencontrés** : (1) `strings`
    **n'existe pas** dans ce bash ; (2) les littéraux C# sont en **UTF-16** dans le tas `#US`
    → un `grep` ASCII ne les voit pas, et un décodage UTF-16 depuis l'offset 0 **rate les
    chaînes à offset impair** (décoder aux DEUX alignements) ; (3) les **arguments d'attribut**
    (`[InputParameter("…")]`) vivent dans le tas des **blobs**, en **UTF-8** — pas au même
    endroit ni au même encodage que les littéraux. **Le test qui vaut : comparer le sha256 de
    la DLL déployée et de la compilée** (fait : identiques). 🪤 Et si l'ANCIEN libellé semble
    survivre à un build neuf : `NqFeedProbeStrategy.cs` compile **dans la même DLL** — ce n'est
    pas un build fantôme.
    ⏭️ **RESTE À FAIRE PAR L'UTILISATEUR** : rouvrir Quantower (piège 5 — la plateforme sert
    l'ANCIENNE DLL tant qu'elle n'a pas redémarré), reconnecter Rithmic, relancer les 2
    instances (NQ:5555, ES:5556). **Non validé sur flux réel.**

23. **« PLUS RIEN NE S'AFFICHE » À 17:13 ET = LA COUPURE QUOTIDIENNE DU CME, pas une panne —
    et c'est MA correction du piège 21 qui l'a rendue indéchiffrable.** Diagnostic mesuré
    (2026-07-16) : les 2 ponts vivants (`tick 0.25`, nouvelle garde passée), carnets **pleins**
    (100×100, 4/s), **0 trade** — normal, coupure 17:00-18:00 ET. Sans trades : ni footprint,
    ni points, ni bougies → écran quasi vide qui a TOUT l'air d'une panne. Or depuis le
    piège 21, la ligne d'état se TAIT quand « tout va bien » : l'app était devenue muette
    précisément dans le cas ambigu. L'utilisateur a fermé l'app au bout de 11 s (journal
    arrêté net SANS exception : une fermeture, pas un crash — reproduit en headless, 0
    exception, heatmap rendue). → `MainWindow._cme_pause()` (horaire encodé : dim 18:00 ET →
    ven 17:00 ET, coupure 17:00-18:00 ET) : la ligne d'état explique la coupure/le week-end,
    « en attente des premiers ticks » ne ment plus le samedi, et **« FLUX GELÉ » est muselé
    marché fermé** (un carnet figé le week-end est ATTENDU — une fausse alerte coûterait la
    confiance que l'alerte doit bâtir). Vérifié : table de vérité 10 cas (frontières 17:00
    inclus / 18:00 exclus, ven 17:00 → week-end, dim 18:00 → réouverture) + message en vraie
    coupure sur flux réel.
    🪤 **Leçon jumelle du piège 21 : une ligne d'état muette a un coût.** « Ne parler que dans
    l'anomalie » exige de savoir reconnaître TOUTES les situations où le vide est légitime —
    sinon le silence redevient un mensonge. L'horaire du marché fait partie de l'état.

### Corrigés dans les 2 `deploy.ps1` (bugs réels, présents depuis le commit initial)
- **Chemin** : `..\..\..` depuis `v1.146.14` visait **`C:\Settings\`** (racine du disque) au lieu
  de `C:\Quantower\Settings\` — et `New-Item -Force` fabriquait l'arbo **sans broncher** : échec
  SILENCIEUX. Le script n'avait donc jamais tourné avec succès ; la DLL de `NqTickExtractor` au
  bon endroit (11 juillet) y avait été copiée **à la main** par une session précédente.
  → dérivation depuis `$root` + `throw` si le dossier n'existe pas.
- **Exit code** : `$ErrorActionPreference` ne capte pas le code de sortie de `dotnet build` → un
  build en échec **déployait la DLL PRÉCÉDENTE**. → test `$LASTEXITCODE`.

---

## À FAIRE, DANS L'ORDRE

### 0bis. IBKR BRANCHÉ — et 2 MESURES qui rendent le site FAUX (2026-07-15, séance ouverte)
- 🐛 **Le port était faux.** `config.PORT = 4001` (IB Gateway live) ; **seul 7497 écoutait**
  = **TWS en paper**. Corrigé. Pour trouver le bon sans deviner :
  `foreach ($p in 4001,4002,7496,7497) { Test-NetConnection 127.0.0.1 -Port $p }`.
  TWS n'ouvre le port QUE si l'API est activée → un port ouvert prouve la config API.
- ✅ **IBKR fonctionne** : `ESU6`/`NQU6` (éch. 2026-09-18) résolus, trades reçus,
  `Error 354 ... /DEEP not subscribed` → heatmap et DOM vides = le mode dégradé attendu.
- ❌ **« CE COMPTE N'EST PAS EN DIFFÉRÉ » ÉTAIT FAUX — RÉFUTÉ le 2026-07-16.** Le compte EST
  en différé, établi par **4 chemins indépendants** : `ticker.marketDataType` = 3 ; `Warning
  10167` (« données différées affichées ») ; `Error 10189` = **« No market data permissions
  for CME FUT »** ; et le même NQ lu au même instant chez Rithmic, dont l'écart **DÉRIVE**
  (−2,50 → −12,25 pts en 30 s — deux temps réel colleraient à ±1 tick, stable).
  🪤 **La mesure « 1,8 s » était CIRCULAIRE** : elle lisait `ticker.time` en le croyant
  horodaté par le marché, alors qu'ib_insync fait `ticker.time = self.lastTime` =
  `datetime.now(timezone.utc)` → **l'heure de RÉCEPTION**. Elle rend ~2 s quoi qu'il arrive.
  C'est le piège de `FlowStore` un cran plus bas : j'avais changé de champ en croyant changer
  de chemin. **Aucun champ d'ib_insync ne donne l'heure du marché** sans `genericTickList`
  "233" (RTVolume), réservé aux abonnés. → Trancher en demandant à TWS, ou via une source
  INDÉPENDANTE ; **jamais en interrogeant le flux sur lui-même**.
  ✅ **LE RETARD EST MESURÉ : 11,5 min** (2026-07-16, NQ, corrélation croisée sur `trades.db` —
  28 min d'IBKR contre 359 min de Quantower, donc pas besoin d'enregistrer exprès : **les
  archives de la phase 4 rendaient la mesure gratuite**). Méthode : pour chaque lag, écart
  médian entre le prix IBKR à `t` et le prix Quantower interpolé à `t - lag`. Minimum **net et
  unique à 11,5 min → 1,53 pt** ; à lag 0 (si IBKR était temps réel) → **12,50 pts** ; à 20 min
  → 9,40 pts. **Donc les données IBKR ne sont pas FAUSSES, elles sont VIEILLES** : mêmes prix à
  1,5 pt près, 11,5 min plus tard. ⚠️ Ce n'est PAS le « ~15 min » de la convention IBKR : ne
  plus écrire ce chiffre, écrire **~11-12 min mesuré** (pas de la grille : 15 s).
  🪤 Note : `np.interp` traverse les trous du flux (le plus grand : 1195 s) — ici sans effet,
  le trou médian étant de 0,0 s, mais à re-vérifier avant de refaire la mesure sur un échantillon
  plus maigre.
  ✅ **Conséquence éditoriale : AUCUNE — vérifié, pas déduit.** La phase 5 annonçait « le site
  et `conception.md` mentent en disant différé » mais **n'a jamais appliqué cette correction-là**
  (ses vraies corrections : rôles inversés, agresseur fourni/deviné, `hist-ibkr`). Grep fait le
  2026-07-16 : aucun des 3 `contenu.json`, ni `conception.md`, ni le `README` ne contient
  « 1,8 s » / « abonnement L1 » / « temps réel » à propos d'IBKR. `tr-ibkr` dit toujours
  « En données différées (gratuites, ~15 min) […] la heatmap et le DOM restent vides » — **juste**.
  La conclusion fausse était donc restée confinée à `config.py` + ce REPRISE (corrigés).
  ⚠️ **La chance a joué** : c'est l'inaction de la phase 5 qui a sauvé le site, pas une
  vérification. Si elle avait fait ce qu'elle annonçait, le mensonge serait publié.
- 🔥 **SECOND DÉFAUT, INDÉPENDANT : LA COUVERTURE.** `reqMktData` ne diffuse pas chaque
  transaction, il publie l'état du dernier prix par intervalles. **MESURÉ sur NQ :
  0,3 mise à jour/s contre 9,0 trades/s chez Rithmic = ~3 % des transactions.** Le footprint
  IBKR n'a donc pas seulement un agresseur inféré : **son VOLUME est faux d'un facteur ~30**.
  → `reqTickByTickData("AllLast")` était noté « la seule sortie, À FAIRE » : **elle est FERMÉE**
  (erreur 10189, mesurée). Ce n'est pas une tâche de développement, c'est un **achat
  d'abonnement** — donc une décision de l'utilisateur, comme le CME historique.
- ⏱️ **IBKR met du temps à livrer** : connecté mais **0 trade après 8 s** (résolution des
  contrats ES+NQ). Le pont Quantower, lui : **1er trade à 1,9 s**. D'où l'état
  « ◌ connecté, en attente des premiers ticks… » plutôt qu'un « aucune donnée » trompeur.

### 0ter. Démo RETIRÉE DU MENU (choix de l'utilisateur, 2026-07-15)
`config.ACCESS` = quantower + ibkr seulement. `demo_feed.py` et `BUILDERS["demo"]` **restent**
(outil de dev : tests headless, et le CME est fermé ~76 h/semaine). Sa place n'était pas à
l'écran — des chiffres inventés à côté des vrais, dans un tableau de bord dont l'argument est
« d'où vient la donnée ». Réactivable en une ligne. L'invariant « la démo n'écrit JAMAIS sur
disque » est toujours testé (le test la monte hors menu exprès).

### 0. Questions ouvertes de l'utilisateur (2026-07-15, flux réel enfin vu)
- **Granularité du footprint — RÉGLÉE**, mais l'arbitrage lui appartient. Cause mesurée : ce
  n'était pas le footprint, c'était **l'auto-Y qui englobait les 100 niveaux/côté du pont**
  (50 pts sur le NQ) → 10 ticks par ligne. Corrigé par `config.VIEW_LEVELS` (bande de niveaux
  que l'axe couvre) + `config.FOOTPRINT_MAX_ROWS`. Mesuré après : étendue 13,4 pts →
  **1 tick = le DOM**. ⚠️ Relever `FOOTPRINT_MAX_ROWS` SEUL ne sert quasi à rien (46→300 ne
  gagne que 10→2 ticks : le pas se cale sur des valeurs rondes). ⚠️ **Arbitrage réel** : vue
  étroite = footprint fin mais heatmap peu profonde ; l'inverse aussi. Si les murs lointains
  lui manquent, relever `VIEW_LEVELS`.
- **Une stratégie PAR symbole** (ES + NQ = 2 instances, ports 5555/5556) : c'est le design
  actuel, pas une fatalité. La stratégie n'expose qu'un `Symbol` ; on pourrait en porter
  plusieurs sur un seul port en étiquetant les messages par symbole. Coût : modif C# + la
  boucle « fermer/rouvrir Quantower » (piège 5). À 2 symboles, 2 instances coûtent moins cher.
- **Se passer de Quantower ?** Voir le piège du pont plus haut : techniquement à portée
  (le POC est codé à 95 %), bloqué par le secret + le risque de conflit de session. **Poser
  la question du multi-session Rithmic AVANT d'investir.**

### 1. Ce qui reste de la phase 5 (le gros est FAIT, voir plus haut)
- **2 captures toujours dues PAR L'UTILISATEUR** (le site les référence déjà, **404 en
  attendant**, il l'a accepté en connaissance de cause) : `All.PNG` et `Dom-Heatmap.PNG` dans
  `affichage/site-content/assets/img/`. **Il fournit les captures, ne JAMAIS en inventer.**
- 🔥 **LES CAPTURES DU SITE SONT CELLES DU MODE DÉMO — PIRE QUE « l'ancienne interface »
  (mesuré le 2026-07-16, md5).** `Heatmap.PNG`, `Orderbook.PNG`, `Footprint.PNG`, `Trades.PNG`
  et `Capture_01.PNG` du site sont **octet pour octet** celles de `_archive/IBKR/`, dont le
  `config.py:11` porte **`DEMO_MODE = True`** et le `demo_feed.py:20`
  **`_BASE = {"ES": (5600.0, …)}`** — la capture « Heatmap » montre ES à **5 660-5 740**, une
  marche aléatoire partie de 5 600, alors qu'ES cote **7 623**. Ce sont des **chiffres
  INVENTÉS**, publiés sans mention, en illustration des sections **DOM & Heatmap** (`[2]`),
  **Footprint** (`[3]`) et **Trades** (`[4]`) — les 3 vues du pilier. Un lecteur y voit une
  heatmap dense qu'IBKR **ne peut pas produire** (pas de L2) et que Rithmic seul sert.
  ⚠️ **C'est l'exact contraire de l'argument du projet** (« d'où vient la donnée »), et de la
  règle qu'il s'impose déjà en interne (« la démo n'écrit JAMAIS sur disque : du synthétique
  mêlé au réel serait pire que rien »). **À remplacer par de vraies captures — de l'accès
  Quantower, le seul qui rende les 4 couches.** Décision + captures = l'utilisateur.
- 🪤 **« De mémoire, `_archive/IBKR` fonctionnait bien » (utilisateur, 2026-07-16) : c'était
  la DÉMO.** Son `market_data.py` est **identique** à l'actuel (même `reqMktData`, même
  `pendingTickersEvent +=`), et son `README.md:67` disait déjà « heatmap et DOM restent vides
  (pas de L2 → `/DEEP not subscribed`) », son `conception.md:135` « aucun contournement
  possible côté code ». **IBKR n'a JAMAIS servi de carnet ; rien n'a régressé.** Le seul vrai
  bug est né de la refonte (le weakref, piège 13). ⚠️ Piste écartée au passage : le compte
  n'est **pas** un paper (`managedAccounts` → **`U22910149`**, live) — le port 7497 est
  seulement celui de son TWS. C'est bien l'abonnement CME qui manque, pas le mode de compte.
- **Rien n'est commité ni poussé** — le site EN LIGNE ment donc toujours. Les 3 dépôts
  concernés : `indicesBoursiers` (les 3 `contenu.json` + le code), `iAlexMG.ca`
  (`data/projets/indices.json`, régénéré). Crypto n'a pas été touché.
- **3 choix de MA main, à faire valider s'il rouspète** :
  1. le mode démo est resté décrit sur la page `tr-ibkr` alors qu'il sert les 3 accès (c'est
     un reliquat de l'époque où IBKR était le seul accès réel) — pas de page `tr-demo` créée,
     ce serait un changement de structure du sous-hub ;
  2. `hist-ibkr` garde « À venir — la contre-épreuve » (l'utilisateur a dit « abonnement CME :
     une autre fois » = reporté, pas refusé) mais dit désormais que **l'abonnement est le
     préalable** et que la contre-épreuve « attend une décision, pas une semaine de
     développement ». Aucune 2e source alternative (Databento, CME DataMine) n'a été
     cherchée : c'est une décision d'achat, elle lui revient ;
  3. le hub `temps-reel` gagne 2 sections (« La même donnée, à deux prix », « Ce que ça
     coûte ») pour porter l'inversion des rôles.

### 2. Phase 5 ✅ — ce qui a été corrigé (2026-07-15)
- **Rôles inversés** : `tr-quantower` est désormais l'accès de l'écran (carnet L2 + agresseur
  fournis, sans abonnement) ; `tr-ibkr` est le témoin dégradé. Idem dans le bandeau `sources`
  du hub, qui disait d'IBKR « La porte du tableau de bord […] celle qui alimente l'écran ».
- **Agresseur** : la page `trades` distinguait mal ; elle oppose maintenant « fourni » (100 %
  mesuré, Quantower) et « deviné » (règle du tick, IBKR), et vend la bascule d'accès comme la
  démonstration de l'écart. `dom-heatmap` ne laisse plus croire que les 2 couches dépendent
  d'un abonnement dans l'absolu.
- **`hist-ibkr` disait « c'est elle qui alimente le tableau de bord temps réel »** : faux.
- `README.md` **réécrit** (titre « IBKR — … » compris), `docs/conception.md` **réécrit** avec un
  §3 « Deux revirements assumés » qui documente PyQt5→PySide6 et « pas de SQLite »→SQLite
  plutôt que d'effacer la trace, docstring de tête d'`orderflow_data.py` corrigé.
- `python tools/sync-site.py` passé : **seul `data/projets/indices.json` a bougé.**

### 3. ⚠️ EXCEPTION au « on ne touche pas à crypto » — tâche SÉPARÉE (chip `task_e01d97bb`)
L'utilisateur a demandé d'ajouter les **résolutions sous la minute** à crypto. **Piège trouvé,
à ne pas rater** : le rollup de crypto a une base de **60 s** et
`backend/archive.py::aggregate_footprint_rollup` (~l.366) fait
`factor = max(1, int(round(res_s / 60.0)))` → à `res_s=1`, `round(1/60)=0` et le `max(1,…)` le
masque : le rollup rendrait des **buckets de 60 s présentés comme des bougies de 1 s**,
**silencieusement faux**. Garde-fou : sous 60 s, forcer le chemin brut
(`gui/orderflow_view.py` ~l.732 : `wide = … and res_s >= 60`), vérifier aussi
`gui/footprint_reader.py`. ⚠️ crypto a **~2 Go de bases VIVANTES** : ne rien supprimer/migrer.

---

## COMMENT LANCER / TESTER

```powershell
# 1. Déployer les 2 stratégies (build + copie dans Quantower)
powershell -File affichage\NqFeed\deploy.ps1

# 2. Dans Quantower (Rithmic connecté) : panneau Strategies
#    « + » -> NQ Feed -> Symbole = NQ -> Run -> LAISSER EN WORKING
#    (« NQ Feed Probe » = la sonde, 60 s puis arrêt auto)
#    ⚠️ Après tout redéploiement du C# : FERMER ET ROUVRIR Quantower.

# 3. Valider le pont seul (stdlib, aucun GUI)
cd affichage ; python quantower_feed.py --seconds 15

# 4. Lancer le tableau de bord
cd affichage ; python main.py
```

**Interpréteur** : l'env **`indices-flow` EXISTE** (créé le 2026-07-15) :
`C:\Users\Moi\miniconda3\envs\indices-flow\python.exe` — python 3.12.13, PySide6 6.11.1,
pyqtgraph 0.14.0, numpy 2.5.1, **ib_insync 0.9.86** (vérifié : s'importe sur py3.12, et
`pyqtgraph.Qt` résout bien vers PySide6). ⚠️ Le recréer demande les **2 temps** du piège 11
(les CGU conda) — la recette est dans l'encadré de `environment.yml`.
⚠️ **UN SEUL env pour les 3 accès** : ils tournent dans le MÊME processus, donc `ib_insync`
doit cohabiter avec PySide6. Un env `ibkr` séparé rendrait la simultanéité impossible.
⚠️ **Toujours pas d'abonnement CME** (l'utilisateur l'a reporté, 2026-07-15) : l'accès IBKR
reste **dégradé**. Mais `MARKET_DATA_TYPE = 3` (différé, gratuit) devrait donner trades et
footprint si TWS tourne — **jamais vérifié en vrai, TWS étant éteint**. Heatmap/DOM vides.

**Test headless du rendu** (utile, ne demande pas d'écran) :
`QT_QPA_PLATFORM=offscreen`, construire `MainWindow`, appeler `panel.refresh()` en boucle.

**Bouchon du pont** (teste tout le Python sans Quantower) : rejouer le même NDJSON sur un socket
local — voir l'historique de la session ; ~30 lignes. Indispensable hors séance.

---

## ÉTAT GIT — COMMITÉ ET POUSSÉ le 2026-07-16

**FAIT** (point stable et officiel, à la demande de l'utilisateur) :
- `indicesBoursiers` : `ecbed1d..829c8a8` (refonte, 30 fichiers), `829c8a8..85bd5cf`
  (REPRISE), `85bd5cf..4da6a09` (recadrage IBKR) sur `origin/main`.
- `iAlexMG.ca` : `af78656..3fd2d80` puis `3fd2d80..50798cf` sur `origin/main`
  (`data/projets/indices.json` régénéré par `sync-site.py` à chaque fois).
- Les deux arbres sont **propres et synchro avec `origin/main`**. `sync-site.py --dry-run`
  rend « à jour » sur les 4 projets. Rien ne reste à committer côté code.
- ⚠️ **Le mensonge du site N'EST PAS le commit — il reste les CAPTURES DÉMO** (voir §1,
  « LES CAPTURES DU SITE SONT CELLES DU MODE DÉMO ») : le commit a figé les vrais textes,
  pas de vraies images. `All.PNG`/`Dom-Heatmap.PNG` toujours 404, les autres toujours démo.
  **À remplacer par de vraies captures Quantower — fournies par l'utilisateur.**
- ✅ **Textes IBKR RECADRÉS (2026-07-16, `4da6a09`/`50798cf`)** — décision de l'utilisateur
  « garder, mais recadrer temporaire » : les limites d'IBKR (différé, carnet L2 vide) sont
  désormais présentées comme dues à l'ABSENCE d'abonnement CME (un forfait les lèverait), et
  le seul écart de fond gardé est l'agresseur DEVINÉ — que l'API d'IBKR ne renvoie pas, même
  en temps réel (corrigé : la page Trades prétendait le contraire via `reqTickByTickData`).
  Chiffre transitoire « ~15 min » retiré. Intro « Visualisations » aussi dépoussiérée
  (PyQt5→PySide6, « futures d'Interactive Brokers »→CME, « 2 sources »→3 accès, onglets→boutons).
- 🔔 **NOUVEAU (2026-07-16) : l'utilisateur SIGNALE l'intention d'acheter un forfait de données
  CME « prochainement ».** Ça ASSOUPLIT la décision « CME REFUSÉ, ne plus reposer » plus bas —
  ne plus la citer comme définitive. Le site est maintenant écrit pour ACCUEILLIR l'abonnement
  (limites IBKR = temporaires). ⚠️ Le jour où il s'abonne : le retard et la heatmap/DOM d'IBKR
  reviennent, MAIS l'agresseur reste deviné — la comparaison « 2 portes » survit, déplacée vers
  « ce que même la donnée payante ne dit pas » (déjà mesuré : `reqTickByTickData` ne donne pas le côté).

Ce qu'a contenu le commit `829c8a8` (garde la trace du QUOI) :

```
 M affichage/config.py              ACCESS + SOURCE (remplace DEMO_MODE), QT_FEED_PORTS,
                                    DATA_DIR, RETENTION_DAYS, RECORD_SNAPSHOT_MS,
                                    STALE_BOOK_SECONDS (piège 17) ; commentaires : différé
                                    ÉTABLI par 4 chemins + retard MESURÉ 11,5 min
 M affichage/flow_view.py           API de couches, réglages Trades, go_live/goto/
                                    set_resolution/set_symbol, sigRangeChangedManually ;
                                    _build_options SUPPRIMÉ ; wheelEvent en mode libre
                                    (piège 14) ; _auto_y sans carnet (piège 16) ;
                                    set_notice/bandeaux (piège 18) ; TzDateAxis.min_step =
                                    résolution (piège 20) ; DOM setTitle(" ") (piège 19)
 M affichage/ib_connection.py      print() -> logging
 M affichage/market_data.py         connect(..., keep_ref=True) — le handler mourait en
                                    weakref (piège 13) ; propriété `delayed` (lue sur
                                    marketDataType, jamais déduite de la config)
 M affichage/main.py                BUILDERS : les 3 accès MONTÉS en parallèle ; IbkrFeed
                                    (thread + boucle asyncio propres) ; Storage ;
                                    setup_logging() ; self._md + `delayed` (piège 13)
 M affichage/orderflow_data.py      source/recorder/book_reader dans FlowStore ;
                                    add_book(throttle=) ; visible_books fusionne mémoire+disque ;
                                    t_book_change + frozen_for() (piège 17)
 M affichage/requirements.txt       PySide6 ; ib_insync DÉCOMMENTÉ (plus optionnel)
 M affichage/ui.py                  RÉÉCRIT : barre complète (boutons, Affichage ⚙, Aller au,
                                    ● Live, Accès, ✕ Quitter, HORLOGE), thème #0d1117 ;
                                    ligne d'état MUETTE quand tout va bien (piège 21), FLUX
                                    GELÉ (piège 17), bandeaux _notice_for (piège 18),
                                    _cme_pause : coupure 17-18h ET + week-end (piège 23)
 M affichage/README.md             RÉÉCRIT (le titre disait encore « IBKR — … »)
 M affichage/docs/conception.md    RÉÉCRIT + §3 « Deux revirements assumés »
 M affichage/site-content/contenu.json      tr-ibkr / tr-quantower / trades / dom-heatmap
 M site-content/contenu.json       bandeau `sources` + hub temps-reel (rôles inversés)
 M historique/site-content/contenu.json     hist-ibkr (2 corrections)
 M historique/NqExtractor/deploy.ps1   les 2 bugs (chemin + exit code)
?? REPRISE.md                      ce fichier

Dans `iAlexMG.ca` (dépôt SÉPARÉ) :
 M data/projets/indices.json       RÉGÉNÉRÉ par tools/sync-site.py — ne jamais l'éditer à la main
?? affichage/NqFeed/               NqFeedStrategy.cs (le pont : garde symbole mort +
                                    InstanceName + libellé, piège 22 — DÉPLOYÉ et VALIDÉ :
                                    les 2 instances servent tick 0.25),
                                    NqFeedProbeStrategy.cs (sonde), NqFeed.csproj, deploy.ps1
?? affichage/backend/              trade_archive, book_archive, recorder, book_reader
?? affichage/controls.py           LayersPanel + TypeSettings (popup « Affichage ⚙ »)
?? affichage/logsetup.py           logs/indices.log ; StreamHandler SEULEMENT si sys.stderr
?? affichage/Lancer.bat            double-clic, avec console (calqué sur crypto)
?? "affichage/Lancer (sans console).bat"   pythonw ; c'est logsetup qui écrit (piège 12)
?? affichage/environment.yml       env indices-flow (+ le piège des CGU conda, encadré dedans)
?? affichage/quantower_feed.py     le client du pont
```
`affichage/data/` (trades.db, books.db) est bien **ignoré** (`.gitignore:16` → `data/`), tout
comme `bin/`/`obj/` des projets C#. Vérifié.

---

## MÉTHODES ÉPROUVÉES CETTE SESSION (réutiliser telles quelles)

- **Interroger la DLL par RÉFLEXION plutôt que la doc.** Un mini-projet `dotnet` avec
  `<Private>true</Private>` (sinon `FileNotFoundException` au runtime) qui `Assembly.LoadFrom`
  le BusinessLayer et dumpe types/événements/propriétés/enums. A répondu en quelques minutes à
  des questions que la doc n'aurait pas tranchées, et **le compilateur a validé le reste**.
- **Instancier pour lire les DÉFAUTS** (`new GetLevel2ItemsParameters()` puis réflexion sur les
  propriétés) au lieu de les supposer : c'est comme ça que la piste `ImplicitOrderBookType` a été
  écartée en 30 secondes.
- **Mesurer avant de construire.** La sonde Phase 0 a évité d'écrire une heatmap qui serait
  peut-être restée vide. Le compteur `reçus` vs `stockés` a révélé les 29 % perdus. La taille de
  `books.db` après le test a révélé les 10,8 Go. **Chaque fois, c'est la mesure qui a corrigé
  le code — jamais l'intuition.**
- **Chercher un chemin INDÉPENDANT pour trancher un doute** : `Symbol.Bid/Ask` (cotation) contre
  le carnet agrégé. Un test bâti sur les mêmes données que le soupçon ne prouve rien (le test
  « trades vs carnet » a échoué pour ça : 4 snapshots/s contre 9 trades/s, le décalage temporel
  noyait le signal).
- **Vérifier ses propres chiffres avant de les rapporter.** Le « 20 Mo/jour » et le « MBO »
  étaient tous deux faux et tous deux flatteurs.
