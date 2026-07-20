# Indices boursiers — Backtesting (LEAN sur NQ)

Stratégies LEAN portées sur NQ (dossier `backtests/algorithms/`), désormais
**autonomes** : toute la chaîne de données vit dans ce mono-dépôt (consolidation du
2026-07-10). Arborescence **miroir** du frère `crypto/backtesting` depuis le
2026-07-19 — mêmes recettes, mêmes emplacements des deux bords.

**Rebuild 1 m (2026-07-19)** : les 8 stratégies portent le re-calibrage anti-frais v2
du frère (entrée 1 m, signaux 3–15 m, cooldowns, stops élargis), rejouées sur la
fenêtre du run BTC — 2026-06-01 → 2026-07-10 — pour une comparaison à conditions
égales (fenêtre bornée dans `nq_instrument.py`). ⚠️ La base de barres n'est profonde
que depuis fin janvier 2026 : avant, l'historique Quantower ne livre qu'un
jour-échantillon par mois.

## La chaîne complète (de la donnée au backtest)

```
../historique/NqExtractor            ticks NQ (Quantower/Rithmic) -> H:\IndicesBoursiers\historique\NQ-<contrat>.db
../historique/normalize_ohlcv.py     barres minute -> H:\IndicesBoursiers\historique\ohlcv\NQ-2026-09\{1m,1H,4H,D}.csv
backtests/volume_profile_features.py   ticks -> features_vp.csv (POC/VAH/VAL par session, cadence 1 min)
backtests/algorithms/*.py            les 8 stratégies LEAN (nq_instrument = frais/levier/lecteur/fenêtre)
                                     + les 3 JUMEAUX des hybrides (orb/sma_suiveur/rsi_bracket, cadre_hybride)
backtests/fig_equite_nq.py           JSON de résultats LEAN -> figures des fiches du site
```

```bash
# régénérer les CSV canoniques (dont le 1m consommé par LEAN) après extension de la base
python ../historique/normalize_ohlcv.py --dir H:\IndicesBoursiers\historique\ohlcv\NQ-2026-09 \
       --prefix NQ-CME --bars-db H:\IndicesBoursiers\historique\NQ-2026-09-1m.db
# régénérer les features volume profile (cadence 1 min) après extension de la base de ticks
python backtests/volume_profile_features.py          # défauts NQ (--db/--out/--tick sinon)
```

## Lancer un backtest (montage LEAN)

Pas de montage local : le **Launcher compilé du frère crypto** sert tel quel —
`--algorithm-location` accepte n'importe quel chemin, seul le CSV monté change
(chemins absolus H: dans `nq_instrument.py`).

```bash
cd ../../crypto/backtesting/backtests/lean/Launcher/bin/Release
export PYTHONNET_PYDLL=$HOME/miniconda3/envs/backtesting/python311.dll
export PYTHONHOME=$HOME/miniconda3/envs/backtesting
dotnet QuantConnect.Lean.Launcher.dll --algorithm-language Python \
    --algorithm-type-name BuyHoldNq \
    --algorithm-location <ce dépôt>/backtesting/backtests/algorithms/buyhold_nq.py \
    --results-destination-folder <dossier de sortie> --close-automatically true
```

⚠️ Sur data custom, LEAN **sort en code 82 mais écrit ses résultats** : le juge de
réussite est la présence du `<Classe>-summary.json`. Le crash `GILState` en toute fin
est cosmétique. Env : conda `backtesting` (Python 3.11) — voir
`crypto/backtesting/requirements.txt` (source unique).

## Modules

- `backtests/algorithms/nq_instrument.py` — tout ce qui DIFFÈRE du monde crypto :
  lecteur du CSV canonique 1 m, **modèle de frais** (commission fixe par
  contrat/side), SymbolProperties (tick 0,25 ×20), levier, **fenêtre du run**
  (bornes explicites, alignées sur le frère), et `viser()` — le sizing en contrats
  ENTIERS (l'équivalent futures du `set_holdings(±1.0)` crypto). Les stratégies
  restent identiques au frère.
- `backtests/algorithms/{orb,sma_suiveur,rsi_bracket}_nq.py` — les **jumeaux backtest
  des 3 stratégies hybrides** du pilier automatisation (volet C, 2026-07-19 ; specs :
  `../automatisation/docs/strategies-hybrides.md`). SL/TP/suiveur **simulés dans la
  boucle 1 m** (patron `risque_stops_nq.py`) sous le cadre de séance des specs
  (entrées 09:30-15:30 ET, flat forcé 16:55 ET, garde-fou 2 pertes pleines/jour,
  cooldown 15 min). Chaque jumeau écrit un **journal de décisions NDJSON** (un fichier
  par jour ET, dans `backtests/journaux/<strategie>/`, hors git) — c'est CE fichier que
  la phase 4 (shadow) comparera au live. **Référence de décisions, PAS un verdict de
  performance.** Les 8 du banc ne bougent pas.
- `backtests/algorithms/cadre_hybride.py` — le cadre COMMUN des 3 jumeaux (même rôle
  que `nq_instrument.py`) : heures de séance en ET (zoneinfo), garde-fou journalier,
  cooldown, écrivain du journal NDJSON.
- `backtests/sessions.py` — découpage Asia/London/NY en heure de New York. ⚠️ **Miroir**
  de `crypto/backtesting/backtests/sessions.py` (même chemin des deux bords) : reporter
  toute correction dans les deux.
- `backtests/volume_profile_features.py` — features POC/VAH/VAL par session depuis les
  ticks, **cadence 1 minute** (refonte du frère reportée le 2026-07-19 ; défauts NQ —
  niveaux de 5 pts).
- `backtests/fig_equite_nq.py` — les figures des fiches du site (courbe d'équité +
  frontière IS/OOS + Buy & Hold en référence), relues depuis les JSON de résultats.

## Résultats & parité

- 8 backtests 1 m / 8 menés (2026-07-19), fenêtre 2026-06-01 → 2026-07-10, verdict
  hors échantillon compris : voir les fiches du pilier Backtesting sur le site
  (`site-content/contenu.json`). Le sol de vérité Buy & Hold (équité recalculée à la
  main) colle à LEAN à 0,000000 $ près.
- L'ancienne campagne 1H (Phase 2) et ses écarts vs BTC :
  [`../automatisation/docs/phase2-backtests-nq.md`](../automatisation/docs/phase2-backtests-nq.md).
- Rapports de parité indicateurs C# vs pipeline Python : `../automatisation/docs/phase3-*.md`.

## Environnement

Le même conda `backtesting` que le frère crypto — voir
`crypto/backtesting/requirements.txt` (source unique, rien à installer de plus ici).
