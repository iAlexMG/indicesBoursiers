# Phase 3 — Indicateurs Quantower (C#) : plan mesuré

> Phase **visuelle** : contrairement aux Phases 0-2 (headless, que je vérifie seul), les
> indicateurs se dessinent sur le graphe NQ → construction **collaborative** (tu lances Quantower,
> tu me montres le rendu, on itère). Ce plan est ancré sur l'API réelle (dump réflexif).

## API mesurée (types clés du BusinessLayer)

- **`Indicator`** (base) : surcharges `OnInit()`, `OnUpdate(UpdateArgs)` (par barre),
  `OnPaintChart(PaintChartEventArgs)` (dessin libre : `Graphics`, `LeftVisibleBarIndex`…),
  `OnClear()`. Déployé en DLL net8.0 dans `Settings\Scripts\Indicators\` (comme les Strategies).
- **`LineSeries`** (via `AddLineSeries`) : `SetValue`/`GetValue` par barre → tracer POC/VAH/VAL,
  delta EMA. `SetMarker(index, IndicatorLineMarker)` pour les **marqueurs de signaux**
  (`IndicatorLineMarkerIconType` = `UpArrow, DownArrow, Flag, FillCircle, …`).
- **`VolumeAnalysisData`** (par barre, via `IHistoryItem.VolumeAnalysisData`) :
  - `.Total` → `BuyVolume`/`SellVolume`/delta de la barre ;
  - `.PriceLevels` = `Dictionary<double, VolumeAnalysisItem>` → **le footprint** (volume acheteur/
    vendeur par niveau de prix) = la matière du Volume Profile.
  - Calcul déclenché par `VolumeAnalysisManager.CalculateProfile(HistoricalData, params)` ou
    `HistoricalData.CalculateVolumeProfile(...)` (⚠️ nécessite les ticks ; sur NQ Rithmic, OK).

## Livrables Phase 3

1. **Indicateur « VP par session »** (POC/VAH/VAL développés barre à barre, gelés hors session) :
   - agréger `VolumeAnalysisData.PriceLevels` depuis l'ouverture de la session courante
     (Asia/London/NY, heure NY — mêmes bornes que `backtests/sessions.py`) ;
   - value area 70 % (même algo glouton que `volume_profile_features.py`) → 3 `LineSeries`.
2. **Indicateur « delta EMA24 »** : delta par barre (`VolumeAnalysisData.Total`) → EMA 24 → 1 `LineSeries`.
3. **Indicateur « signaux »** (1 par stratégie portée) : indicateurs natifs Quantower
   (SMA/MACD/RSI/ATR — **ne PAS réécrire**) → marqueurs d'entrée/sortie + régime sur le graphe NQ.

## Test de parité (le cœur de la phase)

Sur le **même historique** : valeurs C# Quantower **≡** valeurs Python du pipeline
(`features_vp.csv` déjà généré en Phase 2 : `time,session,barres,delta,poc,vah,val`).
Méthode : l'indicateur **exporte** ses POC/VAH/VAL/delta par barre dans un CSV, puis un petit
script compare colonne à colonne à `features_vp.csv`. **Écart quantifié et expliqué**, pas
« corrigé à l'aveugle » :
- seed/lissage des EMA (Wilder RSI vs EMA standard) — formules natives ≠ Python ;
- granularité des niveaux (5 pts NQ) et bornes de session (30 min vs tick exact) ;
- causalité (barre `t` connaissable à `t+1h`).

→ **Fini quand** : indicateurs déployés (`Settings\Scripts\Indicators\`), captures sur graphe NQ,
rapport de parité chiffré.

## Prérequis côté utilisateur

- Quantower ouvert, graphe **NQ 1H**, connexion Rithmic active.
- Volume analysis activable sur le graphe (l'indicateur peut le déclencher via l'API).
- Itération visuelle : je construis/déploie, tu lances et me montres le rendu, on ajuste.
