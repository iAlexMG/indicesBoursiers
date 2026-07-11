# Indices boursiers — Backtesting (LEAN sur NQ)

Stratégies LEAN portées sur NQ (dossier `nq/`), désormais **autonomes** : toute la
chaîne de données vit dans ce mono-dépôt (consolidation du 2026-07-10).

## La chaîne complète (de la donnée au backtest)

```
../historique/NqExtractor            ticks NQ (Quantower/Rithmic) -> F:\data\NQ-<contrat>.db
../historique/normalize_ohlcv.py     candles -> F:\data\ohlcv\NQ-2026-09\{1H,4H,D}.csv
backtesting/volume_profile_features.py   ticks -> features_vp.csv (POC/VAH/VAL par session)
backtesting/nq/*.py                  les 8 stratégies LEAN (nq_instrument = frais/levier/lecteur)
```

```bash
# régénérer les features volume profile après une extension de la base de ticks
python backtesting/volume_profile_features.py          # défauts NQ (--db/--out/--tick sinon)
```

## Modules

- `nq/nq_instrument.py` — tout ce qui DIFFÈRE du monde crypto : lecteur du CSV canonique,
  **modèle de frais** (commission fixe par contrat/side), SymbolProperties (tick 0,25 ×20),
  levier. Les stratégies restent identiques au frère.
- `sessions.py` — découpage Asia/London/NY en heure de New York. ⚠️ **Miroir** de
  `crypto/backtesting/backtests/sessions.py` : reporter toute correction dans les deux.
- `volume_profile_features.py` — features POC/VAH/VAL par session depuis les ticks
  (adapté du frère : mêmes algorithmes, défauts NQ — niveaux de 5 pts). Vérifié le
  2026-07-10 : régénération **identique bit à bit** au `features_vp.csv` produit par la
  chaîne d'origine.

## Résultats & parité

- 8 backtests / 8 menés (Phase 2) : tableau et écarts vs BTC dans
  [`../automatisation/docs/phase2-backtests-nq.md`](../automatisation/docs/phase2-backtests-nq.md).
- Rapports de parité indicateurs C# vs pipeline Python : `../automatisation/docs/phase3-*.md`.

## Reste à faire

- Le montage LEAN (conteneur, `config.json`, montage du CSV) n'est pas versionné ici —
  documenter/scripter un `sync_spec` équivalent au frère quand les backtests NQ reprendront.
