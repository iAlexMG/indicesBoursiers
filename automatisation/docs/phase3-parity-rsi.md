# Phase 3 — Rapport de parité #2 : RSI 14 (natif Quantower vs Python)

> Deuxième test de parité, cette fois sur un indicateur à **lissage** (Wilder) et **seed** de
> warmup — le cas où l'on ATTEND une divergence, à quantifier et expliquer (raison d'être du test).

## Protocole

- **C# Quantower** : indicateur `RSI NQ (14, natif)` — héberge le RSI **natif**
  (`Core.Instance.Indicators.BuiltIn.RSI(14, Close, RSIMode.Exponential, MaMode.SMMA, …)`,
  soit le lissage de Wilder) ; exporte `time,close,rsi` par barre.
- **Python** : `indicators/parity_rsi.py` calcule DEUX références sur `1H.csv` :
  **Wilder** (SMMA récursif, comme LEAN `MovingAverageType.WILDERS`) et **Cutler** (SMA glissante).

## Résultat mesuré (2026-07-09)

- 322 barres appariées. **Découverte annexe** : l'export Quantower remonte au **13 avril**
  (1446 barres horaires) — l'historique **horaire** de Rithmic est bien plus profond que
  l'historique **tick** (~2 semaines, Phase 0). Utile pour les indicateurs.
- **Lissage identifié** : écart max vs **Wilder = 7,25** contre **Cutler = 39,4** → le RSI natif
  Quantower est bien du **Wilder** (SMMA).
- **L'écart de 7,25 est entièrement au démarrage** (18/06, mes premières barres) puis **converge
  vers 0** : zone convergée (après 40 barres) = écart **max 1,62 / moyen 0,089** ; plusieurs
  barres finales à **0,000**. Le 1,62 résiduel = dernière barre partielle (même artefact que la parité SMA).

## Conclusion

**La formule est la MÊME (Wilder des deux côtés).** L'écart apparent n'est pas une erreur de
calcul mais le **seed de warmup** : le RSI de Wilder dépend de tout l'historique amont ;
Quantower a des mois de données avant le 18/06 (RSI convergé), tandis que la référence Python
démarre au 18/06 avec 14 barres de seed → ses premières valeurs diffèrent, puis l'écart décroît à
~0 en ~40 barres. **Mesuré, pas deviné** — exactement l'enseignement visé.

Implication pratique : pour un shadow-mode/live fidèle (Phase 4), fournir aux indicateurs un
**warmup suffisant** (charger assez d'historique avant de faire confiance aux valeurs) ; les
premières dizaines de barres après un cold start ne sont pas fiables pour le RSI.

## Suite

- Parité **EMA / MACD** (seed EMA — même famille de divergence).
- **VP par session** (POC/VAH/VAL, delta) vs `features_vp.csv`.
- Indicateur **signaux** par stratégie (marqueurs entrée/sortie + régime).
