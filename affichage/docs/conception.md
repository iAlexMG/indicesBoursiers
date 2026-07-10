# Conception de l'application — choix, embûches, adaptations

> Document **transversal** : il caractérise l'application globale — la vision, les
> décisions de conception structurantes, les **embûches rencontrées et leurs solutions**,
> et la **contrainte de données IBKR**. À lire en complément de
> [README.md](../README.md) (architecture fichier par fichier).
>
> Dernière mise à jour : **2026-06-29**.

---

## 1. Vision

Dashboard temps réel pour les futures **ES** et **NQ** (Interactive Brokers), reproduisant
les graphiques du projet *Crypto* : **un seul graphe** par symbole où **footprint**
(type Quantower, avant-plan) et **orderflow** (heatmap + scatter + lignes bid/ask, style
Bookmap) sont **superposés**, avec une **échelle DOM** latérale liée en prix.

Deux sources interchangeables derrière une **interface de données unique** (`FlowStore`) :
le flux **réel** IBKR (`ib_insync`) ou un **générateur synthétique** (mode démo). La vue
ne sait pas d'où viennent les données — elle lit un `FlowStore`.

ES et NQ sont présentés en **onglets** (`QTabWidget`) : un graphe combiné plein écran par
symbole, plus lisible que deux panneaux empilés.

---

## 2. Choix de conception structurants

- **Séparation données / rendu via `FlowStore`.** `orderflow_data.py` ne contient que des
  buffers en mémoire (trades + snapshots de carnet) et la construction des bougies ;
  `flow_view.py` ne fait que dessiner. Les deux sources (réel / démo) écrivent dans le même
  `FlowStore` → on bascule de l'un à l'autre sans toucher à la vue.

- **Tout en mémoire, borné par fenêtre temporelle.** Pas de base de données (contrairement
  au projet Crypto qui archive en SQLite). Les `deque` de trades et de snapshots sont
  purgés au-delà de `TRADES_WINDOW_SECONDS` / `BOOKS_WINDOW_SECONDS` (1 h par défaut).
  Rationale : un seul instrument mono-source, usage live → l'historique profond et l'agrégat
  SQL du projet Crypto sont **sans objet ici**.

- **Un seul graphe superposé** (pas deux panneaux) : footprint et orderflow partagent
  exactement le même axe temps+prix → alignement parfait. Le DOM est un panneau latéral
  étroit, **lié en Y** (`setYLink`) au graphe principal.

- **Grille de prix COMMUNE** à la heatmap et au DOM (`_grid`) : chaque rangée de la heatmap
  correspond exactement à une barre du carnet → superposition cohérente. Le pas (`tick`)
  dérive du **plus petit écart réel** entre niveaux du carnet (tick d'instrument).

- **Le footprint porte l'OHLC.** Les bougies sont reconstruites **depuis les trades**
  (`build_candles`), pas depuis un canal candle : le footprint exige de toute façon le détail
  par prix (volume acheteur/vendeur par niveau). L'ancien `OHLCAggregator` est donc devenu
  redondant et a été **retiré**.

- **Rester en PyQt5.** Le projet Crypto est en PySide6 ; ici on conserve PyQt5 (déjà en place
  avec `ib_insync`/`util.useQt()`). `pyqtgraph` abstrait le binding → le code de rendu est
  **porté, pas copié**, sans ajouter de dépendance.

- **Seul l'onglet actif est rafraîchi.** Le `QTimer` de la fenêtre ne redessine que le
  `FlowPanel` visible → coût de rendu constant quel que soit le nombre de symboles.

---

## 3. Contrainte de données IBKR (point central)

Les graphiques footprint et heatmap exigent des données plus riches que ce qu'IBKR fournit
nativement. C'est la principale différence avec un exchange crypto (qui pousse trades et
carnet complets en WebSocket).

- **Côté agresseur (buy/sell) non fourni.** IBKR donne `last` / `lastSize`, mais pas le côté
  du trade. Il est **inféré** (`FlowStore._infer_side`) par la **règle du tick** :
  `last ≥ ask` → buy, `last ≤ bid` → sell, sinon up/down-tick (comparaison au dernier prix).
  Suffisant pour le rendu visuel ; pour une précision parfaite par trade, on passerait à
  `reqTickByTickData` (piste ouverte).

- **Carnet L2 = temps réel uniquement.** `reqMktDepth` (DOM, heatmap) nécessite un
  **abonnement CME L2** ; en différé (`MARKET_DATA_TYPE = 3`) il renvoie
  `Error 354 ... /DEEP not subscribed` → heatmap et échelle DOM vides (le footprint et le
  scatter, eux, se remplissent depuis les trades).

- **Marché fermé = aucun tick.** Le CME ferme le week-end → rien ne circule. D'où le
  **mode démo** : indispensable pour valider l'affichage hors séance ou sans abonnement.

- **Dédoublonnage des trades.** `reqMktData` re-pousse le même `last` à chaque mise à jour ;
  on n'enregistre un trade que si `(last, time)` a changé (`market_data._last_seen`).

---

## 4. Embûches rencontrées & solutions

### Connexion / API (héritées du programme initial, conservées)
- **ConnectionRefused** → mauvais port. Le TWS de ce poste écoute sur **4001** (et non
  4002/7497). Réglé dans `config.py`.
- **Error 321 « API en lecture seule » + `orders request timed out`** → ajout de
  `readonly=True` dans `ib.connect()` : `ib_insync` ne tente plus de lire les ordres au
  démarrage. Compatible avec « Read-Only API » coché côté TWS.
- **Spirale déconnexion/reconnexion + crash `QTimer deleted`** → suppression de la
  reconnexion bloquante (ré-entrance dans l'event loop) + `closeEvent` qui stoppe le timer,
  déconnecte IB et arrête la boucle asyncio.
- **Error 354 sur les prix** → `MARKET_DATA_TYPE = 3` accepte les données différées.

### Rendu / port PySide6 → PyQt5
- **Snapshots de carnet trop nombreux.** Sans throttle, on enregistrerait un snapshot à
  chaque tick → mémoire et heatmap saturées. `FlowStore.add_book` n'enregistre qu'une fois
  par `SNAPSHOT_MS` (250 ms par défaut).
- **Lignes best bid/ask trouées.** Les snapshots peuvent manquer un côté ponctuellement →
  NaN dans la série. `_ffill` remplit par la dernière valeur connue (puis backfill initial)
  pour des lignes continues.
- **Footprint qui « vibre » au zoom.** Le tick de regroupement est figé par **hystérésis**
  (`_footprint_tick`) : on garde le tick courant tant que le nombre de lignes reste lisible
  (12..46), on ne recalcule (vers un tick rond) qu'au franchissement de cette bande.
- **API `mapViewToDevice`.** Le footprint dessine ses chiffres en coordonnées **écran**
  (ne se déforment pas au zoom). Vérifié présent dans pyqtgraph 0.14 (env `ibkr`).

### Mode démo
- **1er affichage vide.** Le générateur **préremplit** ~`LIVE_SPAN_SECONDS` d'historique
  (`DemoFeed._prefill`) en backdatant trades et snapshots → la fenêtre est déjà pleine au
  lancement, sans attendre que le live se construise.

---

## 5. Ce qui n'est PAS porté du projet Crypto (volontairement)

Hybride multi-exchange, basis/spread, détection d'arbitrage, archives SQLite
(`HistoryStore`, `BookReader`, `FootprintReader`, `trades.db`/`books.db`), agrégation SQL
en zoom arrière. Sans objet pour une source **unique** (IBKR) en usage **live**.

---

## 6. Dette connue / pistes ouvertes

- **Précision du côté agresseur** : la règle du tick est une approximation ;
  `reqTickByTickData` donnerait le vrai côté + la vraie taille par trade.
- **Pas d'historique profond** : tout est en mémoire (1 h). Un enregistrement disque
  (à la Crypto) permettrait la navigation historique au-delà de la fenêtre live.
- **Heatmap/DOM tributaires de l'abonnement L2** : aucun contournement possible côté code
  (donnée payante IBKR). Le mode démo couvre la validation visuelle.
