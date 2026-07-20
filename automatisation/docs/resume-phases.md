# Résumé — les phases & les stratégies de trading

> **MAJ 2026-07-19 (pivot)** : les phases 4-5 ne porteront **pas** les 8 stratégies du
> backtesting telles quelles — elles visent **3 stratégies hybrides** (entrées simples +
> SL/TP en ordres attachés + cadre Apex), spécifiées dans
> [strategies-hybrides.md](strategies-hybrides.md). Les 8 restent la référence du pilier
> backtesting. **Objectif assumé : preuve de concept et de fonctionnalités, PAS la
> rentabilité** (historique trop court pour des backtests crédibles). Détail du pivot :
> [journal.md](journal.md), entrée 2026-07-19.

> Vue d'ensemble en langage simple. Pour la distinction *indicateur/stratégie/Visual Studio*,
> voir [guide-architecture-quantower.md](guide-architecture-quantower.md).

## Le fil rouge du projet

Tu as déjà, dans le projet frère (`../Backtesting`), **8 algorithmes de trading** testés sur du
Bitcoin. **Ce projet-ci les amène sur le NQ (futures Nasdaq) en réel**, étape par étape, en
réutilisant un maximum de ce qui existe. On ne saute jamais une étape : chaque phase a une
« définition de fini », et on avance du plus sûr (mesurer, backtester) au plus risqué (envoyer
de vrais ordres).

## Les 6 phases

| Phase | En une phrase | Pourquoi | Statut |
|---|---|---|---|
| **0 — POC** | Vérifier que Quantower/Rithmic peut nous donner ce qu'il faut | ne rien supposer : mesurer profondeur d'historique, aggressor, volume | ✅ **fait** |
| **1 — Extracteur** | Aspirer les ticks NQ dans une base de données | construire notre « source de vérité » locale (Rithmic n'garde que ~20 j) | ✅ **fait** |
| **2 — Backtests** | Rejouer les 8 algos sur les données NQ | voir comment ils se comportent sur NQ vs Bitcoin | ⬅️ **prochaine** |
| **3 — Indicateurs** | Afficher Volume Profile / delta / signaux **sur le graphe NQ** | tes indicateurs visuels habituels + vérifier que nos calculs = ceux de Python | ⏳ |
| **4 — Shadow mode** | Les algos tournent en live mais **notent** leurs signaux sans trader | prouver qu'en réel ils décident comme au backtest — **zéro risque** | ⏳ |
| **5 — Ordres réels** | Un algo passe de vrais ordres sur le challenge prop firm | avec garde-fous d'abord (perte max, kill switch) | ⏳ |

Règle d'or : **pas d'ordres réels (Phase 5) tant que les indicateurs (3) et le shadow mode (4)
ne sont pas validés.**

## Les 8 stratégies de trading (à porter, PAS à réinventer)

Elles existent déjà dans `../Backtesting/backtests/algorithms/`. Verdict du frère sur 6 mois de
Bitcoin baissier : **aucune ne gagne** ; seule la n°7 limite les dégâts. Le but ici n'est pas de
les rendre gagnantes, mais de les **porter fidèlement** sur NQ et de construire la chaîne live.

| # | Nom | Idée en une ligne |
|---|---|---|
| 1 | Buy & Hold | acheter et garder — la référence (« sol de vérité ») |
| 2 | Croisement SMA 50/200 | acheter quand la moyenne courte passe au-dessus de la longue |
| 3 | MACD 12/26/9 | suivre le momentum via le croisement MACD |
| 4 | RSI 14 (retour à la moyenne) | acheter la faiblesse, vendre la force (contre-tendance) |
| 5 | Bollinger 20/2σ | jouer les extrêmes de volatilité (bandes) |
| 6 | RSI + stop/take | comme n°4 mais avec stop-loss et take-profit |
| 7 | **Stratégie avancée** | régime (SMA 200) + MACD confirmé par RSI + sizing selon volatilité + stop suiveur — **la seule qui contrôle la casse** |
| 8 | Volume Profile | jouer les cassures/rejets des zones de volume par session + filtre delta |

Ces 8 algos apparaissent d'abord en **Phase 2** (backtests sur NQ), puis en **Phase 4** (shadow
mode live), et enfin **une seule** (la n°7) en **Phase 5** (vrais ordres).

## Où on en est

Phases **0 et 1 terminées** : la faisabilité est prouvée et **6,93 M de ticks NQ** sont déjà en
base, au format exact du projet frère. Prochaine étape logique : **Phase 2**, brancher les 8 algos
sur ces données NQ. (En parallèle, penser à relancer l'extracteur chaque jour pour accumuler
l'historique.)
