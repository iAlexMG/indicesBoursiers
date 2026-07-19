# Indicators — indicateurs Quantower C# (Phase 3)

Indicateurs custom déployés dans `C:\Quantower\Settings\Scripts\Indicators\`. Phase **visuelle**
(rendu sur le graphe NQ) → construction collaborative. Chaque indicateur pertinent **exporte** ses
valeurs pour un **test de parité** headless vs le pipeline Python (le réflexe du projet frère).

## `SMA Cross NQ (50/200)` — 1er indicateur (validation chaîne + parité)

Croisement SMA 50/200 : deux lignes superposées au prix + flèches d'entrée/sortie aux croisements
(même logique que le backtest `sma_croisement_nq`). SMA = moyenne simple → **parité exacte
attendue** avec Python.

**Résolution de calcul indépendante de l'affichage** (`Résolution de calcul`, défaut **1 h**) :
les SMA se calculent sur un historique à résolution fixe (via `Symbol.GetHistory`) et se projettent
en marches sur le graphe → afficher en 1 min ne change pas les moyennes (elles restent des SMA
50/200 horaires). `Graphe` = ancien comportement (calcul sur le timeframe affiché).

**Déployer :** `powershell -File indicators\SmaCrossNq\deploy.ps1` (ou build + copie manuelle).

**Utiliser :** dans Quantower, ouvrir un graphe **NQ en 1H** → clic droit → *Add indicator* →
`SMA Cross NQ (50/200)`. L'indicateur trace les 2 SMA, pose les flèches, et (si `ExportParite`)
écrit `H:\IndicesBoursiers\parity\NQ-sma-quantower.csv`.

**Vérifier la parité :**
```powershell
python indicators\parity_sma.py
```
Compare l'export Quantower au calcul Python sur `1H.csv` (écart max des SMA + signaux). Un écart
non nul mais faible = différence de **données** (agrégation OHLCV / bornes de barre / fuseau du
graphe), pas de formule — à quantifier et expliquer.

## `RSI NQ (14, natif)` — 2e indicateur (parité de lissage)

Héberge le **RSI natif** de Quantower (`Core.Instance.Indicators.BuiltIn.RSI`, mode Wilder/SMMA)
en fenêtre séparée + niveaux 30/70. Exporte `time,close,rsi` → `H:\IndicesBoursiers\parity\NQ-rsi-quantower.csv`.
Contrairement à la SMA, le RSI a un **lissage** et un **seed** → parité intéressante.

**Utiliser :** graphe NQ 1H → *Add indicator* → `RSI NQ (14, natif)`.
**Vérifier :** `python indicators\parity_rsi.py` — compare à DEUX références (Wilder SMMA et
Cutler SMA) pour identifier le lissage de Quantower et l'écart de seed.

## `VP Session NQ (ticks)` — Volume Profile par session (footprint maison)

Le volume analysis natif de Quantower est **payant** → on calcule le footprint nous-mêmes depuis
les **ticks Rithmic gratuits** (`GetTickHistory`, tâche de fond), portage de
`volume_profile_features.py` (sous-briques 30 min, niveaux 5 pts, sessions NY, value area 70 %).
Rendu (`OnPaintChart`) **par session** : un encadré (période × plage de prix) + l'histogramme
volume-at-price **aligné à gauche** (chevauche la session) + 2 lignes VAH/VAL.

Deux axes indépendants :
- **Granularités superposables** (cases à cocher) : `VP par session` (défaut) · `Sous-VP 1 h` ·
  `Sous-VP 30 min` — plusieurs à la fois (ex. session + 30 min), chacune avec sa couleur d'encadré,
  le plus fin dessiné par-dessus (leçon 09 du frère : la session se construit de sous-briques 30 min).
- **« Affichage »** : `Volume (buy/sell)` · `Volume total` · **`Delta`** (défaut) — le rendu des barres.

Extensible : +1 valeur d'enum + 1 paire dans les `variants` + 1 `case`. Changer les granularités/
couverture recalcule à partir des ticks en cache (pas de re-téléchargement).

**Utiliser :** graphe NQ 1H → *Add indicator* → `VP Session NQ (ticks)`.
**Parité :** `python indicators\parity_vp.py` compare session/delta/POC/VAH/VAL à `features_vp.csv`.

## `EMA NQ (native)` — EMA à résolution fixe

EMA avec **« Résolution de calcul »** (défaut 1 h, comme la SMA) : indépendante de l'affichage.
Mode `Graphe` = héberge l'EMA **native** (`BuiltIn.EMA`) sur le timeframe affiché → sert au test de
parité (native vs Python, seed). Modes fixes = EMA calculée sur l'historique de cette résolution
(seed SMA, lissage 2/(N+1)) et projetée → afficher en 1 min ne change pas l'EMA horaire.
Parité : `python indicators\parity_ema.py` (2 conventions de seed).

## `Signaux NQ (strat. avancée)` — marqueurs entrée/sortie sur le graphe

Reproduit sur le graphe la logique de la stratégie n°7 (`strategie_avancee_nq`) : régime SMA 200
(ligne orange), entrée sur croisement MACD confirmé RSI>50 en régime haussier, sorties stop
suiveur 2×ATR / take 4×ATR / casse de régime. Marqueurs : flèche verte (entrée), rouge=stop /
verte=take / orange=régime (sortie). Indicateurs **natifs** réutilisés (SMA/EMA/RSI/ATR ;
MACD=EMA12−EMA26, signal=EMA9). À utiliser sur un graphe **NQ 1 h**. Exporte les signaux
(`NQ-signaux-quantower.csv`) → future concordance avec le backtest LEAN (Phase 4).
- Indicateur **VP par session** (POC/VAH/VAL barre à barre via `VolumeAnalysisData.PriceLevels`)
  + **delta EMA24** → parité vs `features_vp.csv`.
- Indicateur **signaux** par stratégie (marqueurs entrée/sortie + régime sur le graphe NQ).
