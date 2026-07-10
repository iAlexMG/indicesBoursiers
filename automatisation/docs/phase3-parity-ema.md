# Phase 3 — Rapport de parité #4 : EMA (Quantower vs Python)

> EMA avec résolution de calcul fixe (indépendante de l'affichage). Comme le RSI, l'EMA a un
> lissage récursif + un seed → on quantifie l'écart. Testée en **30 min** (résolution ≠ 1 h).

## Protocole

- **C# Quantower** : indicateur `EMA NQ (native)`, `Résolution = 30 min`. En mode fixe, l'EMA est
  calculée sur l'historique 30 min (`Symbol.GetHistory(MIN30)`), seed = SMA, lissage 2/(N+1),
  projetée sur le graphe quel que soit son timeframe. Export `time,close,ema`.
- **Python** : `indicators/parity_ema.py --resolution-min 30` — clôtures 30 min reconstruites depuis
  la base de ticks, EMA calculée avec 2 conventions de seed (SMA et first-price).

## Résultat mesuré (2026-07-10)

- 654 barres 30 min appariées. **Seed = SMA** (écart 3,92 vs 76,7 pour first-price).
- **Zone convergée : écart max 3,92 · moyen 0,22.** Résiduel piloté par **45 barres sur 654 aux
  clôtures 30 min différentes** (jusqu'à 41 pt) — ticks de frontière + érosion de l'historique tick
  Rithmic sur les vieilles barres (base vs re-fetch), l'EMA récursive propage puis amortit l'écart.

## Conclusion

**Formule EMA correcte** (lissage 2/(N+1), seed SMA) et **résolution-indépendance validée** (l'EMA
est calculée en 30 min et projetée ; changer le timeframe d'affichage ne la modifie pas). Les écarts
résiduels sont des différences de **données** (clôtures de bord), pas de calcul — même signature que
les parités SMA et VP. Pour comparer au **seed natif** de Quantower, utiliser le mode `Résolution =
Graphe` (héberge `BuiltIn.EMA`) sur un graphe 1 h.
