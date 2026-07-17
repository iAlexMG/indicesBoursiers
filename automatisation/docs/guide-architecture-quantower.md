# Guide formatif — Indicateur vs Stratégie, Visual Studio vs dotnet

> Écrit le 2026-07-08 pour lever une confusion légitime : sur les projets précédents tu
> développais des **indicateurs** en C# dans **Visual Studio** ; ici j'ai créé des
> **stratégies** compilées en **ligne de commande**. Rien de nouveau ni d'exotique — c'est le
> même système de plugins Quantower. Voici pourquoi, et comment tout se recoupe.

---

## 1. Le vrai piège : « stratégie » veut dire DEUX choses

| Terme | Sens | Exemple ici |
|---|---|---|
| **Stratégie Quantower** | un **type de code** (une brique logicielle chargée par Quantower) | `NQ-ES History Ticks`, `Phase0 Measure` |
| **Stratégie de trading** | un **algorithme d'achat/vente** (les 8 du projet frère) | croisement SMA 50/200, MACD, RSI… |

Quand je dis « j'ai fait une stratégie », je parle du **type de code** (brique Quantower), pas
d'un algo qui trade. Mes deux briques actuelles **n'envoient aucun ordre** : elles mesurent et
extraient des données. Les 8 **stratégies de trading**, elles, n'arriveront qu'aux Phases 4–5.

---

## 2. Les types de code Quantower (tous = une DLL `.dll` dans `Settings\Scripts\`)

Quantower charge du C# compilé, rangé par **type**, dans `C:\Quantower\Settings\Scripts\` :

| Type | Sert à | Cycle de vie | Où ça tourne | Ton passé |
|---|---|---|---|---|
| **Indicator** (indicateur) | **afficher/dessiner** sur un graphe, calculer une valeur par bougie | `OnInit`, `OnUpdate` (par barre), `OnPaintChart` (dessin) | **attaché à un graphe** | ✅ c'est ce que tu faisais |
| **Strategy** (stratégie) | **logique autonome** : lire l'historique, les comptes, passer des ordres | `OnRun`, `OnStop` | **panneau Strategies**, sans graphe | ⬅️ ce que j'ai fait Phases 0–1 |
| plug-in / vendor / … | panneaux, connecteurs… | — | — | (hors sujet ici) |

**Un indicateur est fait pour VOIR** (il vit sur un graphe et dessine). **Une stratégie est faite
pour AGIR/CALCULER en tâche de fond** (elle a accès à tout : connexions, symboles, historique,
comptes, ordres ; elle n'a pas besoin de graphe).

---

## 3. Pourquoi Strategy (et pas Indicator) pour les Phases 0 et 1 ?

Parce que ces deux phases **ne dessinent rien** :

- **Phase 0** = *mesurer* (profondeur d'historique, % d'aggressor, volume/jour). Résultat : un
  fichier texte. Aucun graphe.
- **Phase 1** = *extraire* les ticks et les écrire dans une base SQLite. Aucun graphe.

Un **indicateur** aurait été le mauvais outil (il est lié à un graphe et pensé pour l'affichage).
Une **stratégie** est exactement l'outil : elle tourne en tâche de fond, lit l'historique via
Rithmic et écrit sur le disque.

> **Tes indicateurs reviennent en Phase 3** — c'est écrit dans le plan : « Indicateurs custom
> affichés dans Quantower » (Volume Profile par session, delta, marqueurs de signaux **sur le
> graphe NQ**). Là, on retrouve exactement ton workflow habituel : indicateur + affichage visuel.
> Autrement dit, on n'a pas abandonné les indicateurs — on n'y est simplement **pas encore**.

---

## 4. Visual Studio 2026 vs `dotnet build` : le MÊME compilateur

C'est **deux outils pour compiler le même projet C#**, avec **le même résultat** :

| | Visual Studio 2026 | `dotnet build` (ce que j'utilise) |
|---|---|---|
| Nature | IDE graphique (fenêtres, boutons) | ligne de commande |
| Compilateur sous le capot | **Roslyn** | **Roslyn** (identique) |
| Résultat | une DLL `net8.0` | **la même** DLL `net8.0` |
| Fichier projet | `.csproj` | **le même** `.csproj` |

Je compile en ligne de commande **parce que je suis un agent dans un terminal** : je ne peux pas
cliquer dans l'interface de Visual Studio. Mais **rien ne t'empêche d'ouvrir mes projets dans
Visual Studio 2026** — double-clic sur le `.csproj` (ex. `extractor\NqExtractor\NqExtractor.csproj`),
puis Générer. Tu obtiendras la DLL identique. Un seul détail : mes `.csproj` reçoivent le chemin
de Quantower via une variable (`QuantowerBin`) que mon script `deploy.ps1` remplit ; dans Visual
Studio, soit tu remplaces la référence par le chemin en dur de ta version, soit tu ouvres la
solution comme les exemples officiels Quantower (mêmes références).

**Déploiement** (identique quel que soit l'outil) : copier la DLL produite dans
`C:\Quantower\Settings\Scripts\Strategies\<nom>\` (ou `\Indicators\<nom>\` pour un indicateur),
puis (re)démarrer Quantower. Mes `deploy.ps1` font exactement ça automatiquement.

---

## 5. Récapitulatif : quel type de code, quel outil, par phase

| Phase | Ce qu'on construit | Type Quantower | Visuel ? | Outil de compil |
|---|---|---|---|---|
| 0 POC | mesures de faisabilité | **Strategy** | non | dotnet (ou VS) |
| 1 Extracteur | ticks NQ → SQLite | **Strategy** | non | dotnet (ou VS) |
| 2 Backtests | rejouer les 8 algos (hors Quantower, moteur LEAN) | *(Python, pas Quantower)* | — | — |
| 3 Indicateurs | VP session, delta, signaux **sur le graphe NQ** | **Indicator** ⭐ | **oui** | VS ou dotnet |
| 4 Shadow | les 8 algos journalisent leurs signaux (zéro ordre) | **Strategy** | non | VS ou dotnet |
| 5 Ordres | exécution réelle avec garde-fous | **Strategy** | non | VS ou dotnet |

En clair : **Strategy = tâches de fond & trading** ; **Indicator = affichage sur graphe**. Les
deux sont du C# Quantower ordinaire ; Visual Studio et `dotnet` sont interchangeables.
