# Indices boursiers — Backtesting (LEAN sur NQ)

Stratégies LEAN portées sur NQ (dossier `backtests/algorithms/`), désormais
**autonomes** : toute la chaîne de données vit dans ce mono-dépôt (consolidation du
2026-07-10). Arborescence **miroir** du frère `crypto/backtesting` depuis le
2026-07-19 — mêmes recettes, mêmes emplacements des deux bords.

## La chaîne complète (de la donnée au backtest)

```
../historique/NqExtractor            ticks NQ (Quantower/Rithmic) -> H:\IndicesBoursiers\historique\NQ-<contrat>.db
../historique/normalize_ohlcv.py     candles -> H:\IndicesBoursiers\historique\ohlcv\NQ-2026-09\{1H,4H,D}.csv
backtesting/backtests/volume_profile_features.py   ticks -> features_vp.csv (POC/VAH/VAL par session)
backtesting/backtests/algorithms/*.py    les 8 stratégies LEAN (nq_instrument = frais/levier/lecteur)
```

```bash
# régénérer les features volume profile après une extension de la base de ticks
python backtesting/backtests/volume_profile_features.py    # défauts NQ (--db/--out/--tick sinon)
```

## Modules

- `backtests/algorithms/nq_instrument.py` — tout ce qui DIFFÈRE du monde crypto : lecteur
  du CSV canonique, **modèle de frais** (commission fixe par contrat/side),
  SymbolProperties (tick 0,25 ×20), levier. Les stratégies restent identiques au frère.
- `backtests/sessions.py` — découpage Asia/London/NY en heure de New York. ⚠️ **Miroir**
  de `crypto/backtesting/backtests/sessions.py` (même chemin des deux bords) : reporter
  toute correction dans les deux.
- `backtests/volume_profile_features.py` — features POC/VAH/VAL par session depuis les ticks
  (adapté du frère : mêmes algorithmes, défauts NQ — niveaux de 5 pts). Vérifié le
  2026-07-10 : régénération **identique bit à bit** au `features_vp.csv` produit par la
  chaîne d'origine.

## Résultats & parité

- 8 backtests / 8 menés (Phase 2) : tableau et écarts vs BTC dans
  [`../automatisation/docs/phase2-backtests-nq.md`](../automatisation/docs/phase2-backtests-nq.md).
- Rapports de parité indicateurs C# vs pipeline Python : `../automatisation/docs/phase3-*.md`.

## Environnement

Le même conda `backtesting` que le frère crypto — voir
`crypto/backtesting/requirements.txt` (source unique, rien à installer de plus ici).

## Reste à faire

- Le montage LEAN (conteneur, `config.json`, montage du CSV) n'est pas versionné ici —
  documenter/scripter un `sync_spec` équivalent au frère quand les backtests NQ
  reprendront. Son foyer prévu : `backtests/lean/` (déjà ignoré par git, comme chez le
  frère).
