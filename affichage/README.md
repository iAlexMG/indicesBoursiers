# Orderflow / Footprint / DOM (ES & NQ)

Programme Python qui affiche, pour les futures **ES** et **NQ**, un **dashboard natif**
(PySide6 + pyqtgraph) reproduisant les graphiques du projet *Crypto* : **un seul graphe**
où sont superposés un **footprint** (type Quantower), un **orderflow** (heatmap de
liquidité + scatter des trades + lignes best bid/ask, style Bookmap) et une **échelle DOM**
(carnet courant) à droite, alignée sur la même grille de prix.

> Voir [docs/conception.md](docs/conception.md) pour la vision, les choix de conception et
> les embûches. L'état d'avancement et les pièges mesurés sont dans
> [../REPRISE.md](../REPRISE.md) — **à lire en premier** pour reprendre le travail.
> Dernière mise à jour : **2026-07-15**.

## Trois accès au même marché

Un seul marché (**CME**), trois façons d'y accéder. Les trois tournent **en parallèle** :
le menu `Accès` de la barre ne change que la source **lue**, il ne démarre ni n'arrête
rien — basculer est donc instantané, et le flux qu'on quitte ne se fige pas.

| Accès | Ce qu'il donne | Ce qu'il coûte |
|---|---|---|
| **Quantower** (défaut) | Carnet L2 complet + côté agresseur **fourni par le marché** (couverture mesurée : 100 %) → les 4 couches s'affichent, le footprint est **exact**. | Rien. Passe par les événements bruts du BusinessLayer, que l'abonnement payant ne verrouille pas. |
| **IBKR** | Mode **dégradé** : footprint et scatter en différé. Heatmap et DOM **vides**, côté agresseur **inféré** (règle du tick). | Un abonnement CME (L1 + Depth) lèverait la dégradation. |
| **Démo** | Générateur synthétique — valide l'affichage marché fermé. | Aucune connexion. |

Un accès en panne (Quantower fermé, TWS éteint) laisse sa vue **vide** sans gêner les
autres, et se reconnecte tout seul en tâche de fond.

**Rithmic n'existe que DANS Quantower** (son mot de passe n'est pas déchiffrable hors du
process) : d'où le **pont**. Une stratégie C# tourne dans la plateforme et sert le flux en
NDJSON sur un socket local, qu'un client Python stdlib consomme.

```
  [quantower]  Rithmic -> Quantower + stratégie « NQ-ES RealTime » --NDJSON/TCP--> quantower_feed.py --\
  [ibkr]       TWS / IB Gateway --reqMktData/reqMktDepth--> market_data.py -------------------->  >--> FlowStore --> FlowPanel
  [demo]       demo_feed.py (synthétique) ------------------------------------------------------/         (un store par (accès, symbole))
```

## Architecture (fichier par fichier)

| Fichier | Rôle |
|---|---|
| `config.py` | Tous les paramètres : `ACCESS` (table du menu) et `SOURCE` (accès de démarrage), ports du pont, connexion IB, symboles, résolution, fenêtre live, profondeur DOM, cadences, rétention, couleurs. |
| `orderflow_data.py` | **Cœur données** : `Trade`, `Candle` (+ footprint bid/ask par niveau), `build_candles`, et `FlowStore` (ring buffers trades + snapshots de carnet, complétés par le disque pour la heatmap). |
| `flow_view.py` | **Cœur rendu** : `FootprintItem`, axes/viewbox custom et `FlowPanel` (heatmap + scatter + lignes bid/ask + footprint + échelle DOM sur une grille de prix commune). Ne porte aucun réglage. |
| `controls.py` | Popups du bouton `Affichage ⚙` : `LayersPanel` (liste des couches) et `TypeSettings` (visible / opacité / ordre + options du type). |
| `logsetup.py` | Journal `logs/indices.log` (écrasé à chaque lancement). Porté du frère. Console ajoutée **seulement si elle existe** — sous `pythonw`, `sys.stderr` est `None`. |
| `ui.py` | Fenêtre : la barre de commandes + **un seul** `FlowPanel`, rafraîchi par un QTimer. |
| `main.py` | Point d'entrée : **monte les 3 accès** (`BUILDERS`), la classe `Storage`, et `IbkrFeed` (thread + boucle asyncio propres). |
| `quantower_feed.py` | Client du pont : thread + reconnexion à backoff plafonné. **stdlib seule.** |
| `NqFeed/` | Le pont, côté C# : `NqFeedStrategy.cs` (la stratégie « NQ-ES RealTime »), `NqFeedProbeStrategy.cs` (la sonde qui a mesuré le flux), `deploy.ps1`. |
| `backend/` | Le disque : `trade_archive.py`, `book_archive.py`, `recorder.py` (thread d'écriture unique, file bornée, purge de rétention), `book_reader.py` (lecture bornée, hors thread Qt). |
| `ib_connection.py` | Connexion TWS/IB Gateway (`readonly=True`) + résolution du contrat front-month. |
| `market_data.py` | Souscriptions `reqMktData` (ticks) + `reqMktDepth` (DOM) → alimente les `FlowStore`. |
| `demo_feed.py` | Générateur synthétique (QTimer) : marche aléatoire, trades buy/sell, carnet animé. |

## Lancer

**Double-clic**, comme le projet frère — les deux lanceurs visent directement le python de
l'env, sans `conda activate` :

| Fichier | Pour quoi |
|---|---|
| `Lancer.bat` | La console reste ouverte et affiche le journal en direct. À préférer dès qu'il y a un doute. |
| `Lancer (sans console).bat` | Aucune fenêtre qui traîne. |

Dans les deux cas, le journal est écrit dans **`logs\indices.log`** (écrasé à chaque
lancement) par `logsetup.py` — c'est là qu'on lit pourquoi un accès reste vide.

Ou à la main :

```powershell
conda activate indices-flow
python main.py
```

L'app démarre **sans rien d'autre** : le pont et TWS sont facultatifs, la démo tourne
toujours. Pour le flux réel :

```powershell
# 1. Déployer les stratégies (build + copie dans Quantower)
powershell -File NqFeed\deploy.ps1

# 2. Dans Quantower (Rithmic connecté), panneau Strategies :
#    « + » -> NQ-ES RealTime -> Symbole = NQ -> Run -> LAISSER EN WORKING
#    ⚠️ Après tout redéploiement du C# : FERMER ET ROUVRIR Quantower (voir plus bas).

# 3. Valider le pont seul, sans GUI :
python quantower_feed.py --seconds 15
```

Une instance de « NQ-ES RealTime » **par symbole**, chacune sur son port (`config.QT_FEED_PORTS`).
Un symbole sans port a une vue vide, sans gêner les autres.

> ⚠️ **Quantower charge les DLL de stratégie au démarrage et garde l'ancienne en mémoire.**
> Le déploiement réussit sans verrou ni message, mais la plateforme **continue de servir
> l'ANCIENNE** version. Toute modification du C# impose donc de fermer et rouvrir
> Quantower. Corollaire : grouper les changements du pont, et pousser le maximum de
> logique côté Python, rechargeable à volonté.

### Mode réel — TWS / IB Gateway (accès dégradé)

1. Démarre **TWS** (ou IB Gateway) et connecte-toi.
2. **Configure → API → Settings** : autorise l'API socket, ajoute `127.0.0.1` aux
   *Trusted IPs*. Le code se connecte en `readonly=True`.
3. Dans `config.py` : `PORT = 4001` (TWS de ce poste).
4. **Type de données** (`MARKET_DATA_TYPE`) : `1` = temps réel, **requis** pour la heatmap
   et le DOM, nécessite un **abonnement CME** ; `3` = différé (gratuit, ~15 min) →
   footprint et scatter se remplissent, **heatmap et DOM restent vides**
   (`/DEEP not subscribed`).

## Contrôles de l'interface

- **Barre** — `Résolution` (durée d'une bougie, 1 s … 5 min) · `Aller au` (saut à une date)
  · `Accès` (Quantower / IBKR / Démo) · `Affichage ⚙` (réglages des couches) · `● Live`
  (bascule) · `ES` / `NQ` · `✕ Quitter`.
- **Ligne d'état**, sous la barre : dit ce que l'accès courant donne, ou **pourquoi** il ne
  donne rien. C'est le premier endroit à regarder devant un écran vide :
  ```
  ●  Quantower · NQ — dernier tick il y a 0,2 s · 4551 trades en mémoire
  ○  Quantower · NQ — aucune donnée. Le pont ne répond pas sur 127.0.0.1:5555 —
     la stratégie « NQ-ES RealTime » tourne-t-elle dans Quantower, en Working ?
  ```
- **Molette** : zoom du temps (ancré au présent en live). **Ctrl + molette** : zoom des prix.
- **Glisser** : navigue dans l'historique et **quitte le live** (le bouton `● Live` le reflète).
- **`Affichage ⚙`** : par couche (Footprint / Heatmap / Trades / Carnet) — visible, opacité,
  ordre avant/arrière ; options du footprint (barres bid/ask, POC, chiffres, en-tête V/D) ;
  options des trades (taille des points, `Min size`, filtre `Auto`).

## Ce que montre chaque couche

- **Footprint** : chandelier discret + barres horizontales bid (droite) / ask (gauche) par
  niveau de prix, chiffres bid×ask, en-tête **V** (volume) / **D** (delta) par bougie,
  surbrillance des déséquilibres (imbalance ≥ 3×).
- **Heatmap** : intensité de liquidité du carnet dans le temps (snapshots binnés, log).
- **Scatter** : chaque trade = un point (vert acheteur / rouge vendeur), taille ∝ volume.
- **Lignes bid/ask** : meilleur bid (vert) et meilleur ask (rouge) au fil du temps.
- **Échelle DOM** : carnet courant en barres horizontales, aligné sur la grille de prix.

## Le disque

Le carnet n'a **aucun historique téléchargeable** (Rithmic ne sert pas de L2 passé) : la
seule façon d'avoir une heatmap historique est d'**enregistrer le live**. L'historique ne
grandit donc que vers l'avant, pendant les sessions où l'app tourne.

- `data/trades.db` + `data/books.db` (ignorés par git, supprimables app fermée).
- La cadence d'enregistrement (`RECORD_SNAPSHOT_MS`) est **découplée** de celle de
  l'affichage (`SNAPSHOT_MS`) : mesuré, enregistrer à 250 ms coûterait **1,54 Go/jour**
  pour une finesse que la heatmap historique n'affiche jamais.
- `RETENTION_DAYS` borne la taille ; la purge tourne dans le thread du `Recorder`.
- ⚠️ La **démo n'écrit rien** : on ne mélange jamais du synthétique au marché réel.
- ⚠️ Le **footprint reste borné à la mémoire** (`visible_trades` ne lit pas le disque) : à
  ~9 trades/s, une journée pèse ~750 000 trades et les relire à chaque image gèlerait
  l'affichage. La heatmap, elle, remonte loin sans risque — sa lecture est bornée par
  l'affichage (~1000 colonnes), pas par le volume.

## Personnalisation

Tout est dans `config.py` : `ACCESS`, `SOURCE`, `SYMBOLS`, `QT_FEED_PORTS`,
`RESOLUTION_SECONDS`, `LIVE_SPAN_SECONDS`, `DOM_ROWS`, `SNAPSHOT_MS`,
`RECORD_SNAPSHOT_MS`, `RETENTION_DAYS`, `HEAT_OPACITY`, couleurs `BUY`/`SELL`, `TIMEZONE`.
