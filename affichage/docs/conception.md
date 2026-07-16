# Conception de l'application — choix, embûches, adaptations

> Document **transversal** : il caractérise l'application globale — la vision, les
> décisions de conception structurantes, les **embûches rencontrées et leurs solutions**,
> et les **contraintes de données** propres à chaque accès. À lire en complément de
> [README.md](../README.md) (architecture fichier par fichier).
>
> Dernière mise à jour : **2026-07-15**.

---

## 1. Vision

Dashboard temps réel pour les futures **ES** et **NQ** (CME), reproduisant les graphiques
du projet *Crypto* : **un seul graphe** où **footprint** (type Quantower, avant-plan) et
**orderflow** (heatmap + scatter + lignes bid/ask, style Bookmap) sont **superposés**, avec
une **échelle DOM** latérale liée en prix.

**Trois accès** au même marché, derrière une **interface de données unique** (`FlowStore`) :
Quantower/Rithmic, IBKR, et un générateur synthétique. La vue ne sait pas d'où viennent les
données — elle lit un `FlowStore`. C'est l'angle du projet : *un marché, plusieurs accès*,
et ce que chacun coûte ou fournit.

Les trois accès sont captés **en parallèle** ; le menu `Accès` ne change que la source
**lue**. ES et NQ se choisissent par **boutons**, au-dessus d'un graphe unique.

---

## 2. Choix de conception structurants

- **Séparation données / rendu via `FlowStore`.** `orderflow_data.py` porte les buffers et
  la construction des bougies ; `flow_view.py` ne fait que dessiner. Les trois accès
  écrivent dans des `FlowStore` de même forme → on bascule sans toucher à la vue.

- **Un store par `(accès, symbole)`, tous alimentés en continu.** Le menu ne fait que
  rebrancher la vue sur une autre clé. Capter à la demande aurait creusé un trou de données
  à chaque bascule, et figé le flux qu'on quitte. C'est l'architecture du projet Crypto
  (tous les flux captés, la vue en lit un).

- **Chaque source dans son thread ; le GUI relit sur un QTimer.** Aucune source ne touche à
  un objet Qt. C'est ce qui rend le binding indifférent à la source — et, en particulier,
  ce qui **dissout le conflit apparent entre `ib_insync` et PySide6** : `util.useQt()` (qui
  ne connaît pas PySide6) n'est jamais appelé, la boucle asyncio d'IB tournant dans son
  thread à elle.

- **Aucun accès n'est obligatoire.** Pont fermé, TWS éteint, `ib_insync` absent : l'accès
  concerné laisse sa vue vide et se reconnecte en tâche de fond, les autres continuent.

- **Un accès muet affiche du VIDE, jamais les chiffres du voisin.** Laisser la vue sur le
  flux précédent en changeant l'étiquette serait un mensonge — or c'est précisément ce que
  ce dashboard démontre : d'où vient la donnée. `FlowPanel.refresh` **efface** donc toutes
  les couches quand son store n'a rien (`_clear`), au lieu de sortir en avance en laissant
  les pixels du flux d'avant. Ça n'a rien de théorique : le bug a existé, et il montrait
  1226 points de la démo sous l'étiquette « Quantower ».

- **Le vide doit s'EXPLIQUER.** Un écran noir muet est indiscernable d'une panne : la ligne
  d'état nomme l'accès lu, l'âge du dernier tick, et — s'il ne donne rien — la raison lue
  auprès du flux lui-même (port injoignable, TWS éteint). Sans elle, la seule façon de
  savoir qu'un pont était fermé était d'aller lire `logs/indices.log`.

- **Un seul graphe superposé** (pas deux panneaux) : footprint et orderflow partagent
  exactement le même axe temps+prix → alignement parfait. Le DOM est un panneau latéral
  étroit, **lié en Y** (`setYLink`) au graphe principal.

- **Grille de prix COMMUNE** à la heatmap et au DOM (`_grid`) : chaque rangée de la heatmap
  correspond exactement à une barre du carnet. Le pas (`tick`) dérive du **plus petit écart
  réel** entre niveaux du carnet (tick d'instrument).

- **Le footprint porte l'OHLC.** Les bougies sont reconstruites **depuis les trades**
  (`build_candles`), pas depuis un canal candle : le footprint exige de toute façon le
  détail par prix. L'ancien `OHLCAggregator` en est devenu redondant et a été retiré.

- **Tout passe par `pyqtgraph.Qt`**, qui résout le binding lui-même : aucun `pyqtSignal`,
  aucun import direct de PySide6/PyQt5. C'est ce qui rend le code de rendu **transférable**
  entre les deux dépôts, et ce qui a rendu le port PySide6 quasi gratuit.

- **`logging`, jamais `print()`** (`logsetup.py`, porté du frère). Deux raisons mesurées,
  pas stylistiques : les accès écrivent depuis des **threads** différents et `print()` écrit
  le texte puis le saut de ligne en deux opérations — les lignes se collaient ; et sous
  `pythonw.exe` (lanceur « sans console »), `sys.stdout`/`sys.stderr` valent **`None`** et
  tout `print()` est **silencieusement jeté**, tracebacks compris. Un `FileHandler` écrit
  quoi qu'il arrive et sérialise derrière un verrou. Seule exception : le mode CLI de
  `quantower_feed.py`, qui imprime un *rapport* de validation, pas un journal.

---

## 3. Deux revirements assumés

Ce document affirmait l'inverse jusqu'au 2026-07-15. Les deux choix ont été inversés parce
que la **mesure** les a contredits — la trace est gardée ici plutôt qu'effacée.

- **PyQt5 → PySide6.** On gardait PyQt5 pour ne pas fâcher `ib_insync`/`util.useQt()`. Le
  conflit n'existait pas : dès lors que le flux IB vit dans son thread et n'écrit que dans
  un `FlowStore`, le binding ne le concerne plus. Le port a coûté un `app.exec_()` →
  `app.exec()`, parce que tout passait déjà par `pyqtgraph.Qt`. Bénéfice : même pile que le
  projet frère, donc du code qui circule entre les deux.

- **« Pas de base de données » → SQLite.** Le raisonnement (« un seul instrument mono-source,
  usage live → l'historique profond est sans objet ») tombait sur un fait : **le carnet n'a
  aucun historique téléchargeable**, ni chez Rithmic ni chez aucun exchange crypto. Une
  heatmap historique ne peut donc exister qu'en **enregistrant le live**. Sans disque, il n'y
  avait pas de « profondeur en moins » — il n'y avait *jamais* de heatmap passée du tout.

---

## 4. Contraintes de données, par accès

### Quantower / Rithmic — l'accès principal

Le fait fondateur, **mesuré par réflexion sur `TradingPlatform.BusinessLayer.dll`, pas
supposé** : l'abonnement payant de Quantower ne verrouille que **ses propres panneaux**.
Les événements bruts du BusinessLayer sont ouverts.

- **Côté agresseur FOURNI** (`Last.AggressorFlag`) : couverture mesurée **100 %** sur NQ
  (`None` 0, `NotSet` 0) → **le footprint est exact**, jamais inféré.
- **Carnet L2 servi par Rithmic**, sans abonnement supplémentaire : 239 snapshots sur 60 s,
  **0 vide**, ~210 niveaux par côté.
- **Aucun historique L2** (`HistoryType` ne le connaît pas) → la heatmap ne grandit qu'en
  enregistrant le live. Exactement la contrainte du projet Crypto : la symétrie tient seule.
- **Pas de `TradeId`** en live → aucune déduplication possible. Ce n'est pas une perte :
  deux trades d'un lot au même prix, côté et milliseconde sont **réels et distincts**, et
  les fusionner fausserait le volume — ce que le footprint est censé mesurer.
- **Le pont, et pourquoi il est « obligatoire » — la nuance compte.** Mesuré par
  `automatisation/poc/Phase0Poc` (docs/phase0-poc.md, Q1) : `Core.Initialize()` **fonctionne**
  hors du process Quantower, et les 80 connexions de `settings.xml` — dont Rithmic, avec son
  user et son serveur — se restaurent toutes seules. **Le seul verrou est le SECRET** : le mot
  de passe stocké est chiffré et non déchiffrable dehors
  (`FailedToRestorePassword=True` → `Connect()` rend `"Password is empty."`). Ce n'est donc
  pas le BusinessLayer qui interdit le standalone. La sortie existe (mot de passe en clair
  dans un fichier local gitignoré) mais elle **n'a jamais été validée de bout en bout**, et
  elle coûte : un secret en clair sur le disque, et un risque **non mesuré** de conflit de
  session Rithmic avec le Quantower de l'utilisateur. On tourne donc DANS Quantower, où la
  connexion est déjà vivante et authentifiée.
- **Une instance de stratégie PAR SYMBOLE**, chacune sur son port (`config.QT_FEED_PORTS`).
  C'est un choix, pas une fatalité : la stratégie n'expose qu'un `Symbol` en paramètre, mais
  rien n'empêcherait d'en porter plusieurs sur un seul port en étiquetant les messages. À
  deux symboles, deux instances coûtent moins cher qu'un protocole multi-symbole — et chaque
  modif du C# impose de redémarrer Quantower.

### IBKR — l'accès dégradé

Conservé pour ce qu'il démontre, pas pour ce qu'il rend.

- **Côté agresseur non fourni.** IBKR donne `last` / `lastSize`, pas le côté. Il est
  **inféré** (`FlowStore._infer_side`) par la règle du tick : `last ≥ ask` → buy,
  `last ≤ bid` → sell, sinon comparaison au dernier prix. Approximation suffisante pour le
  rendu ; `reqTickByTickData` donnerait le vrai côté (piste ouverte).
- **Carnet L2 = abonnement CME.** En différé (`MARKET_DATA_TYPE = 3`), `reqMktDepth` renvoie
  `Error 354 ... /DEEP not subscribed` → heatmap et DOM vides ; footprint et scatter, eux,
  se remplissent depuis les trades.
- **Dédoublonnage nécessaire.** `reqMktData` re-pousse le même `last` à chaque mise à jour :
  on n'enregistre que si `(last, time)` a changé (`market_data._last_seen`). Noter le
  contraste avec Rithmic ci-dessus — ici le doublon est un **artefact du transport**, pas un
  vrai trade.

### Démo

- **Marché fermé = aucun tick.** Le CME ferme la fin de semaine. D'où le générateur :
  indispensable pour valider l'affichage hors séance.
- **Il n'écrit rien sur disque.** On ne mélange jamais du synthétique au marché réel : une
  heatmap historique mi-vraie mi-inventée serait pire qu'aucune heatmap.

---

## 5. Embûches rencontrées & solutions

### Connexion / API
- **ConnectionRefused** → mauvais port. Le TWS de ce poste écoute sur **4001**.
- **Error 321 « API en lecture seule »** → `readonly=True` dans `ib.connect()`.
- **Error 354 sur les prix** → `MARKET_DATA_TYPE = 3` accepte le différé.
- **`eventkit` appelle `get_event_loop()` DÈS L'IMPORT** d'`ib_insync`, pas à la connexion :
  dans un thread, il faut poser la boucle **avant** d'importer. Et ce n'est pas une
  `ImportError` — un garde trop étroit laisse le thread mourir.

### Rendu
- **Snapshots de carnet trop nombreux.** `FlowStore.add_book` throttle à `SNAPSHOT_MS`.
  ⚠️ Mais **le pont échantillonne déjà** : les deux throttles au même seuil se superposaient
  et la gigue réseau en jetait **29 % en silence**. D'où `add_book(..., throttle=False)` —
  c'est l'appelant qui sait si sa source est déjà échantillonnée. IBKR garde le throttle
  (il pousse à chaque tick).
- **Lignes best bid/ask trouées.** `_ffill` remplit par la dernière valeur connue.
- **Footprint qui « vibre » au zoom.** Tick de regroupement figé par **hystérésis**
  (`_footprint_tick`) : on ne recalcule qu'au franchissement de la bande lisible (12..46).
- **Scatter qui clignote.** Le filtre `Auto` utilise un **seuil de taille stable** (lui aussi
  par hystérésis) : un top-N recalculé à chaque image faisait apparaître/disparaître les
  points en bord de sélection. Les points sont aussi dessinés par volume croissant, pour que
  l'empilement ne permute pas d'une image à l'autre.
- **Trop de points.** À ~9 trades/s, l'heure gardée en mémoire fait ~32 000 points. Le filtre
  `Auto` vise une cible qui **décroît avec la plage** ; `HARD_CAP` plafonne quoi qu'il arrive.

### Mode démo
- **1er affichage vide.** Le générateur **préremplit** ~`LIVE_SPAN_SECONDS` d'historique
  (`DemoFeed._prefill`) → la fenêtre est déjà pleine au lancement.

---

## 6. Ce qui n'est PAS porté du projet Crypto (volontairement)

Hybride multi-exchange, basis/spread, détection d'arbitrage : **sans objet** pour un marché
**unique** — il n'y a pas d'arbitrage entre une place et elle-même. Le menu `Marché`
Futures/Spot l'est aussi (futures CME seuls).

**Pas de rollup pré-agrégé** non plus, donc `visible_trades` ne lit pas le disque et le
footprint reste borné à la mémoire. Chez Crypto, la lecture brute en zoom arrière coûtait
~300 ms par image ; le rollup l'a contournée. Ici la limite est peut-être bien plus loin —
sur un seul instrument — mais elle est **à mesurer avant de porter** quoi que ce soit.

---

## 7. Dette connue / pistes ouvertes

- **Précision du côté agresseur chez IBKR** : la règle du tick est une approximation.
  `reqTickByTickData` donnerait le vrai côté et la vraie taille. Sans objet côté Rithmic,
  qui le fournit.
- **Coût disque** : ~385 Mo/jour à 1 Hz, soit **2,7 Go sur 7 jours** — comparable au projet
  frère, mais conséquent. Envisager une rétention plus courte, ou une jonction vers un autre
  volume.
- **Footprint historique** : borné à la mémoire tant qu'aucun rollup n'existe (voir §6).
- **Heatmap/DOM d'IBKR tributaires de l'abonnement L2** : aucun contournement côté code.
  C'est justement ce que l'accès Quantower démontre — la même donnée, autrement.
