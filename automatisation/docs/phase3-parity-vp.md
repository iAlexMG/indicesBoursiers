# Phase 3 — Rapport de parité #3 : Volume Profile par session (C# Quantower vs Python)

> Parité de la pièce signature : l'indicateur `VP Session NQ (ticks)` (footprint calculé maison
> depuis les ticks) vs `features_vp.csv` (volume_profile_features.py sur nos ticks). Colonnes
> comparées : session, delta, POC, VAH, VAL — appariées par horodatage UTC.

## Résultat mesuré (2026-07-09)

Sur 225 barres appariées :

| Grandeur | Résultat |
|---|---|
| **Session** (asia/london/ny/hors) | **225/225 = 100 %** identique |
| **Delta** par barre | **écart max 0.0** (exact) |
| **POC** | écart **moyen 1,96 pt** · max 105 (6 barres > 20 pt) |
| **VAH** | moyen 2,40 pt · max 140 |
| **VAL** | moyen 1,29 pt · max 70 |

## Analyse des écarts POC/VAH/VAL

219 barres sur 225 à ≤ 20 pt. Les **6 barres aberrantes sont toutes sur UNE session** (Asia du
25/06) : le compteur `barres` y diffère (Python=4 vs Quantower=2) et l'écart **converge vers 0**
au fil de la session (105→100→100→50→50→30 pt).

**Cause** : cette session (25/06) est à **~15 jours = le mur de profondeur tick de Rithmic (~2
semaines, mesuré Phase 0)**. `features_vp.csv` a été calculé depuis notre base, remplie le 08/07,
qui avait alors TOUS les ticks de l'ouverture de cette session. L'indicateur, lui, re-télécharge
les ticks *aujourd'hui* (09/07) et en obtient **moins** pour cette vieille session (les ticks les
plus anciens tombent) → moins de barres accumulées au départ → POC/VAH/VAL légèrement décalés au
début, puis identiques. **Les sessions récentes coïncident.**

## Conclusion

**Le calcul de VP par session est correct** : sessions 100 %, delta exact, POC/VAH/VAL au ~2 pt
près (1 niveau de 5 pt). Les seuls écarts notables sont sur la session la plus ancienne, par
**érosion de l'historique tick de Rithmic** (pas un bug de calcul) — et ils s'estompent dans la
session. C'est la démonstration concrète de l'intérêt de la **collecte incrémentale** (Phase 1) :
notre base **conserve** des ticks que Rithmic finit par ne plus servir.

## Note SMA (résolution fixe)

En parallèle, la SMA passée en **résolution de calcul 1 h** (indépendante de l'affichage) donne
la **même parité** qu'avant (écart max SMA 50 = 11 pt, SMA 200 = 3 pt, dus aux ticks de bord et à
la dernière barre partielle) : changer le timeframe d'affichage ne modifie pas les moyennes.
Rapport SMA détaillé : [phase3-parity-sma.md](phase3-parity-sma.md).
