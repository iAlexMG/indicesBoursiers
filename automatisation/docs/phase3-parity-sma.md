# Phase 3 — Rapport de parité #1 : SMA 50/200 (C# Quantower vs Python)

> Premier test de parité de la Phase 3, sur le cas **le plus simple** (SMA = moyenne simple,
> aucune ambiguïté de seed/lissage). But : valider la **méthode** de parité avant les indicateurs
> à vraies divergences de formule (RSI Wilder, EMA seed) puis la VP par session.

## Protocole

- **C# Quantower** : indicateur `SMA Cross NQ (50/200)` sur un graphe NQ 1H (flux Rithmic live),
  exporte `time,close,sma_rapide,sma_lente,signal` par barre → `F:\data\parity\NQ-sma-quantower.csv`.
- **Python (référence)** : `indicators/parity_sma.py` recalcule les SMA sur `F:\data\ohlcv\NQ-2026-09\1H.csv`
  (issu de notre pipeline de ticks). Comparaison **appariée par horodatage UTC**.

## Résultat mesuré (2026-07-09)

- 107 barres appariées. **97/107 aux closes RIGOUREUSEMENT identiques** → SMA identique.
- Écart max : SMA 50 = **11,21 pt**, SMA 200 = **3,24 pt** (~0,04 % du prix).
- **10 barres aux closes différents**, deux causes :
  1. **9 barres : écart ≤ 2,75 pt (≤ 11 ticks)** — un tick sur la frontière d'heure rangé
     différemment (extracteur en bornes ms vs agrégation Quantower). Sub-tick à quelques ticks.
  2. **1 barre : +42,75 pt (08/07 23:00)** — **dernière barre partielle** au moment du snapshot
     (collecte arrêtée à 23:07 ; Quantower a l'heure complète). Se corrige à la collecte suivante.
- 8 barres Quantower sans correspondance = barres du 09/07 plus récentes que notre base.

## Conclusion

**La formule est exacte** (closes identiques ⇒ SMA identique). Les écarts résiduels sont
purement des différences de **données** aux frontières (tick de bord d'heure) et l'effet de
**barre courante incomplète** — pas un défaut de calcul. La méthode de parité (export C# →
comparaison Python appariée par temps, avec le `close` comme juge) est **validée**.

## Suite

- Parité **RSI (Wilder)** et **EMA (seed)** : là, on ATTEND une divergence de formule à quantifier
  et expliquer (c'est la raison d'être du test — indicateurs natifs ≠ implémentation Python).
- Parité **VP par session** (POC/VAH/VAL, delta) vs `features_vp.csv`.
