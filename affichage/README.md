# IBKR — Orderflow / Footprint / DOM (ES & NQ)

Programme Python qui affiche, pour les futures **ES** et **NQ**, un **dashboard natif**
(PyQt5 + pyqtgraph) reproduisant les graphiques du projet *Crypto* : **un seul graphe**
par symbole où sont superposés un **footprint** (type Quantower), un **orderflow**
(heatmap de liquidité + scatter des trades + lignes best bid/ask, style Bookmap) et une
**échelle DOM** (carnet courant) à droite, alignée sur la même grille de prix.

Deux sources de données interchangeables :
- **Réel** : connexion **TWS / IB Gateway** via `ib_insync` (flux temps réel ES/NQ).
- **Démo** : générateur de données synthétiques (aucune connexion) — pour voir les
  graphiques bouger même marché fermé ou sans abonnement L2.

> Voir [docs/conception.md](docs/conception.md) pour la vision, les choix de conception,
> les embûches rencontrées et la **contrainte de données IBKR**.
> Dernière mise à jour : **2026-06-29**.

## Architecture (fichier par fichier)

| Fichier | Rôle |
|---|---|
| `config.py` | Tous les paramètres : `DEMO_MODE`, connexion (PORT, `MARKET_DATA_TYPE`), symboles, résolution footprint, fenêtre live, profondeur DOM, cadence des snapshots, couleurs. |
| `orderflow_data.py` | **Cœur données** : `Trade`, `Candle` (+ footprint bid/ask par niveau), `build_candles`, et `FlowStore` (ring buffers en mémoire des trades et des snapshots de carnet, inférence du côté agresseur). |
| `flow_view.py` | **Cœur rendu** : `FootprintItem`, axes/viewbox custom (`TzDateAxis`, `PriceAxis`, `FlowViewBox`) et `FlowPanel` (heatmap + scatter + lignes bid/ask + footprint + échelle DOM sur une grille de prix commune). |
| `ib_connection.py` | Connexion TWS/IB Gateway (`readonly=True`) + résolution du contrat front-month. |
| `market_data.py` | Souscriptions `reqMktData` (ticks) + `reqMktDepth` (DOM) → alimente les `FlowStore`. |
| `demo_feed.py` | Générateur synthétique (QTimer) : marche aléatoire du prix, trades buy/sell, carnet animé → `FlowStore`. |
| `ui.py` | Fenêtre : `QTabWidget` (un onglet ES, un onglet NQ), un `FlowPanel` par symbole, refresh de l'onglet actif. |
| `main.py` | Point d'entrée : branche le mode **démo** ou **réel** selon `config.DEMO_MODE`. |

Le flux de données est identique dans les deux modes — seule la **source** change :

```
   [réel]  TWS/IB Gateway --reqMktData/reqMktDepth--> market_data.py --\
                                                                        >--> FlowStore --> FlowPanel (UI)
   [démo]  demo_feed.py (synthétique) ----------------------------------/
```

## Lancer (environnement conda dédié)

```powershell
conda activate ibkr
python main.py
```

- **Par défaut** : `config.DEMO_MODE = True` → la fenêtre s'ouvre immédiatement avec des
  données synthétiques (onglets ES/NQ animés). Aucune connexion requise.
- **Données réelles** : mettre `DEMO_MODE = False`. Voir ci-dessous.

Réinstallation des dépendances si besoin : `pip install -r requirements.txt`
(`ib_insync`, `pyqtgraph`, `PyQt5`, numpy).

### PyCharm — interpréteur conda
File ▸ Settings ▸ Project ▸ **Python Interpreter** → l'env conda **`ibkr`**
(`C:\Users\Moi\anaconda3\envs\ibkr\python.exe`), puis lancer `main.py`.

## Mode réel — TWS / IB Gateway

1. Démarre **TWS** (ou IB Gateway) et connecte-toi.
2. **Configure → API → Settings** : autorise l'API socket, ajoute `127.0.0.1` aux
   *Trusted IPs*. Le code se connecte en `readonly=True` (compatible « Read-Only API » coché).
3. Dans `config.py` : `PORT = 4001` (TWS de ce poste), `DEMO_MODE = False`.
4. **Type de données** (`MARKET_DATA_TYPE`) :
   - `1` = temps réel — **requis** pour la heatmap et l'échelle DOM (carnet L2), nécessite
     un **abonnement aux données CME** (L1 + Depth/L2).
   - `3` = différé (gratuit, ~15 min) — footprint et scatter se remplissent depuis les
     trades ; **heatmap et DOM restent vides** (pas de L2 → `/DEEP not subscribed`).

> Le **carnet d'ordres** (DOM) et la **heatmap** n'existent qu'en **temps réel**.
> Le footprint marche aussi en différé. Hors heures de marché CME (week-end), aucun
> tick ne circule : utilise le **mode démo** pour valider l'affichage.

## Contrôles de l'interface

- **Molette** : zoom du temps (ancré au présent en live).
- **Ctrl + molette** : zoom des prix.
- Bouton **⟳ Live** : revenir au suivi temps réel + axe des prix automatique.
- Cases **Footprint / Heatmap / Trades / Carnet** : afficher/masquer chaque couche.
- Menu **Résolution** : durée d'une bougie footprint (1 s … 5 min).
- Onglets **ES / NQ** : un symbole par onglet (seul l'onglet actif est rafraîchi).

## Ce que montre chaque couche

- **Footprint** : chandelier discret + barres horizontales bid (droite) / ask (gauche)
  par niveau de prix, chiffres bid×ask, en-tête **V** (volume) / **D** (delta) par bougie,
  surbrillance des déséquilibres (imbalance ≥ 3×).
- **Heatmap** : intensité de liquidité du carnet dans le temps (snapshots binnés, log).
- **Scatter** : chaque trade = un point (vert acheteur / rouge vendeur), taille ∝ volume.
- **Lignes bid/ask** : meilleur bid (vert) et meilleur ask (rouge) au fil du temps.
- **Échelle DOM** : carnet courant en barres horizontales, aligné sur la grille de prix.

## Personnalisation

Tout est dans `config.py` : `SYMBOLS`, `RESOLUTION_SECONDS`, `LIVE_SPAN_SECONDS`,
`DOM_ROWS`, `SNAPSHOT_MS`, `HEAT_OPACITY`, couleurs `BUY`/`SELL`, `TIMEZONE`.
